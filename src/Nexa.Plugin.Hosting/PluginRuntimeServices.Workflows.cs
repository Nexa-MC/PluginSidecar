using System.Collections.Immutable;
using System.Security.Cryptography;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginRuntimeServices
{
    private sealed class FlowReturn(AbiValue value) : Exception { internal AbiValue Value=value; }
    private sealed class Run(WorkflowDefinition definition,WorkflowRequest request)
    {
        internal Operation? Operation;internal WorkflowDefinition Definition=definition;internal WorkflowRequest Request=request;
        internal List<WorkflowStep> Steps=[];internal Dictionary<string,AbiValue> Outcomes=[];internal HashSet<string> PendingEffects=[],Returns=[];internal EntityId Id=null!;internal long Work,Bytes;
    }
    private readonly Dictionary<EntityId,Run> _runs=[];
    private long _workflowBytes;
    private void SaveOutcome(Run run,string path,AbiValue value)
    {
        long delta=AbiValueBinary.Encode(value).Length-(run.Outcomes.TryGetValue(path,out var previous)?AbiValueBinary.Encode(previous).Length:0);
        if(run.Bytes+delta>2*1024*1024||_workflowBytes+delta>32*1024*1024)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        run.Bytes+=delta;_workflowBytes+=delta;run.Outcomes[path]=value;
    }
    private void ForgetRun(EntityId id){if(_runs.Remove(id,out var run))_workflowBytes-=run.Bytes;}
    private IEnumerable<HostCapabilityRoute> WorkflowRoutes => [
        HostRoutes.Typed<WorkflowDefinition,RegistrationRef>("Workflows","Register",(c,v)=>
        {var report=ValidateWorkflow(v);if(!report.Valid)throw new AbiHostException(report.Issues[0].Code);return ValueTask.FromResult(Stage(c,"workflow",v.Id,v));}),
        HostRoutes.Typed<WorkflowDefinition,ValidationReport>("Workflows","Validate",(c,v)=>ValueTask.FromResult(ValidateWorkflow(v))),
        HostRoutes.Typed<WorkflowRequest,ValidationReport>("Workflows","ExplainPlan",(c,v)=>{ResolveWorkflow(c,v);return ValueTask.FromResult(Validation(true));}),
        HostRoutes.Typed<WorkflowRequest,MutationPlan>("Workflows","Preview",(c,v)=>
        {var definition=ResolveWorkflow(c,v);return ValueTask.FromResult(new MutationPlan(new(Guid.NewGuid().ToString("N")),_revision,Convert.ToHexString(SHA256.HashData(AbiValueBinary.Encode(WorkflowRequest.Write(v)))),Text(definition.Id.Value),ConfirmationRequirement.None,[]));}),
        HostRoutes.Typed<WorkflowRequest,OperationRef>("Workflows","Start",(c,v)=>
        {
            var definition=ResolveWorkflow(c,v);var run=new Run(definition,v);
            return ValueTask.FromResult(StartOperation(c,definition.Output,async token=>
            {try { var result=await ExecuteNodes(c with {Options=c.Options with {Cancellation=token}},run,definition.Nodes,v.Inputs,0,token,"root").ConfigureAwait(false);return AbiReply<AbiValue>.Ok(result); } catch(FlowReturn result) { return AbiReply<AbiValue>.Ok(result.Value); }},
                operation=>{lock(_gate){run.Operation=operation;run.Id=operation.Record.Id;_runs.Add(run.Id,run);PersistRun(operation);}}));
        }),
        HostRoutes.Typed<WorkflowRunRequest,OperationRef>("Workflows","Resume",(c,v)=>ValueTask.FromResult(Resume(c,v))),
        HostRoutes.Typed<WorkflowStepRequest,OperationRef>("Workflows","RetryStep",(c,v)=>
        {lock(_gate){var run=FindRun(c,v.Run);if(string.IsNullOrWhiteSpace(v.IdempotencyKey)||!run.Steps.Any(s=>s.Id==v.StepId&&s.State==OperationState.Failed))throw new AbiHostException(AbiErrors.CONFLICT);return ValueTask.FromResult(Resume(c,new(v.Run,v.ExpectedRevision,run.Definition.DefinitionDigest)));}}),
        HostRoutes.Typed<EntityRequest,OperationRecord>("Workflows","Read",(c,v)=>{FindRun(c,v.Id);return ValueTask.FromResult(GetOperation(c,v.Id).Record);}),
        HostRoutes.Typed<EntityRequest,WorkflowTrace>("Workflows","ReadRunTrace",(c,v)=>
        {lock(_gate){var run=FindRun(c,v.Id);return ValueTask.FromResult(new WorkflowTrace(v.Id,run.Definition.DefinitionDigest,run.Steps.ToImmutableArray()));}}),
        HostRoutes.Typed<OperationRequest,OperationRef>("Workflows","Cancel",(c,v)=>{FindRun(c,v.Operation);return ValueTask.FromResult(Cancel(c,v));}),
        HostRoutes.Typed<PageRequest,Page<OperationRecord>>("Workflows","ListRuns",(c,v)=>
        {lock(_gate){RestoreOwnRuns(c);return ValueTask.FromResult(Page(c,v,"runs",_operations.Values.Where(o=>o.Owner==c.Identity&&_runs.ContainsKey(o.Record.Id)).OrderBy(o=>o.Record.Id.Value,StringComparer.Ordinal).Select(o=>o.Record)));}}),
        HostRoutes.Typed<WatchRequest,SubscriptionRef>("Workflows","Watch",(c,v)=>
        {if(v.Schema!=Abi100Schemas.OperationRecord)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);FindRun(c,v.Target);return ValueTask.FromResult(Subscribe(c,"operation",v.Target.Value) with {ResyncRequired=v.Cursor is not null});})
    ];
    private Run FindRun(HostInvocation c,EntityId id)
    {lock(_gate){GetOperation(c,id);return _runs.GetValueOrDefault(id)??throw new AbiHostException(AbiErrors.HANDLE_TYPE_MISMATCH);}}
    private WorkflowDefinition ResolveWorkflow(HostInvocation c,WorkflowRequest request)
    {
        var definition=WorkflowDefinition.Read(Find(c,"workflow",request.Workflow).Value);
        if(definition.DefinitionDigest!=request.DefinitionDigest)throw new AbiHostException(AbiErrors.PLAN_STALE);
        Validate(definition.Input,request.Inputs);return definition;
    }
    private static ValidationReport ValidateWorkflow(WorkflowDefinition definition)
    {
        if(definition.Concurrency!=ConcurrencyPolicy.Parallel)return Validation(false,AbiErrors.CAPABILITY_UNAVAILABLE);
        if(definition.MaximumDepth is <1 or >32||definition.MaximumWork is <1 or >4096||string.IsNullOrWhiteSpace(definition.DefinitionDigest))return Validation(false);
        int count=0;var ids=new HashSet<string>(StringComparer.Ordinal);
        bool Nodes(ImmutableArray<WorkflowNode> nodes,int depth)
        {
            if(depth>definition.MaximumDepth)return false;
            foreach(var node in nodes)
            {
                if(++count>definition.MaximumWork||!ids.Add(node.Id)||string.IsNullOrWhiteSpace(node.Id)||node.MaximumConcurrency is <1 or >32||node.MaximumAttempts is <1 or >16||node.BackoffMilliseconds is <0 or >300000)return false;
                if(node.Kind is WorkflowNodeKind.AskUser or WorkflowNodeKind.WaitEvent)return false;
                if(node.Kind is WorkflowNodeKind.Action or WorkflowNodeKind.Compensate or WorkflowNodeKind.Subflow or WorkflowNodeKind.If && node.Target is null)return false;
                if(node.Kind is WorkflowNodeKind.Retry && node.Children.Length!=1 ||node.Kind is WorkflowNodeKind.Finally && node.Children.Length!=2 ||node.Kind is WorkflowNodeKind.Try && node.Children.Length is <1 or >2)return false;
                if(!Nodes(node.Children,depth+1))return false;
            }
            return true;
        }
        return Validation(Nodes(definition.Nodes,0));
    }
    private async ValueTask<AbiValue> ExecuteNodes(HostInvocation caller,Run run,ImmutableArray<WorkflowNode> nodes,AbiValue input,int depth,CancellationToken token,string path)
    {
        if(depth>run.Definition.MaximumDepth)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        for(int index=0;index<nodes.Length;index++)input=await ExecuteNode(caller,run,nodes[index],input,depth,token,path+"/"+index).ConfigureAwait(false);
        return input;
    }
    private async ValueTask<AbiValue> ExecuteNode(HostInvocation c,Run run,WorkflowNode node,AbiValue previous,int depth,CancellationToken token,string path)
    {
        if(depth>run.Definition.MaximumDepth)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        token.ThrowIfCancellationRequested();
        lock(_gate)if(run.Outcomes.TryGetValue(path,out var completed)){if(node.Kind==WorkflowNodeKind.Return||run.Returns.Contains(path))throw new FlowReturn(completed);return completed;}
        if(Interlocked.Increment(ref run.Work)>run.Definition.MaximumWork)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        AbiValue input=node.Inputs is AbiValue.Null?previous:node.Inputs;
        Validate(node.InputSchema,input);
        int trace;
        lock(_gate){if(run.Steps.Count>=4096)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);trace=run.Steps.Count;run.Steps.Add(new(node.Id,OperationState.Running,[],null));}
        try
        {
            AbiValue output=await Execute().ConfigureAwait(false);Validate(node.OutputSchema,output);
            lock(_gate){run.Steps[trace]=run.Steps[trace] with {State=OperationState.Completed};SaveOutcome(run,path,output);run.PendingEffects.Remove(path);PersistRun(run.Operation!);}return output;
        }
        catch(FlowReturn result) {Validate(node.OutputSchema,result.Value);lock(_gate){run.Steps[trace]=run.Steps[trace] with {State=OperationState.Completed};SaveOutcome(run,path,result.Value);run.Returns.Add(path);run.PendingEffects.Remove(path);PersistRun(run.Operation!);}throw;}
        catch(Exception e) when(e is not OutOfMemoryException and not AccessViolationException)
        {lock(_gate)run.Steps[trace]=run.Steps[trace] with {State=e is OperationCanceledException?OperationState.Cancelled:OperationState.Failed,Failure=new(e is AbiHostException known?known.Code:AbiErrors.PLUGIN_EXCEPTION,Text("workflow.step.failed"),c.Options.CorrelationId,false,[])};throw;}
        async ValueTask<AbiValue> Execute()
        {
            switch(node.Kind)
            {
                case WorkflowNodeKind.Return:throw new FlowReturn(input);
                case WorkflowNodeKind.Fail:throw new AbiHostException(input is AbiValue.Utf8String code?code.Value:AbiErrors.PLUGIN_EXCEPTION);
                case WorkflowNodeKind.Checkpoint:lock(_gate)PersistRun(run.Operation!);return input;
                case WorkflowNodeKind.Sequence:return await Children(node.Children,input);
                case WorkflowNodeKind.Delay:await Task.Delay(TimeSpan.FromMilliseconds(node.BackoffMilliseconds),token).ConfigureAwait(false);return input;
                case WorkflowNodeKind.Action:
                case WorkflowNodeKind.Compensate:
                    var entry=Find(c,"action",node.Target!);var action=ActionDescriptor.Read(entry.Value);
                    var request=new ActionRequest(node.Target!,new(new("nexa:workflow"),run.Id,c.Identity.PluginId),input,null,run.Id.Value+"/"+path);
                    AdmitAction(c,action,request);
                    if(action.Idempotency!=IdempotencyMode.ReadOnly)lock(_gate){run.PendingEffects.Add(path);PersistRun(run.Operation!);}
                    var reply=await InvokeAction(c,entry,action,request,token).ConfigureAwait(false);
                    if(!reply.Success)throw new AbiHostException(reply.Error!.Code);return reply.Value;
                case WorkflowNodeKind.If:
                    var condition=await EvaluatePredicate(c,new(node.Target!,input,new(run.Definition.DefinitionDigest))).ConfigureAwait(false);
                    int branch=condition.Outcome switch{PredicateOutcome.True=>0,PredicateOutcome.False=>1,_=>throw new AbiHostException(AbiErrors.CONFLICT)};
                    return branch<node.Children.Length?await ExecuteNode(c,run,node.Children[branch],input,depth+1,token,path+"/"+branch):input;
                case WorkflowNodeKind.Switch:
                    string choice=DataCodec.ReadString(input);var selected=node.Children.FirstOrDefault(n=>n.Id==choice)??node.Children.FirstOrDefault(n=>n.Id=="default");
                    return selected is null?input:await ExecuteNode(c,run,selected,input,depth+1,token,path+"/switch/"+selected.Id);
                case WorkflowNodeKind.ForEach:
                    var items=DataCodec.ReadArray(input,(v,_)=>v,0);var results=ImmutableArray.CreateBuilder<AbiValue>();
                    for(int index=0;index<items.Length;index++)results.Add(await ExecuteNodes(c,run,node.Children,items[index],depth+1,token,path+"/each/"+index));return new AbiValue.Array(results.ToImmutable());
                case WorkflowNodeKind.Parallel:
                    using(var concurrency=new SemaphoreSlim((int)node.MaximumConcurrency))
                    using(var branches=CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        var jobs=node.Children.Select(async (child,index)=>{await concurrency.WaitAsync(branches.Token).ConfigureAwait(false);try{try{return await ExecuteNode(c,run,child,input,depth+1,branches.Token,path+"/parallel/"+index);}catch(FlowReturn returned){return returned.Value;}}finally{concurrency.Release();}}).ToList();
                        if(node.Completion==ParallelCompletion.All)return new AbiValue.Array((await Task.WhenAll(jobs).ConfigureAwait(false)).ToImmutableArray());
                        Exception? last=null;
                        while(jobs.Count>0){var done=await Task.WhenAny(jobs);jobs.Remove(done);try{var result=await done;branches.Cancel();try{await Task.WhenAll(jobs);}catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException){}return result;}catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException){last=e;}}
                        throw last??new AbiHostException(AbiErrors.NOT_FOUND);
                    }
                case WorkflowNodeKind.Retry:
                    for(int attempt=1;;attempt++)try{return await Children(node.Children,input);}catch(AbiHostException error)when(attempt<node.MaximumAttempts&&node.RetryableErrors.Contains(error.Code)){await Task.Delay(TimeSpan.FromMilliseconds(node.BackoffMilliseconds),token);}
                case WorkflowNodeKind.Try:
                    try{return await ExecuteNode(c,run,node.Children[0],input,depth+1,token,path+"/body");}catch(AbiHostException)when(node.Children.Length==2){return await ExecuteNode(c,run,node.Children[1],input,depth+1,token,path+"/cleanup");}
                case WorkflowNodeKind.Finally:
                    try{return await ExecuteNode(c,run,node.Children[0],input,depth+1,token,path+"/body");}finally{await ExecuteNode(c,run,node.Children[1],input,depth+1,token,path+"/cleanup");}
                case WorkflowNodeKind.Subflow:
                    var sub=WorkflowDefinition.Read(Find(c,"workflow",node.Target!).Value);Validate(sub.Input,input);AbiValue resultValue;try{resultValue=await Children(sub.Nodes,input);}catch(FlowReturn result){resultValue=result.Value;}Validate(sub.Output,resultValue);return resultValue;
                default:throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
            }
        }
        ValueTask<AbiValue> Children(ImmutableArray<WorkflowNode> children,AbiValue value)=>ExecuteNodes(c,run,children,value,depth+1,token,path+"/children");
    }
}
