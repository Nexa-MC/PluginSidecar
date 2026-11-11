using System.Collections.Immutable;
using System.Security.Cryptography;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginRuntimeServices
{
    private IEnumerable<HostCapabilityRoute> ActionRoutes => [
        HostRoutes.Typed<ActionDescriptor,RegistrationRef>("Actions","Register",(c,v)=>
        {if(v.Handler is null or 0)throw new AbiHostException(AbiErrors.HANDLE_INVALID);return ValueTask.FromResult(Stage(c,"action",v.Id,v));}),
        HostRoutes.Typed<QualifiedRequest,ActionDescriptor>("Actions","Describe",(c,v)=>ValueTask.FromResult(ActionDescriptor.Read(Find(c,"action",v.Id).Value))),
        HostRoutes.Typed<ActionRequest,OperationRef>("Actions","Invoke",(c,v)=>
        {var entry=Find(c,"action",v.Action);var action=ActionDescriptor.Read(entry.Value);AdmitAction(c,action,v);return ValueTask.FromResult(StartOperation(c,action.Output,token=>InvokeAction(c,entry,action,v,token)));}),
        HostRoutes.Typed<PredicateDefinition,RegistrationRef>("Predicates","Register",(c,v)=>
        {if(v.Handler==0)throw new AbiHostException(AbiErrors.HANDLE_INVALID);if(v.Handler is null)Expression(v.Expression,new AbiValue.Null(),0,true);return ValueTask.FromResult(Stage(c,"predicate",v.Id,v));}),
        HostRoutes.Typed<QualifiedRequest,PredicateDefinition>("Predicates","Describe",(c,v)=>ValueTask.FromResult(PredicateDefinition.Read(Find(c,"predicate",v.Id).Value))),
        HostRoutes.Typed<PredicateRequest,PredicateResult>("Predicates","Evaluate",EvaluatePredicate)
    ];
    private void AdmitAction(HostInvocation c,ActionDescriptor action,ActionRequest request)
    {
        Validate(action.Input,request.Inputs);
        if(!_capabilities(c,action.RequiredCapabilities)||action.Scopes.Length!=0&&!action.Scopes.Contains(request.Target.Scope))throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
        if(action.Confirmation!=ConfirmationRequirement.None)throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        if(action.Idempotency==IdempotencyMode.RequiredKey&&string.IsNullOrWhiteSpace(request.IdempotencyKey))throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
    }
    private readonly Dictionary<(Guid, QualifiedId, string), (string Digest, TaskCompletionSource<AbiReply<AbiValue>> Completion)> _actionMemo=[];
    private async ValueTask<AbiReply<AbiValue>> InvokeAction(HostInvocation c,Entry entry,ActionDescriptor action,ActionRequest request,CancellationToken token)
    {
        TaskCompletionSource<AbiReply<AbiValue>>? completion=null;
        Task<AbiReply<AbiValue>>? replay=null;
        if(action.Idempotency!=IdempotencyMode.ReadOnly)
        {
            string digest=Convert.ToHexString(SHA256.HashData(AbiValueBinary.Encode(ActionRequest.Write(request))));
            var key=(c.Identity.LoadInstanceId,action.Id,request.IdempotencyKey);
            lock(_gate)
            {
                if(_actionMemo.TryGetValue(key,out var prior))
                {if(prior.Digest!=digest)throw new AbiHostException(AbiErrors.REQUEST_ID_REUSED);replay=prior.Completion.Task;}
                else
                {
                    if(_actionMemo.Count>=1024)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
                    completion=new(TaskCreationOptions.RunContinuationsAsynchronously);_actionMemo.Add(key,(digest,completion));
                }
            }
        }
        if(replay is not null)return await replay.WaitAsync(token).ConfigureAwait(false);
        AbiReply<AbiValue> reply;
        try
        {
            reply=await _dispatch(entry.Owner,action.Handler!.Value,ActionRequest.Write(request),c.Options with
            {OperationId=Guid.NewGuid(),CausationId=c.Options.OperationId,Cancellation=token,IdempotencyKey=request.IdempotencyKey}).ConfigureAwait(false);
            if(reply.Success){Validate(action.Output,reply.Value);if(AbiValueBinary.Encode(reply.Value).Length>65536)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);}
        }
        catch(Exception error) when(error is not OutOfMemoryException and not AccessViolationException)
        {
            string code=error is AbiHostException known?known.Code:error is OperationCanceledException?AbiErrors.OUTCOME_UNKNOWN:AbiErrors.PLUGIN_EXCEPTION;
            reply=AbiReply<AbiValue>.Fail(new(code,code,"Active","action.invoke",c.Identity.PluginId,c.Identity.LoadInstanceId,c.Options.CorrelationId,false,ImmutableDictionary<string,string>.Empty));
        }
        completion?.TrySetResult(reply);return reply;
    }
    private async ValueTask<PredicateResult> EvaluatePredicate(HostInvocation c,PredicateRequest request)
    {
        var entry=Find(c,"predicate",request.Id);var predicate=PredicateDefinition.Read(entry.Value);Validate(predicate.Input,request.Inputs);
        if(predicate.Handler is null)
            return new(DataCodec.ReadBoolean(Expression(predicate.Expression,request.Inputs,0,false))?PredicateOutcome.True:PredicateOutcome.False,request.Consistency,[]);
        // Runtime callback effects are not available before atomic activation.
        if(c.Phase!="Active")throw new AbiHostException(AbiErrors.SERVICE_NOT_READY);
        var reply=await _dispatch(entry.Owner,predicate.Handler.Value,PredicateRequest.Write(request),c.Options).ConfigureAwait(false);
        if(!reply.Success)throw new AbiHostException(reply.Error!.Code);
        var result=PredicateResult.Read(reply.Value);
        if(result.InputRevision!=request.Consistency)throw new AbiHostException(AbiErrors.STALE_REVISION);
        return result;
    }
    private static AbiValue Expression(AbiValue expression,AbiValue input,int depth,bool validateOnly)
    {
        if(depth>32)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        if(expression is not AbiValue.Map map)return expression;
        string op=DataCodec.ReadString(DataCodec.Field(map.Value,"op"));
        var args=map.Value.TryGetValue("args",out var value)?DataCodec.ReadArray(value,(v,_)=>v,0):[];
        if(args.Length>256)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        AbiValue Eval(int i)=>Expression(args[i],input,depth+1,validateOnly);
        switch(op)
        {
            case "input":
                string field=DataCodec.ReadString(DataCodec.Field(map.Value,"field"));
                return validateOnly?new AbiValue.Null():DataCodec.Field(DataCodec.ReadMap(input),field);
            case "not" when args.Length==1: {var a=Eval(0);return new AbiValue.Bool(validateOnly||!DataCodec.ReadBoolean(a));}
            case "eq" when args.Length==2: {var a=Eval(0);var b=Eval(1);return new AbiValue.Bool(validateOnly||AbiValueBinary.Encode(a).SequenceEqual(AbiValueBinary.Encode(b)));}
            case "all" or "any":
                var children=args.Select(a=>Expression(a,input,depth+1,validateOnly)).ToArray();
                return new AbiValue.Bool(validateOnly||(op=="all"?children.All(DataCodec.ReadBoolean):children.Any(DataCodec.ReadBoolean)));
            default:throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        }
    }
}
