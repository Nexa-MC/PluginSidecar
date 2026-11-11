using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginRuntimeServices
{
    private sealed class Operation(PluginIdentity owner,OperationRecord record,bool cancellable,CancellationTokenSource lifetime)
    {
        internal PluginIdentity Owner=owner;
        internal OperationRecord Record=record;
        internal bool Cancellable=cancellable;
        internal CancellationTokenSource Lifetime=lifetime;
        internal bool Running;internal Task? Worker;
    }
    private readonly Dictionary<EntityId,Operation> _operations=[];
    private Operation CreateOperation(HostInvocation c,SchemaRef output,bool cancellable)
    {
        lock(_gate)
        {
            if(_operations.Count>=256) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var id=new EntityId(Guid.NewGuid().ToString("N"));
            var operation=new Operation(c.Identity,new(id,OperationState.Pending,0,"pending",output,new AbiValue.Null(),null,NewRevision()),cancellable,
                CancellationTokenSource.CreateLinkedTokenSource(Lifetime(c.Identity)));
            _operations.Add(id,operation);_revision=NewRevision();return operation;
        }
    }
    private Operation GetOperation(HostInvocation c,EntityId id)
    {
        lock(_gate)
        {
            if(!_operations.TryGetValue(id,out var operation)) operation=RestoreRun(c,id);
            if(operation.Owner!=c.Identity) throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            return operation;
        }
    }
    private static bool Terminal(OperationState state)=>state is OperationState.Completed or OperationState.Failed or OperationState.Cancelled or OperationState.OutcomeUnknown;
    private void Change(Operation operation,OperationRecord record)
    {
        if(AbiValueBinary.Encode(OperationRecord.Write(record)).Length>262144)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        lock(_gate) {
            if(Terminal(record.State)&&_runs.TryGetValue(record.Id,out var run)&&run.PendingEffects.Count>0)
                record=record with {State=OperationState.OutcomeUnknown,Failure=new(AbiErrors.OUTCOME_UNKNOWN,Text("workflow.effect.unknown"),Guid.NewGuid(),false,[])};
            operation.Record=record with {Revision=NewRevision()};_revision=NewRevision();Notify("operation",record.Id.Value,OperationRecord.Write(operation.Record),operation.Owner.LoadInstanceId);PersistRun(operation); }
    }
    private void RetireOperations(Guid owner)
    {
        foreach(var op in _operations.Values.Where(o=>o.Owner.LoadInstanceId==owner).ToArray())
        { op.Lifetime.Cancel(); _operations.Remove(op.Record.Id); ForgetRun(op.Record.Id); if(!op.Running)op.Lifetime.Dispose(); }
    }
    private OperationRef StartOperation(HostInvocation c,SchemaRef schema,Func<CancellationToken,ValueTask<AbiReply<AbiValue>>> execute, Action<Operation>? initialize = null)
    {
        var operation=CreateOperation(c,schema,true);
        try {initialize?.Invoke(operation);}catch{lock(_gate){_operations.Remove(operation.Record.Id);ForgetRun(operation.Record.Id);}operation.Lifetime.Dispose();throw;}
        RunOperation(c,operation,execute);
        return new(operation.Record.Id,OperationState.Pending);
    }
    private void RunOperation(HostInvocation c,Operation operation,Func<CancellationToken,ValueTask<AbiReply<AbiValue>>> execute)
    {
        operation.Running=true;operation.Lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        operation.Worker=Task.Run(async()=>
        {
            try
            {
                Change(operation,operation.Record with {State=OperationState.Running,Stage="running"});
                var result=await execute(operation.Lifetime.Token).ConfigureAwait(false);
                if(result.Success) { Validate(operation.Record.ResultSchema,result.Value); Change(operation,operation.Record with {State=OperationState.Completed,Progress=1,Stage="completed",Result=result.Value}); }
                else Change(operation,operation.Record with {State=result.Error!.Code==AbiErrors.CANCELLED?OperationState.Cancelled:OperationState.Failed,Stage="failed",
                    Failure=new(result.Error.Code,Text(result.Error.MessageKey),result.Error.CorrelationId,result.Error.Retryable,[])});
            }
            catch(OperationCanceledException) { Change(operation,operation.Record with {State=OperationState.Cancelled,Stage="cancelled"}); }
            catch(Exception e) when(e is not OutOfMemoryException and not AccessViolationException)
            { var code=e is AbiHostException known?known.Code:AbiErrors.PLUGIN_EXCEPTION;Change(operation,operation.Record with {State=OperationState.Failed,Stage="failed",Failure=new(code,Text(code),c.Options.CorrelationId,false,[])}); }
            finally { lock(_gate) { operation.Running=false; if(!_operations.ContainsKey(operation.Record.Id))operation.Lifetime.Dispose(); } }
        });
        _=ObserveOperation(operation);
    }
    private async Task ObserveOperation(Operation operation)
    {
        try{await operation.Worker!.ConfigureAwait(false);}
        catch(Exception error) when(error is not OutOfMemoryException and not AccessViolationException)
        {lock(_gate)operation.Record=operation.Record with {State=OperationState.Failed,Failure=new(AbiErrors.PROVIDER_UNAVAILABLE,Text("workflow.persistence.failed"),Guid.NewGuid(),false,[]),Revision=NewRevision()};}
    }
    private OperationRef Cancel(HostInvocation c,OperationRequest v)
    {
        lock(_gate)
        {
            var op=GetOperation(c,v.Operation);
            if(!Terminal(op.Record.State))
            {
                if(!op.Cancellable) throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
                Change(op,op.Record with {State=op.Running?OperationState.CancellationRequested:OperationState.Cancelled});op.Lifetime.Cancel();
            }
            return new(op.Record.Id,op.Record.State);
        }
    }
    private IEnumerable<HostCapabilityRoute> TaskRoutes => [
        HostRoutes.Typed<TaskCreate,OperationRef>("Tasks","CreateOwned",(c,v)=>{Own(c,v.Id);var op=CreateOperation(c,Abi100Schemas.Empty,v.Cancellable);return ValueTask.FromResult(new OperationRef(op.Record.Id,op.Record.State));}),
        HostRoutes.Typed<TaskProgress,Empty>("Tasks","Report",(c,v)=>
        { lock(_gate) {var op=GetOperation(c,v.Task);CheckMutable(op,v.ExpectedRevision);if(v.Progress is <0 or >1)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);Change(op,op.Record with {State=OperationState.Running,Progress=v.Progress,Stage=v.Stage});return ValueTask.FromResult(new Empty());} }),
        HostRoutes.Typed<TaskCompletion,Empty>("Tasks","Complete",(c,v)=>
        { lock(_gate) {var op=GetOperation(c,v.Task);CheckMutable(op,v.ExpectedRevision);Validate(v.Schema,v.Result);Change(op,op.Record with {State=OperationState.Completed,Progress=1,Result=v.Result,ResultSchema=v.Schema});return ValueTask.FromResult(new Empty());} }),
        HostRoutes.Typed<TaskFailure,Empty>("Tasks","Fail",(c,v)=>
        { lock(_gate) {var op=GetOperation(c,v.Task);CheckMutable(op,v.ExpectedRevision);Change(op,op.Record with {State=OperationState.Failed,Failure=v.Error});return ValueTask.FromResult(new Empty());} }),
        HostRoutes.Typed<PageRequest,Page<OperationRecord>>("Tasks","List",(c,v)=>
        {lock(_gate)return ValueTask.FromResult(Page(c,v,"operations",_operations.Values.Where(o=>o.Owner==c.Identity).OrderBy(o=>o.Record.Id.Value,StringComparer.Ordinal).Select(o=>o.Record)));}),
        HostRoutes.Typed<EntityRequest,OperationRecord>("Tasks","Read",(c,v)=>ValueTask.FromResult(GetOperation(c,v.Id).Record)),
        HostRoutes.Typed<OperationRequest,OperationRef>("Tasks","Cancel",(c,v)=>ValueTask.FromResult(Cancel(c,v))),
        HostRoutes.Typed<EntityRequest,Empty>("Tasks","Dismiss",(c,v)=>
        {lock(_gate){var op=GetOperation(c,v.Id);if(!Terminal(op.Record.State)||op.Running)throw new AbiHostException(AbiErrors.CONFLICT);if(_runs.TryGetValue(v.Id,out var run)&&run.Definition.Persistent)File.Delete(JournalPath(c.Identity.PluginId,v.Id));_operations.Remove(v.Id);ForgetRun(v.Id);op.Lifetime.Dispose();_revision=NewRevision();return ValueTask.FromResult(new Empty());}})
    ];
    private static void CheckMutable(Operation op,Revision revision)
    {if(op.Record.Revision!=revision)throw new AbiHostException(AbiErrors.STALE_REVISION);if(Terminal(op.Record.State)||op.Running)throw new AbiHostException(AbiErrors.CONFLICT);}

    private Entry Lookup(HostInvocation c,ServiceLookup v)
    {
        var provider=Find(c,"service",v.Service);var service=ServiceDefinition.Read(provider.Value);
        if(provider.Owner.PluginId!=v.Provider||service.Contract.Version<v.MinimumVersion||service.Contract.Version>v.MaximumVersion)
            throw new AbiHostException(AbiErrors.SERVICE_CONTRACT_MISMATCH);
        return provider;
    }
    private Binding BindingFor(HostInvocation c,ServiceBinding reference)
    {
        lock(_gate)
        {
            if(!_bindings.TryGetValue(reference.Handle,out var binding))throw new AbiHostException(AbiErrors.PROVIDER_GONE);
            if(binding.Owner!=c.Identity||binding.Reference!=reference)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            return binding;
        }
    }
    private IEnumerable<HostCapabilityRoute> ServiceRoutes => [
        HostRoutes.Typed<ServiceDefinition,RegistrationRef>("Plugins","ExportService",(c,v)=>
        {CheckContract(v.Contract);if(v.Handler==0)throw new AbiHostException(AbiErrors.HANDLE_INVALID);return ValueTask.FromResult(Stage(c,"service",v.Id,v));}),
        HostRoutes.Typed<ServiceLookup,PublicContract>("Plugins","DescribeService",(c,v)=>ValueTask.FromResult(ServiceDefinition.Read(Lookup(c,v).Value).Contract)),
        HostRoutes.Typed<ServiceLookup,ServiceBinding>("Plugins","BindService",(c,v)=>
        {lock(_gate){var entry=Lookup(c,v);var service=ServiceDefinition.Read(entry.Value);if(_bindings.Count>=8192)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);var result=new ServiceBinding(++_next,entry.Owner.PluginId,entry.Owner.LoadInstanceId,service.Contract.Id,service.Contract.Version);_bindings.Add(result.Handle,new(c.Identity,result,entry,v.Required));return ValueTask.FromResult(result);}}),
        HostRoutes.Typed<ServiceBinding,Empty>("Plugins","ReleaseBinding",(c,v)=>
        {lock(_gate){BindingFor(c,v);_bindings.Remove(v.Handle);return ValueTask.FromResult(new Empty());}}),
        HostRoutes.Typed<ServiceCall,OperationRef>("Plugins","CallService",(c,v)=>
        {
            var binding=BindingFor(c,v.Binding);var definition=ServiceDefinition.Read(binding.Provider.Value);
            var method=definition.Contract.Methods.SingleOrDefault(m=>m.Id==v.Method)??throw new AbiHostException(AbiErrors.NOT_FOUND);
            if(method.NativeExecution||v.Delegation is not null)throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
            if(v.CallChain.Length>=16||v.CallChain.Contains(binding.Provider.Owner.LoadInstanceId))throw new AbiHostException(AbiErrors.CONFLICT);
            Validate(method.Input,v.Input);
            var input=ServiceCall.Write(v with {CallChain=v.CallChain.Add(c.Identity.LoadInstanceId)});
            return ValueTask.FromResult(StartOperation(c,method.Output,token=>_dispatch(binding.Provider.Owner,definition.Handler,input,
                c.Options with {OperationId=Guid.NewGuid(),CausationId=c.Options.OperationId,Cancellation=token})));
        })
    ];
}
