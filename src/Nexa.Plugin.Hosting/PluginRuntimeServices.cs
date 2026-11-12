using System.Collections.Immutable;
using System.Security.Cryptography;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

/// <summary>Host-owned runtime services. All access enters through CapabilityAuthority.</summary>
public sealed partial class PluginRuntimeServices
{
    private readonly object _gate;
    internal Func<ulong> AllocateRegistrationHandle=null!;
    internal Func<HostInvocation,ulong,RegistrationRef> RegisterCleanup=null!;
    internal Func<HostInvocation,RegistrationRef,ValueTask<bool>> ReleaseCleanup=null!;
    internal Func<HostInvocation,PluginIdentity,ulong,AbiValue,InvocationOptions,ValueTask<AbiReply<AbiValue>>> DispatchProbe=null!;
    internal Func<HostInvocation,PluginIdentity,ulong,AbiValue,InvocationOptions,ValueTask<AbiReply<AbiValue>>> DispatchOwnProbe=null!;
    internal Func<HostInvocation,LoadReport> OwnLoadReport=null!;
    private readonly string _workflowStore;
    private readonly PluginRuntimeOptions _options;
    private readonly Func<PluginIdentity,string,bool> _authorize;
    private readonly Func<PluginIdentity,string,uint,AbiValue,InvocationOptions,ValueTask<AbiReply<AbiValue>>> _invokeAdmitted;
    private readonly Func<HostInvocation, ImmutableArray<string>, bool> _capabilities;
    private readonly Func<SchemaRef, AbiValue, bool> _validate;
    private readonly Func<PluginIdentity, ulong, AbiValue, InvocationOptions, ValueTask<AbiReply<AbiValue>>> _dispatch;
    private readonly Dictionary<(string, QualifiedId), Entry> _entries = [];
    private readonly Dictionary<ulong, Binding> _bindings = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _lifetimes = [];
    private readonly Dictionary<(QualifiedId, string), StateSnapshot> _states = [];
    private readonly Dictionary<Guid, List<DiagnosticReport>> _diagnostics = [];
    private Revision _revision = NewRevision();
    private ulong _next;
    private long _bytes, _stateBytes, _diagnosticBytes;
    private sealed record Entry(PluginIdentity Owner, Guid Group, RegistrationRef Registration, AbiValue Value, int Bytes);
    private sealed record Binding(PluginIdentity Owner, ServiceBinding Reference, Entry Provider, bool Required);
    internal PluginRuntimeServices(object gate, Func<SchemaRef, AbiValue, bool> validate,
        Func<PluginIdentity, ulong, AbiValue, InvocationOptions, ValueTask<AbiReply<AbiValue>>> dispatch,
        Func<HostInvocation, ImmutableArray<string>, bool> capabilities, string workflowStore, PluginRuntimeOptions options,
        Func<PluginIdentity,string,uint,AbiValue,InvocationOptions,ValueTask<AbiReply<AbiValue>>> invokeAdmitted, Func<PluginIdentity,string,bool> authorize)
    { _gate = gate; _validate = validate; _dispatch = dispatch; _capabilities = capabilities; _workflowStore=workflowStore; _options=options; _invokeAdmitted=invokeAdmitted; _authorize=authorize; }
    private static Revision NewRevision() => new(Guid.NewGuid().ToString("N"));
    private static LocalizedText Text(string key) => new(key, []);
    private static ValidationReport Validation(bool valid, string code = "SCHEMA_INVALID") => new(valid, valid ? [] : [new(code, Text(code), "", true)]);
    private void Validate(SchemaRef schema, AbiValue value) { if (!_validate(schema, value)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID); }
    private static void Own(HostInvocation caller, QualifiedId id)
    { if (!id.Value.StartsWith(caller.Identity.PluginId + ":", StringComparison.Ordinal)) throw new AbiHostException(AbiErrors.CAPABILITY_DENIED); }
    private bool Visible(HostInvocation c, Entry e) => e.Registration.State == RegistrationState.Active || e.Group == c.GroupId;
    private Entry Find(HostInvocation c, string kind, QualifiedId id)
    { lock (_gate) return _entries.TryGetValue((kind,id),out var entry) && Visible(c,entry) ? entry : throw new AbiHostException(AbiErrors.NOT_FOUND); }
    private RegistrationRef Stage<T>(HostInvocation c, string kind, QualifiedId id, T value) where T : IAbiData<T>
    {
        Own(c,id); var encoded = T.Write(value); int size = AbiValueBinary.Encode(encoded).Length;
        lock (_gate)
        {
            c.Options.Cancellation.ThrowIfCancellationRequested();
            if (_entries.ContainsKey((kind,id))) throw new AbiHostException(AbiErrors.REGISTRATION_DUPLICATE);
            if (_entries.Count >= 8192 || _bytes + size > 32*1024*1024) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var registration = new RegistrationRef(AllocateRegistrationHandle(),c.Identity.PluginId,c.Identity.LoadInstanceId,id,RegistrationState.Staged);
            _entries.Add((kind,id), new(c.Identity,c.GroupId,registration,encoded,size)); _bytes+=size;
            if (!_lifetimes.ContainsKey(c.Identity.LoadInstanceId)) _lifetimes.Add(c.Identity.LoadInstanceId,new());
            _revision=NewRevision(); return registration;
        }
    }
    private Page<T> Page<T>(HostInvocation c, PageRequest page, string partition, IEnumerable<T> values) where T : IAbiData<T>
    {
        if (page.Limit is <1 or >256) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        int offset=0;
        if (page.Cursor is not null)
        {
            var map=DataCodec.ReadMap(AbiValueBinary.Decode(Convert.FromBase64String(page.Cursor)));
            if (DataCodec.ReadString(DataCodec.Field(map,"scope")) != c.Identity.LoadInstanceId+"/"+partition || Revision.Read(DataCodec.Field(map,"revision"))!=_revision)
                throw new AbiHostException(AbiErrors.STALE_REVISION);
            offset=checked((int)DataCodec.ReadUInt64(DataCodec.Field(map,"offset")));
        }
        var remaining=values.Skip(offset).Take((int)page.Limit+1).ToArray();
        var result=remaining.Take((int)page.Limit).ToImmutableArray();
        string? cursor=remaining.Length<=page.Limit ? null : Convert.ToBase64String(AbiValueBinary.Encode(DataCodec.Map(
            ("scope",DataCodec.WriteString(c.Identity.LoadInstanceId+"/"+partition)),("revision",Revision.Write(_revision)),("offset",DataCodec.WriteUInt64((ulong)(offset+result.Length))))));
        return new(result,cursor,_revision);
    }
    internal ImmutableArray<string> RequiredConsumers(ISet<string> providers)
    {
        lock(_gate)return _bindings.Values.Where(b=>b.Required&&providers.Contains(b.Provider.Owner.PluginId)).Select(b=>b.Owner.PluginId).Distinct().ToImmutableArray();
    }
    internal void ValidateCommit(Guid group)
    {
        ValidateSchedules(group);
        lock (_gate)
        foreach (var entry in _entries.Where(p=>p.Key.Item1=="service" && p.Value.Group==group).Select(p=>p.Value))
        {
            var service=ServiceDefinition.Read(entry.Value);
            if (!_entries.TryGetValue(("contract",service.Contract.Id),out var contract) ||
                contract.Registration.State!=RegistrationState.Active && contract.Group!=group ||
                !AbiValueBinary.Encode(contract.Value).SequenceEqual(AbiValueBinary.Encode(PublicContract.Write(service.Contract))))
                throw new AbiHostException(AbiErrors.SERVICE_CONTRACT_MISMATCH);
            if(service.Probe is { } probe&&(!_entries.TryGetValue(("predicate",probe),out var predicate)||predicate.Owner!=entry.Owner))
                throw new AbiHostException(AbiErrors.DEPENDENCY_MISSING);
        }
    }
    internal void Publish(Guid group)
    {
        lock (_gate)
        {
            foreach(var subscription in _subscriptions.Values.Where(s=>s.Group==group))subscription.Active=true;
            foreach(var pair in _entries.Where(p=>p.Value.Group==group).ToArray())
                _entries[pair.Key]=pair.Value with {Registration=pair.Value.Registration with {State=RegistrationState.Active}};
            _revision=NewRevision();RefreshAvailability();
            foreach(var sub in _subscriptions.Values.Where(s=>s.Group==group&&s.Kind=="definition-watch"))Enqueue(sub,Snapshot<DefinitionBatch>.Write(ContentRegistry!.RegistrySnapshot(new(sub.Key))));
        }
    }
    internal void Abort(Guid group)
    { lock(_gate) foreach(var id in _entries.Values.Where(e=>e.Group==group).Select(e=>e.Owner.LoadInstanceId).Distinct().ToArray()) Retire(id); }
    internal void Retire(Guid instance)
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            foreach(var pair in _entries.Where(p=>p.Value.Owner.LoadInstanceId==instance).ToArray()) { _entries.Remove(pair.Key);_bytes-=pair.Value.Bytes; }
            foreach(var pair in _bindings.Where(p=>p.Value.Owner.LoadInstanceId==instance || p.Value.Reference.ProviderLoadInstance==instance).ToArray()) _bindings.Remove(pair.Key);
            foreach(var key in _states.Keys.Where(k=>!_entries.ContainsKey(("state",k.Item1))).ToArray()) { _stateBytes-=AbiValueBinary.Encode(StateSnapshot.Write(_states[key])).Length; _states.Remove(key); }
            _runtimeReady.Remove(instance);RetireWaiters(instance);RetireSchedules(instance); RetireSubscriptions(instance); RetireOperations(instance);
            foreach(var key in _actionMemo.Keys.Where(k=>k.Item1==instance).ToArray())_actionMemo.Remove(key);
            if(_diagnostics.Remove(instance,out var reports)) _diagnosticBytes-=reports.Sum(r=>(long)AbiValueBinary.Encode(DiagnosticReport.Write(r)).Length);_lifetimes.Remove(instance,out lifetime);_revision=NewRevision();RefreshAvailability();
        }
        if(lifetime is not null) { lifetime.Cancel(); lifetime.Dispose(); }
    }
    private CancellationToken Lifetime(PluginIdentity owner)
    {
        lock(_gate) { if(!_lifetimes.TryGetValue(owner.LoadInstanceId,out var source)) _lifetimes.Add(owner.LoadInstanceId,source=new()); return source.Token; }
    }
    private IEnumerable<HostCapabilityRoute> BasicRoutes => [
        HostRoutes.Typed<PublicContract,RegistrationRef>("Contracts","DefineService",(c,v)=>
        { CheckContract(v); return ValueTask.FromResult(Stage(c,"contract",v.Id,v)); }),
        HostRoutes.Typed<ProviderKindDefinition,RegistrationRef>("Contracts","DefineProviderKind",(c,v)=>
        { CheckContract(v.Contract); return ValueTask.FromResult(Stage(c,"provider-kind",v.Id,v)); }),
        HostRoutes.Typed<ExtensionPointDefinition,RegistrationRef>("Contracts","DefineExtensionPoint",(c,v)=>ValueTask.FromResult(Stage(c,"extension",v.Id,v))),
        HostRoutes.Typed<ConsumerDefinition,RegistrationRef>("Contracts","RegisterConsumer",(c,v)=>
        { if(v.Handler==0) throw new AbiHostException(AbiErrors.HANDLE_INVALID);return ValueTask.FromResult(Stage(c,"consumer",v.Id,v)); }),
        HostRoutes.Typed<ContractQuery,PublicContract>("Contracts","Describe",(c,v)=>
        { var result=PublicContract.Read(Find(c,"contract",v.Id).Value); if(result.Version!=v.Version) throw new AbiHostException(AbiErrors.SERVICE_CONTRACT_MISMATCH);return ValueTask.FromResult(result); }),
        HostRoutes.Typed<PageRequest,Page<PublicContract>>("Contracts","List",(c,v)=>
        {lock(_gate) return ValueTask.FromResult(Page(c,v,"contracts",_entries.Where(p=>p.Key.Item1=="contract"&&Visible(c,p.Value)).OrderBy(p=>p.Key.Item2.Value,StringComparer.Ordinal).Select(p=>PublicContract.Read(p.Value.Value))));}),
        HostRoutes.Typed<SchemaValidationRequest,ValidationReport>("Contracts","ValidateSchema",(c,v)=>ValueTask.FromResult(Validation(_validate(v.Schema,v.Value)))),
        HostRoutes.Typed<CompatibilityRequest,ValidationReport>("Contracts","CheckCompatibility",(c,v)=>
        { var result=PublicContract.Read(Find(c,"contract",v.Contract).Value);return ValueTask.FromResult(Validation(result.Version==v.Version&&result.Schema.Digest==v.SchemaDigest,AbiErrors.SERVICE_CONTRACT_MISMATCH)); }),
        HostRoutes.Typed<StateDescriptor,RegistrationRef>("State","DefineOwned",(c,v)=>ValueTask.FromResult(Stage(c,"state",v.Id,v))),
        HostRoutes.Typed<QualifiedRequest,StateDescriptor>("State","Describe",(c,v)=>ValueTask.FromResult(StateDescriptor.Read(Find(c,"state",v.Id).Value))),
        HostRoutes.Typed<StateReadRequest,StateSnapshot>("State","Read",(c,v)=>ValueTask.FromResult(ReadState(c,v))),
        HostRoutes.Typed<StateUpdate,StateSnapshot>("State","UpdateOwned",(c,v)=>ValueTask.FromResult(UpdateState(c,v))),
        HostRoutes.Typed<StateUpdate,StateSnapshot>("State","CompareExchangeOwned",(c,v)=>ValueTask.FromResult(UpdateState(c,v))),
        HostRoutes.Typed<DiagnosticReport,Empty>("Diagnostics","Log",Log),
        HostRoutes.Typed<DiagnosticReport,Empty>("Diagnostics","Report",Log),
        HostRoutes.Typed<DiagnosticSpan,SpanRef>("Diagnostics","CreateSpan",(c,v)=>
        { Log(c,new("span.start",Text(v.Name),DiagnosticSeverity.Trace,v.CorrelationId,c.Options.OperationId));return ValueTask.FromResult(new SpanRef(Guid.NewGuid())); }),
        HostRoutes.Typed<PageRequest,Page<DiagnosticReport>>("Diagnostics","ReadOwnReports",(c,v)=>
        {lock(_gate) return ValueTask.FromResult(Page(c,v,"diagnostics",_diagnostics.GetValueOrDefault(c.Identity.LoadInstanceId)??[]));})
    ];
    private static void CheckContract(PublicContract v)
    {
        if(v.Version==0 || v.Methods.IsEmpty || v.Methods.Length>256 || v.Methods.Any(m=>m.Id==0||m.NativeExecution) || v.Methods.Select(m=>m.Id).Distinct().Count()!=v.Methods.Length)
            throw new AbiHostException(AbiErrors.SERVICE_CONTRACT_MISMATCH);
    }
    private ValueTask<Empty> Log(HostInvocation c,DiagnosticReport value)
    {
        lock(_gate)
        {
            if(!_diagnostics.TryGetValue(c.Identity.LoadInstanceId,out var list)) _diagnostics.Add(c.Identity.LoadInstanceId,list=[]);
            int size=AbiValueBinary.Encode(DiagnosticReport.Write(value)).Length;
            if(size>65536||_diagnosticBytes+size>8*1024*1024)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            if(list.Count==1024) { _diagnosticBytes-=AbiValueBinary.Encode(DiagnosticReport.Write(list[0])).Length;list.RemoveAt(0); } list.Add(value);_diagnosticBytes+=size;_revision=NewRevision();
        }
        return ValueTask.FromResult(new Empty());
    }
    private static string ScopeKey(ScopedTarget scope) => Convert.ToHexString(SHA256.HashData(AbiValueBinary.Encode(ScopedTarget.Write(scope))));
    private StateSnapshot ReadState(HostInvocation c,StateReadRequest request)
    {
        lock(_gate)
        {
            var entry=Find(c,"state",request.Id);var descriptor=StateDescriptor.Read(entry.Value);
            if(descriptor.Scope!=request.Scope) throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
            return _states.GetValueOrDefault((request.Id,ScopeKey(request.Scope))) ?? new(new AbiValue.Null(),descriptor.Schema,new("uninitialized"),DateTimeOffset.UnixEpoch,null);
        }
    }
    private StateSnapshot UpdateState(HostInvocation c,StateUpdate request)
    {
        lock(_gate)
        {
            var entry=Find(c,"state",request.Id); if(entry.Owner!=c.Identity) throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            var old=ReadState(c,new(request.Id,request.Scope));
            if(old.Revision!=request.ExpectedRevision) throw new AbiHostException(AbiErrors.STALE_REVISION);
            Validate(old.Schema,request.Value);
            var updated=new StateSnapshot(request.Value,old.Schema,NewRevision(),DateTimeOffset.UtcNow,null);
            var key=(request.Id,ScopeKey(request.Scope));long delta=AbiValueBinary.Encode(StateSnapshot.Write(updated)).Length-(_states.TryGetValue(key,out var stored)?AbiValueBinary.Encode(StateSnapshot.Write(stored)).Length:0);
            if(_stateBytes+delta>32*1024*1024)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            _stateBytes+=delta;_states[key]=updated;_revision=NewRevision();
            Notify("state",request.Id.Value,StateSnapshot.Write(updated));return updated;
        }
    }
    public IEnumerable<HostCapabilityRoute> Routes => BasicRoutes.Concat(ServiceRoutes).Concat(EventRoutes).Concat(TaskRoutes).Concat(ActionRoutes).Concat(WorkflowRoutes).Concat(ScheduleRoutes).Concat(AvailabilityRoutes).Concat(ResourceRoutes);
}
