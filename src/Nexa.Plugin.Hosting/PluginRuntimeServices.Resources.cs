using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginRuntimeServices
{
    private readonly Dictionary<ulong,int> _inUseRegistrations=[];
    private void HoldRegistration(Entry entry)
    {
        lock(_gate)
        {
            if(!_entries.Values.Any(e=>e.Registration.Handle==entry.Registration.Handle))throw new AbiHostException(AbiErrors.HANDLE_EXPIRED);
            _inUseRegistrations[entry.Registration.Handle]=_inUseRegistrations.GetValueOrDefault(entry.Registration.Handle)+1;
        }
    }
    private void DropRegistration(Entry entry)
    {lock(_gate){int count=_inUseRegistrations.GetValueOrDefault(entry.Registration.Handle);if(count<=1)_inUseRegistrations.Remove(entry.Registration.Handle);else _inUseRegistrations[entry.Registration.Handle]=count-1;}}
    private IEnumerable<HostCapabilityRoute> ResourceRoutes => [
        HostRoutes.Typed<CleanupRequest,RegistrationRef>("Resources","RegisterCleanup",(c,v)=>ValueTask.FromResult(RegisterCleanup(c,v.Callback))),
        HostRoutes.Typed<RegistrationRequest,Empty>("Resources","Release",async(c,v)=>
        {
            if(v.Registration.Owner!=c.Identity.PluginId||v.Registration.LoadInstanceId!=c.Identity.LoadInstanceId)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            if(await ReleaseCleanup(c,v.Registration).ConfigureAwait(false))return new();
            lock(_gate)
            {
                if(ReleaseEntry(c,v.Registration)||ContentRegistry!.Release(c,v.Registration))return new();
                throw new AbiHostException(AbiErrors.HANDLE_EXPIRED);
            }
        })
    ];
    private bool ReleaseEntry(HostInvocation caller,RegistrationRef reference)
    {
        var item=_entries.SingleOrDefault(e=>e.Value.Registration.Handle==reference.Handle);
        if(item.Value is null)return false;
        AtomicDefinitionRegistry.CheckReference(caller,reference,item.Value.Registration);
        var id=item.Key.Item2;string kind=item.Key.Item1;
        // A release cannot invalidate a live binding or scheduled/running workflow.
        if(_inUseRegistrations.ContainsKey(reference.Handle)||_bindings.Values.Any(b=>b.Provider.Registration.Handle==reference.Handle)||
            _subscriptions.Values.Any(s=>s.Key==id.Value&&s.Kind==kind&&kind is "event" or "state")||
            kind=="event"&&_eventWaiters.Values.Any(w=>w.Event==id)||
            _schedules.Values.Any(s=>kind=="workflow"&&s.Definition.Workflow==id||kind=="event"&&s.Definition.Event==id)||
            _entries.Any(e=>e.Value.Registration.Handle!=reference.Handle&&References(e.Key.Item1,e.Value,kind,id))||
            _runs.Values.Any(r=>r.Admitted&&(kind=="workflow"&&r.Definition.Id==id||NodesReference(r.Definition.Nodes,kind,id))))throw new AbiHostException(AbiErrors.CONFLICT);
        _entries.Remove(item.Key);_bytes-=item.Value.Bytes;
        if(kind=="schedule"&&_schedules.Remove(id,out var schedule))SetScheduleInput(schedule,null);
        if(kind=="state")foreach(var key in _states.Keys.Where(k=>k.Item1==id).ToArray()){_stateBytes-=AbiValueBinary.Encode(StateSnapshot.Write(_states[key])).Length;_states.Remove(key);}
        _revision=NewRevision();RefreshAvailability();return true;
    }
    private static bool References(string kind,Entry entry,string targetKind,QualifiedId id)=>kind switch
    {
        "service"=>targetKind=="contract"&&ServiceDefinition.Read(entry.Value).Contract.Id==id||targetKind=="predicate"&&ServiceDefinition.Read(entry.Value).Probe==id,
        "provider-kind"=>targetKind=="contract"&&ProviderKindDefinition.Read(entry.Value).Contract.Id==id,
        "consumer"=>targetKind=="contract"&&ConsumerDefinition.Read(entry.Value).Contract==id,
        "workflow"=>NodesReference(WorkflowDefinition.Read(entry.Value).Nodes,targetKind,id),
        _=>false
    };
    private static bool NodesReference(System.Collections.Immutable.ImmutableArray<WorkflowNode> nodes,string kind,QualifiedId id)=>nodes.Any(n=>
        n.Target==id&&(kind switch {"action"=>n.Kind is WorkflowNodeKind.Action or WorkflowNodeKind.Compensate,"predicate"=>n.Kind==WorkflowNodeKind.If,"workflow"=>n.Kind==WorkflowNodeKind.Subflow,"event"=>n.Kind==WorkflowNodeKind.WaitEvent,_=>false})||NodesReference(n.Children,kind,id));
}
