using System.Collections.Immutable;
using System.Security.Cryptography;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed record HostInvocation(PluginIdentity Identity, Guid GroupId, string Phase, InvocationOptions Options);
public delegate ValueTask<AbiReply<AbiValue>> HostAbilityHandler(HostInvocation invocation, AbiValue arguments);
public sealed record HostCapabilityRoute(AbiOperationDescriptor Descriptor, HostAbilityHandler Handler);

/// <summary>Trusted Host authority. Guest-provided identity, phase and capability metadata are never used as grants.</summary>
public sealed class CapabilityAuthority
{
    private readonly ImmutableDictionary<(string, uint), HostCapabilityRoute> _routes;
    private readonly Guid _session = Guid.NewGuid();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Context> _contexts = [];
    private ulong _nextHandle;
    private int _inflight, _memoEntries;
    private long _cachedBytes, _reservedBytes;
    internal object PublicationGate => _gate;

    public CapabilityAuthority(IEnumerable<HostCapabilityRoute> routes) : this(_ => routes) { }

    internal CapabilityAuthority(Func<CapabilityAuthority, IEnumerable<HostCapabilityRoute>> createRoutes)
    {
        _routes = createRoutes(this).ToImmutableDictionary(r => (r.Descriptor.ContractId, r.Descriptor.MethodId));
        foreach (var route in _routes.Values)
            if (route.Descriptor.MethodId == 0 || string.IsNullOrWhiteSpace(route.Descriptor.ContractId) || route.Handler is null)
                throw new ArgumentException("Invalid Host capability route.", nameof(createRoutes));
    }

    /// <summary>Called by trusted Host admission only, after package verification and user scope grants.</summary>
    public Admission Admit(PluginIdentity identity, Guid groupId, ImmutableArray<string> grantedPermissions)
    {
        if (identity.LoadInstanceId == Guid.Empty || groupId == Guid.Empty) throw new ArgumentException("An admitted instance needs assigned identities.");
        lock (_gate)
        {
            if (_contexts.Count >= 1024) throw new InvalidOperationException("The Host plugin context quota is exhausted.");
            if (_contexts.ContainsKey(identity.LoadInstanceId)) throw new InvalidOperationException("Load instance already admitted.");
            var info = new AbiContextInfo(new(++_nextHandle), _session, identity, 100, [], grantedPermissions, Guid.NewGuid(), "abi100-v0.2", "host-authority", []);
            var context = new Context(this, info, groupId);
            _contexts.Add(identity.LoadInstanceId, context);
            return new Admission(context);
        }
    }

    public void CommitGroup(IReadOnlyList<Admission> members, Action publishRegistrations)
    {
        lock (_gate)
        {
            if (members.Count == 0 || members.Any(m => !m.BelongsTo(this) || !m.IsPrepared || m.GroupId != members[0].GroupId) ||
                members.Select(m => m.Context.Info.Identity.LoadInstanceId).Distinct().Count() != members.Count ||
                _contexts.Values.Count(c => c.GroupId == members[0].GroupId) != members.Count) throw new InvalidOperationException("Group is not prepared.");
            publishRegistrations(); // Host-owned atomic store only; no plugin callback.
            foreach (var member in members) member.Activate();
        }
    }

