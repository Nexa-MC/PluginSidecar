using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace PluginSidecar.WasmRuntime;

/// <summary>Bootstrap and business callback adapter for import-free WebAssembly.</summary>
public sealed class WasmPluginEntry(PluginManifest manifest, WasmPluginMachine machine) : IPluginBootstrapV1, IPluginInvocationV1, IPluginCleanupInvocationV1, IPluginProbeInvocationV1, IAsyncDisposable
{
    private PluginIdentity? _identity;
    private ABIContext? _abi;
    private readonly CancellationTokenSource _activeLifetime = new();
    private int _started, _active, _retired, _disposed;
    public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return AbiReply<PreInitResult>.Fail(Error(AbiErrors.STAGE_INVALID, "PreInitialize"));
        _identity = context.Identity;
        var draft = await context.Negotiator.GetABIContext(100, new(Guid.NewGuid(), context.Budget, context.Cancellation)).ConfigureAwait(false);
        if (!draft.Success) return AbiReply<PreInitResult>.Fail(draft.Error!);
        var builder = new Abi100Draft(draft.Value);
        foreach (var requirement in manifest.RequiredCapabilities) builder = builder.Require(requirement.ContractId, requirement.MinimumVersion);
        foreach (var optional in manifest.OptionalCapabilities) builder = builder.Optional(optional.ContractId, optional.MinimumVersion);
        var ready = await builder.Make(context.Negotiator, Guid.NewGuid(), manifest.RequestedPermissions, context.Cancellation).ConfigureAwait(false);
        if (!ready.Success) return AbiReply<PreInitResult>.Fail(ready.Error!);
        _abi = ready.Value;
        var result = await Callback("PreInitialize", ready.Value, null, context.Budget, context.Cancellation).ConfigureAwait(false);
        return result.Success ? AbiReply<PreInitResult>.Ok(new(ready.Value, manifest.RequiredDependencies, manifest.OptionalDependencies, manifest.Conflicts, [])) : AbiReply<PreInitResult>.Fail(result.Error!);
    }
    public ValueTask<AbiReply<Unit>> OnInitialize(ABIContext abi, StageContext stage) => Callback("Initialize", abi, stage, stage.RemainingBudget, stage.Cancellation);
    public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext abi, StageContext stage) => Callback("PostInitialize", abi, stage, stage.RemainingBudget, stage.Cancellation);
    public async ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext abi, StageContext stage)
    {
        var result = await Callback("Checkup", abi, stage, stage.RemainingBudget, stage.Cancellation).ConfigureAwait(false);
        return result.Success ? AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed, [], [])) : AbiReply<CheckupResult>.Fail(result.Error!);
    }
    public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext abi, StageContext stage) => Callback("Finalize", abi, stage, stage.RemainingBudget, stage.Cancellation);
    public ValueTask<AbiReply<Unit>> OnActivated(ABIContext abi, InvocationContext invocation)
    {
        if (Volatile.Read(ref _retired) != 0 || !ReferenceEquals(abi, _abi) || invocation.Identity != _identity)
            return ValueTask.FromResult(AbiReply<Unit>.Fail(Error(AbiErrors.HANDLE_OWNER_MISMATCH, "Activated")));
        Volatile.Write(ref _active, 1);
        return Callback("Activated", abi, null, invocation.Budget, invocation.Cancellation, invocation);
    }
    public ValueTask<AbiReply<Unit>> OnStop(StopContext context) { Retire(); return Callback("Stop", context.ABI, null, context.Budget, context.Cancellation); }
    public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context) { Retire(); return Callback("Abort", context.ABI, null, context.Budget, context.Cancellation); }
    private void Retire() { Volatile.Write(ref _retired, 1); Volatile.Write(ref _active, 0); _activeLifetime.Cancel(); }

    public async ValueTask<AbiReply<AbiValue>> InvokeHandler(ABIContext abi, ulong handler, AbiValue input, InvocationContext invocation)
    {
        if (Volatile.Read(ref _active) == 0 || Volatile.Read(ref _retired) != 0)
            return AbiReply<AbiValue>.Fail(Error(AbiErrors.PLUGIN_STOPPED, "Active", invocation));
        if (!ReferenceEquals(abi, _abi) || invocation.Identity != _identity || handler == 0 || invocation.InvocationId == Guid.Empty)
            return AbiReply<AbiValue>.Fail(Error(AbiErrors.HANDLE_OWNER_MISMATCH, "Active", invocation));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(invocation.Cancellation, _activeLifetime.Token);
        try
        {
            DataCodec.Validate(input);
            var message = Message("handler", "Active", abi, invocation.Budget, invocation);
            message = new(message.Value.Add("handler", DataCodec.WriteUInt64(handler)).Add("input", input));
            var result = await Run(message, abi, null, invocation.Budget, lifetime.Token, invocation).ConfigureAwait(false);
            var fields = DataCodec.ReadMap(result);
            return DataCodec.ReadBoolean(DataCodec.Field(fields, "success"))
                ? AbiReply<AbiValue>.Ok(DataCodec.Field(fields, "value"))
                : AbiReply<AbiValue>.Fail(GuestError(fields, "Active", invocation));
        }
        catch (OperationCanceledException) { return AbiReply<AbiValue>.Fail(Error(lifetime.IsCancellationRequested ? AbiErrors.CANCELLED : AbiErrors.DEADLINE_EXCEEDED, "Active", invocation)); }
        catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException) { return AbiReply<AbiValue>.Fail(Error(AbiErrors.PLUGIN_EXCEPTION, "Active", invocation)); }
    }

    public async ValueTask<AbiReply<AbiValue>> InvokeProbe(ABIContext abi,ulong handler,AbiValue input,InvocationContext invocation)
    {
        const string phase="Checkup";
        if(!ReferenceEquals(abi,_abi)||invocation.Identity!=_identity||handler==0||invocation.InvocationId==Guid.Empty||Volatile.Read(ref _retired)!=0)
            return AbiReply<AbiValue>.Fail(Error(AbiErrors.HANDLE_OWNER_MISMATCH,phase,invocation));
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(invocation.Cancellation,_activeLifetime.Token);
        try
        {
            DataCodec.Validate(input);
            var message=Message("handler",phase,abi,invocation.Budget,invocation);
            message=new(message.Value.Add("handler",DataCodec.WriteUInt64(handler)).Add("input",input));
            var fields=DataCodec.ReadMap(await Run(message,abi,null,invocation.Budget,lifetime.Token,invocation).ConfigureAwait(false));
            return DataCodec.ReadBoolean(DataCodec.Field(fields,"success"))?AbiReply<AbiValue>.Ok(DataCodec.Field(fields,"value")):AbiReply<AbiValue>.Fail(GuestError(fields,phase,invocation));
        }
        catch(OperationCanceledException){return AbiReply<AbiValue>.Fail(Error(AbiErrors.CANCELLED,phase,invocation));}
        catch(Exception error)when(error is not OutOfMemoryException and not AccessViolationException){return AbiReply<AbiValue>.Fail(Error(AbiErrors.PLUGIN_EXCEPTION,phase,invocation));}
    }

    public async ValueTask<AbiReply<AbiValue>> InvokeCleanup(ABIContext abi,ulong handler,string phase,InvocationOptions options)
    {
        if(!ReferenceEquals(abi,_abi)||handler==0||_identity is null||Volatile.Read(ref _disposed)!=0)
            return AbiReply<AbiValue>.Fail(Error(AbiErrors.HANDLE_OWNER_MISMATCH,phase));
        var invocation=new InvocationContext(_identity,options.OperationId,options.CorrelationId,options.CausationId,options.Cancellation,options.Budget);
        try
        {
            var message=Message("handler",phase,abi,options.Budget,invocation);
            message=new(message.Value.Add("handler",DataCodec.WriteUInt64(handler)).Add("input",new AbiValue.Null()));
            StageContext? stage=options.StageInvocationId==Guid.Empty?null:new(_identity,LoadStage.Initialize,options.StageInvocationId,Guid.Empty,options.Cancellation,options.Budget);
            var fields=DataCodec.ReadMap(await Run(message,abi,stage,options.Budget,options.Cancellation,invocation).ConfigureAwait(false));
            return DataCodec.ReadBoolean(DataCodec.Field(fields,"success"))?AbiReply<AbiValue>.Ok(DataCodec.Field(fields,"value")):AbiReply<AbiValue>.Fail(GuestError(fields,phase,invocation));
        }
        catch(OperationCanceledException){return AbiReply<AbiValue>.Fail(Error(AbiErrors.OUTCOME_UNKNOWN,phase,invocation));}
        catch(Exception error)when(error is not OutOfMemoryException and not AccessViolationException){return AbiReply<AbiValue>.Fail(Error(AbiErrors.PLUGIN_EXCEPTION,phase,invocation));}
    }

    private async ValueTask<AbiReply<Unit>> Callback(string phase, ABIContext? abi, StageContext? stage, TimeSpan budget, CancellationToken cancellation, InvocationContext? invocation = null)
    {
        try
        {
            invocation ??= new(_identity ?? throw new InvalidOperationException("PreInitialize was not called."), Guid.NewGuid(), Guid.NewGuid(), null, cancellation, budget);
            if (abi is not null && !ReferenceEquals(abi, _abi)) return AbiReply<Unit>.Fail(Error(AbiErrors.HANDLE_OWNER_MISMATCH, phase, invocation));
            var result = await Run(Message("callback", phase, abi, budget, invocation), abi, stage, budget, cancellation, invocation).ConfigureAwait(false);
            var fields = DataCodec.ReadMap(result);
            return DataCodec.ReadBoolean(DataCodec.Field(fields, "success")) ? AbiReply<Unit>.Ok(new()) : AbiReply<Unit>.Fail(GuestError(fields, phase, invocation));
        }
        catch (OperationCanceledException) { return AbiReply<Unit>.Fail(Error(cancellation.IsCancellationRequested ? AbiErrors.CANCELLED : AbiErrors.DEADLINE_EXCEEDED, phase, invocation)); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { return AbiReply<Unit>.Fail(Error(AbiErrors.PLUGIN_EXCEPTION, phase, invocation)); }
    }
    private async ValueTask<AbiValue> Run(AbiValue input, ABIContext? abi, StageContext? stage, TimeSpan budget, CancellationToken cancellation, InvocationContext invocation)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        return await machine.RunDetailedAsync(input, async (call, token) =>
        {
            if (abi is null) return AbiReply<AbiValue>.Fail(Error(AbiErrors.CONTEXT_NOT_READY, "Callback", invocation));
            var ability = call.ExperimentId is { } experiment ? abi.GetExperiment(experiment, call.Contract) : abi.GetAbility(call.Contract);
            if (!ability.Success) return AbiReply<AbiValue>.Fail(ability.Error!);
            var remaining = budget - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (call.Budget is { } requested && requested < remaining) remaining = requested;
            if (remaining <= TimeSpan.Zero) return AbiReply<AbiValue>.Fail(Error(AbiErrors.DEADLINE_EXCEEDED, "Callback", invocation));
            var options = new InvocationOptions(call.OperationId, remaining, token)
            { StageInvocationId = stage?.StageInvocationId ?? Guid.Empty, CorrelationId = invocation.CorrelationId, CausationId = invocation.CausationId, IdempotencyKey = call.IdempotencyKey, ExpectedRevision = call.ExpectedRevision };
            return await ability.Value.Invoke(call.Contract, call.Method, call.Arguments, options).ConfigureAwait(false);
        }, budget, cancellation).ConfigureAwait(false);
    }
    private static AbiValue.Map Message(string kind, string phase, ABIContext? abi, TimeSpan budget, InvocationContext invocation) => (AbiValue.Map)DataCodec.Map(
        ("kind", DataCodec.WriteString(kind)), ("phase", DataCodec.WriteString(phase)), ("pluginId", DataCodec.WriteString(invocation.Identity.PluginId)),
        ("loadInstanceId", DataCodec.WriteGuid(invocation.Identity.LoadInstanceId)), ("invocationId", DataCodec.WriteGuid(invocation.InvocationId)),
        ("correlationId", DataCodec.WriteGuid(invocation.CorrelationId)), ("causationId", invocation.CausationId is { } cause ? DataCodec.WriteGuid(cause) : new AbiValue.Null()),
        ("budgetMilliseconds", DataCodec.WriteInt64((long)budget.TotalMilliseconds)),
        ("abi", abi is null ? new AbiValue.Null() : DataCodec.Map(("version", DataCodec.WriteUInt64(abi.Info.StableAbiVersion)),
            ("capabilities", new AbiValue.Array(abi.Info.Capabilities.Select(c => DataCodec.WriteString(c.ContractId)).ToImmutableArray())),
            ("permissions", new AbiValue.Array(abi.Info.Permissions.Select(DataCodec.WriteString).ToImmutableArray())),
            ("experiments", new AbiValue.Array(abi.Info.Experiments.Select(e => DataCodec.WriteGuid(e.ExpId)).ToImmutableArray())))));
    private AbiError GuestError(ImmutableDictionary<string, AbiValue> fields, string phase, InvocationContext invocation)
    {
        var detail = fields.TryGetValue("error", out var error) && error is AbiValue.Map m ? m.Value : fields;
        string code = detail.TryGetValue("code", out var value) ? DataCodec.ReadString(value) : AbiErrors.PLUGIN_EXCEPTION;
        var result = Error(code, phase, invocation);
        return result with
        {
            MessageKey = detail.TryGetValue("messageKey", out value) ? DataCodec.ReadString(value) : code,
            Parameters = Strings("parameters"), Details = Strings("details")
        };
        ImmutableDictionary<string,string> Strings(string key) => detail.TryGetValue(key, out var map)
            ? DataCodec.ReadMap(map).ToImmutableDictionary(p => p.Key, p => DataCodec.ReadString(p.Value)) : ImmutableDictionary<string,string>.Empty;
    }
    private AbiError Error(string code, string phase, InvocationContext? invocation = null) => new(code, code, phase, "plugin.callback", _identity?.PluginId ?? manifest.PluginId,
        _identity?.LoadInstanceId ?? Guid.Empty, invocation?.CorrelationId ?? Guid.Empty, false, ImmutableDictionary<string, string>.Empty) { OperationId = invocation?.InvocationId ?? Guid.Empty };
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Retire(); await machine.DisposeAsync().ConfigureAwait(false); _activeLifetime.Dispose();
    }
}