    public sealed class Admission
    {
        private readonly Context _context;
        internal Admission(Context context) => _context = context;
        public IAbiNegotiatorV1 Negotiator => _context;
        public ABIContext Context => _context;
        public void Enter(StageContext stage)
        {
            lock (_context.Gate)
            {
                if (_context.Phase is "Stopped" or "Stop" or "Abort" || stage.Identity != _context.Info.Identity || stage.LoadGroupId != _context.GroupId || stage.StageInvocationId == Guid.Empty)
                    throw new InvalidOperationException("Invalid lifecycle admission.");
                var expected = _context.Phase switch { "PreInitialize" => LoadStage.Initialize, "Initialize" => LoadStage.PostInitialize, "PostInitialize" => LoadStage.Checkup, "Checkup" => LoadStage.Finalize, _ => (LoadStage?)null };
                if (!_context.Ready || expected != stage.Stage) throw new InvalidOperationException("Invalid lifecycle transition.");
                if (_context.Stage is not null) throw new InvalidOperationException("Previous callback is still admitted.");
                _context.StageLifetime = CancellationTokenSource.CreateLinkedTokenSource(stage.Cancellation, _context.Lifetime.Token);
                _context.StageLifetime.CancelAfter(stage.RemainingBudget > TimeSpan.Zero ? stage.RemainingBudget : TimeSpan.Zero);
                _context.Stage = stage; _context.Phase = stage.Stage.ToString();
            }
        }
        public void End(StageContext stage)
        {
            CancellationTokenSource? lifetime = null;
            lock (_context.Gate) { if (_context.Stage?.StageInvocationId == stage.StageInvocationId) { _context.Stage = null; lifetime = _context.StageLifetime; _context.StageLifetime = null; } }
            if (lifetime is not null) { lifetime.Cancel(); lifetime.Dispose(); }
        }
        internal bool IsReady { get { lock (_context.Gate) return _context.Ready; } }
        internal bool IsPrepared { get { lock (_context.Gate) return _context.Ready && _context.Phase == "Finalize" && _context.Stage is null; } }
        internal bool BelongsTo(CapabilityAuthority authority) => ReferenceEquals(_context.Owner, authority);
        internal Guid GroupId => _context.GroupId;
        public void Activate() { lock (_context.Gate) { if (!_context.Ready || _context.Phase != "Finalize" || _context.Stage is not null) throw new InvalidOperationException("Instance is not prepared."); _context.Phase = "Active"; } }
        public void Retire(bool abort)
        {
            lock (_context.Gate) { _context.Phase = abort ? "Abort" : "Stop"; _context.Stage = null; }
            _context.Lifetime.Cancel();
        }
        public void CompleteStop()
        {
            lock (_context.Gate)
            {
                _context.Phase = "Stopped";
                _context.ClearMemo();
                _context.Owner._contexts.Remove(_context.Info.Identity.LoadInstanceId);
                _context.StageLifetime?.Dispose(); _context.StageLifetime = null;
                _context.Lifetime.Dispose();
            }
        }
    }

    internal sealed class Context(CapabilityAuthority owner, AbiContextInfo info, Guid groupId) : ABIContext, IAbiNegotiatorV1, IAbilityV1
    {
        internal CapabilityAuthority Owner => owner;
        internal object Gate => owner._gate;
        internal CancellationTokenSource? StageLifetime;
        internal Guid GroupId { get; } = groupId;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal bool Ready;
        internal StageContext? Stage;
        internal string Phase = "PreInitialize";
        private readonly Dictionary<Guid, (string Digest, AbiReply<ABIContext> Result)> _makes = [];
        private int _inflight;
        private long _cachedBytes, _reservedBytes;
        private readonly Dictionary<(string, uint, string), (string Digest, Task<AbiReply<AbiValue>> Result)> _idempotency = [];
        private ImmutableHashSet<string> _capabilities = ImmutableHashSet<string>.Empty;
        public AbiContextInfo Info { get; private set; } = info;
        public AbiContextHandle ContextHandle => Info.Handle;
        AbiContextHandle IAbilityV1.Context => ContextHandle;
        public AbiReply<IAbilityV1> GetAbility(string contractId)
        {
            lock (Gate)
            {
                if (!Ready) return Fail<IAbilityV1>(AbiErrors.CONTEXT_NOT_READY);
                if (Phase == "Stopped") return Fail<IAbilityV1>(AbiErrors.PLUGIN_STOPPED);
                if (!_capabilities.Contains(contractId)) return Fail<IAbilityV1>(AbiErrors.CAPABILITY_UNAVAILABLE);
                return AbiReply<IAbilityV1>.Ok(this);
            }
        }
        public AbiReply<IAbilityV1> GetExperiment(Guid expId, string contractId) => Fail<IAbilityV1>(AbiErrors.EXPERIMENT_UNAVAILABLE);
        public ValueTask<AbiReply<ExperimentRef>> GetExpermentABI(Guid expId, InvocationOptions options) => ValueTask.FromResult(Fail<ExperimentRef>(AbiErrors.EXPERIMENT_UNAVAILABLE, options));
        public ValueTask<AbiReply<ABIContext>> GetABIContext(uint version, InvocationOptions options)
        {
            lock (Gate)
            {
                string? error = Phase != "PreInitialize" ? AbiErrors.STAGE_DENIED : !AbiVersions.IsStable(version) ? AbiErrors.ABI_VERSION_INVALID : version != 100 ? AbiErrors.ABI_UNSUPPORTED : null;
                return ValueTask.FromResult(error is null ? AbiReply<ABIContext>.Ok(this) : Fail<ABIContext>(error, options));
            }
        }
        public ValueTask<AbiReply<ABIContext>> MakeABIContext(ABIContext draft, ImmutableArray<ExperimentRef> experiments, MakeOptions options, CancellationToken cancellation)
        {
            lock (Gate)
            {
                if (cancellation.IsCancellationRequested) return ValueTask.FromResult(Fail<ABIContext>(AbiErrors.CANCELLED));
                if (Phase != "PreInitialize" || !ReferenceEquals(draft, this)) return ValueTask.FromResult(Fail<ABIContext>(AbiErrors.STAGE_DENIED));
                if (options.OperationId == Guid.Empty || options.RequestedPermissions.IsDefault || options.RequiredCapabilities.IsDefault || options.OptionalCapabilities.IsDefault || experiments.IsDefault)
                    return ValueTask.FromResult(Fail<ABIContext>(AbiErrors.SCHEMA_INVALID));
                if (options.RequiredCapabilities.Concat(options.OptionalCapabilities).Any(r => r is null || string.IsNullOrWhiteSpace(r.ContractId) || r.MinimumVersion == 0) ||
                    options.RequiredCapabilities.Concat(options.OptionalCapabilities).Select(r => r.ContractId).Distinct(StringComparer.Ordinal).Count() != options.RequiredCapabilities.Length + options.OptionalCapabilities.Length ||
                    options.RequestedPermissions.Any(string.IsNullOrWhiteSpace)) return ValueTask.FromResult(Fail<ABIContext>(AbiErrors.SCHEMA_INVALID));
                string digest = Convert.ToHexString(SHA256.HashData(AbiValueBinary.Encode(DataCodec.Map(
                    ("permissions", new AbiValue.Array(options.RequestedPermissions.Select(DataCodec.WriteString).ToImmutableArray())),
                    ("required", Requirements(options.RequiredCapabilities)), ("optional", Requirements(options.OptionalCapabilities)),
                    ("experiments", new AbiValue.Array(experiments.Select(e => (AbiValue)new AbiValue.UInt64(e.Value)).ToImmutableArray()))))));
                if (_makes.TryGetValue(options.OperationId, out var existing)) return ValueTask.FromResult(existing.Digest == digest ? existing.Result : Fail<ABIContext>(AbiErrors.REQUEST_ID_REUSED));
                if (Ready) return ValueTask.FromResult(Fail<ABIContext>(AbiErrors.CONTEXT_ALREADY_CONFIRMED));
                AbiReply<ABIContext> result;
                if (_makes.Count >= 128) return ValueTask.FromResult(Fail<ABIContext>(AbiErrors.MESSAGE_TOO_LARGE));
                var required = options.RequiredCapabilities; var optional = options.OptionalCapabilities;
                if (!experiments.IsEmpty) result = Fail<ABIContext>(AbiErrors.EXPERIMENT_UNAVAILABLE);
                else if (options.RequestedPermissions.Any(p => !Info.Permissions.Contains(p))) result = Fail<ABIContext>(AbiErrors.CAPABILITY_DENIED);
                else if (required.Any(r => !Supported(r, options.RequestedPermissions))) result = Fail<ABIContext>(AbiErrors.CAPABILITY_UNAVAILABLE);
                else
                {
                    // Negotiation narrows the admission grant. Unknown optional contracts are omitted.
                    _capabilities = required.Concat(optional).Where(r => Supported(r, options.RequestedPermissions)).Select(r => Normalize(r.ContractId)).ToImmutableHashSet(StringComparer.Ordinal);
                    Info = Info with { Permissions = options.RequestedPermissions.Distinct(StringComparer.Ordinal).ToImmutableArray(), Capabilities = _capabilities.Order().Select(c => new CapabilityDescriptor(c)).ToImmutableArray() };
                    Ready = true; result = AbiReply<ABIContext>.Ok(this);
                }
                _makes.Add(options.OperationId, (digest, result)); return ValueTask.FromResult(result);
            }
        }
        private static AbiValue Requirements(ImmutableArray<CapabilityRequirement> requirements) => new AbiValue.Array(requirements.Select(r => DataCodec.Map(("id", DataCodec.WriteString(r.ContractId)), ("version", new AbiValue.UInt64(r.MinimumVersion)))).ToImmutableArray());
        private bool Supported(CapabilityRequirement requirement, ImmutableArray<string> requested) => requirement.MinimumVersion == 1 && owner._routes.Values.Any(r => r.Descriptor.ContractId == Normalize(requirement.ContractId) && requested.Contains(r.Descriptor.Permission) && Info.Permissions.Contains(r.Descriptor.Permission));
        private static string Normalize(string id) => id.Contains('/') ? id : id + "/100";

        public async ValueTask<AbiReply<AbiValue>> Invoke(string contractId, uint methodId, AbiValue arguments, InvocationOptions options)
        {
            try { DataCodec.Validate(arguments); } catch (FormatException) { return Fail<AbiValue>(AbiErrors.SCHEMA_INVALID, options); }
            HostCapabilityRoute route;
            HostInvocation invocation;
            CancellationToken stageCancellation;
            TaskCompletionSource<AbiReply<AbiValue>>? completion = null;
            Task<AbiReply<AbiValue>>? replay = null;
            bool reserved = false;
            lock (Gate)
            {
                var available = GetAbility(contractId); if (!available.Success) return AbiReply<AbiValue>.Fail(available.Error!);
                if (!owner._routes.TryGetValue((contractId, methodId), out route!)) return Fail<AbiValue>(AbiErrors.CAPABILITY_UNAVAILABLE, options);
                if (!Info.Permissions.Contains(route.Descriptor.Permission)) return Fail<AbiValue>(AbiErrors.CAPABILITY_DENIED, options);
                if (!route.Descriptor.Phases.Contains(Phase)) return Fail<AbiValue>(Phase is "Stop" or "Stopped" ? AbiErrors.PLUGIN_STOPPED : AbiErrors.STAGE_DENIED, options);
                if (options.OperationId == Guid.Empty || options.Budget <= TimeSpan.Zero || options.Budget > TimeSpan.FromMinutes(5)) return Fail<AbiValue>(AbiErrors.SCHEMA_INVALID, options);
                if (Stage is { } stage)
                {
                    if (options.StageInvocationId != stage.StageInvocationId) return Fail<AbiValue>(AbiErrors.STAGE_FROZEN, options);
                    if (stage.RemainingBudget <= TimeSpan.Zero) return Fail<AbiValue>(AbiErrors.DEADLINE_EXCEEDED, options);
                    options = options with { Budget = options.Budget < stage.RemainingBudget ? options.Budget : stage.RemainingBudget };
                    stageCancellation = StageLifetime!.Token;
                }
                else
                {
                    if (Phase is not ("Active" or "Stop" or "Abort") || options.StageInvocationId != Guid.Empty) return Fail<AbiValue>(AbiErrors.STAGE_FROZEN, options);
                    stageCancellation = Phase == "Active" ? Lifetime.Token : default;
                }
                if (options.Cancellation.IsCancellationRequested || stageCancellation.IsCancellationRequested) return Fail<AbiValue>(AbiErrors.CANCELLED, options);
                if (route.Descriptor.Idempotency != "ReadOnly")
                {
                    if (string.IsNullOrWhiteSpace(options.IdempotencyKey) || options.IdempotencyKey.Length > 256) return Fail<AbiValue>(AbiErrors.SCHEMA_INVALID, options);
                    var key = (contractId, methodId, options.IdempotencyKey);
                    string digest = Convert.ToHexString(SHA256.HashData(AbiValueBinary.Encode(DataCodec.Map(("arguments", arguments), ("revision", options.ExpectedRevision is null ? new AbiValue.Null() : DataCodec.WriteString(options.ExpectedRevision))))));
                    if (_idempotency.TryGetValue(key, out var prior))
                    {
                        if (prior.Digest != digest) return Fail<AbiValue>(AbiErrors.REQUEST_ID_REUSED, options);
                        replay = prior.Result;
                    }
                    else
                    {
                        if (_idempotency.Count >= 4096 || owner._memoEntries >= 65536) return Fail<AbiValue>(AbiErrors.MESSAGE_TOO_LARGE, options);
                        if (_cachedBytes + _reservedBytes + DataCodec.MaximumBytes > 8L * 1024 * 1024 || owner._cachedBytes + owner._reservedBytes + DataCodec.MaximumBytes > 64L * 1024 * 1024)
                            return Fail<AbiValue>(AbiErrors.MESSAGE_TOO_LARGE, options);
                        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _idempotency.Add(key, (digest, completion.Task)); owner._memoEntries++;
                    }
                }
                if (replay is null)
                {
                    if (_inflight >= 8 || owner._inflight >= 256)
                    {
                        if (completion is not null) { _idempotency.Remove((contractId, methodId, options.IdempotencyKey!)); owner._memoEntries--; }
                        return Fail<AbiValue>(AbiErrors.MESSAGE_TOO_LARGE, options);
                    }
                    _inflight++; owner._inflight++;
                    if (completion is not null) { reserved = true; _reservedBytes += DataCodec.MaximumBytes; owner._reservedBytes += DataCodec.MaximumBytes; }
                }
                invocation = new(Info.Identity, GroupId, Phase, options);
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(options.Cancellation, stageCancellation);
            linked.CancelAfter(options.Budget);
            var callToken = linked.Token;
            Task<AbiReply<AbiValue>> pending = replay ?? Task.Run(async () =>
            {
                AbiReply<AbiValue> reply;
                int memoSize;
                try
                {
                    callToken.ThrowIfCancellationRequested();
                    reply = await route.Handler(invocation with { Options = options with { Cancellation = callToken } }, arguments).ConfigureAwait(false);
                    if (reply.Success) DataCodec.Validate(reply.Value);
                    memoSize = reply.Success ? AbiValueBinary.Encode(reply.Value).Length : ErrorSize(reply.Error!);
                }
                catch (OperationCanceledException)
                { reply = Fail<AbiValue>(route.Descriptor.Kind == "Query" ? AbiErrors.CANCELLED : AbiErrors.OUTCOME_UNKNOWN, options); memoSize = 4096; }
                catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
                { reply = Fail<AbiValue>(AbiErrors.PLUGIN_EXCEPTION, options); memoSize = 4096; }
                lock (Gate)
                {
                    _inflight--; owner._inflight--;
                    if (reserved)
                    {
                        _reservedBytes -= DataCodec.MaximumBytes; owner._reservedBytes -= DataCodec.MaximumBytes;
                        if (Phase != "Stopped") { _cachedBytes += memoSize; owner._cachedBytes += memoSize; }
                    }
                    completion?.TrySetResult(reply);
                }
                return reply;
            });
            try { return await pending.WaitAsync(callToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // The authoritative task keeps its concurrency/memory reservation until it
                // really finishes. A retry with the same key observes that task, not a second effect.
                return Fail<AbiValue>(route.Descriptor.Kind == "Query" ? AbiErrors.DEADLINE_EXCEEDED : AbiErrors.OUTCOME_UNKNOWN, options);
            }
        }
        private static int ErrorSize(AbiError error) => AbiValueBinary.Encode(DataCodec.Map(
            ("code", DataCodec.WriteString(error.Code)), ("message", DataCodec.WriteString(error.Message)), ("messageKey", DataCodec.WriteString(error.MessageKey)),
            ("stage", DataCodec.WriteString(error.Stage)), ("operation", DataCodec.WriteString(error.Operation)), ("pluginId", DataCodec.WriteString(error.PluginId)),
            ("parameters", new AbiValue.Map(error.Parameters.ToImmutableDictionary(p => p.Key, p => DataCodec.WriteString(p.Value)))),
            ("details", new AbiValue.Map(error.Details.ToImmutableDictionary(p => p.Key, p => DataCodec.WriteString(p.Value)))))).Length + 128;
        internal void ClearMemo()
        {
            owner._cachedBytes -= _cachedBytes; _cachedBytes = 0; owner._memoEntries -= _idempotency.Count; _idempotency.Clear(); _makes.Clear();
        }
        private AbiReply<T> Fail<T>(string code, InvocationOptions? options = null) => AbiReply<T>.Fail(new(code, code, Phase, "abi", Info.Identity.PluginId,
            Info.Identity.LoadInstanceId, options?.CorrelationId ?? Guid.Empty, false, ImmutableDictionary<string, string>.Empty) { OperationId = options?.OperationId ?? Guid.Empty });
    }
}
