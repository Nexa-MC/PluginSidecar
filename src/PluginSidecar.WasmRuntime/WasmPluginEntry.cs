using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace PluginSidecar.WasmRuntime;

/// <summary>Bootstrap adapter for the import-free WebAssembly execution profile.</summary>
public sealed class WasmPluginEntry(PluginManifest manifest, WasmPluginMachine machine) : IPluginBootstrapV1, IAsyncDisposable
{
    private PluginIdentity? _identity;
    public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
    {
        _identity = context.Identity;
        var draft = await context.Negotiator.GetABIContext(100, new(Guid.NewGuid(), context.Budget, context.Cancellation)).ConfigureAwait(false);
        if (!draft.Success) return AbiReply<PreInitResult>.Fail(draft.Error!);
        var builder = new Abi100Draft(draft.Value);
        foreach (var requirement in manifest.RequiredCapabilities) builder = builder.Require(requirement.ContractId, requirement.MinimumVersion);
        foreach (var optional in manifest.OptionalCapabilities) builder = builder.Optional(optional.ContractId, optional.MinimumVersion);
        var ready = await builder.Make(context.Negotiator, Guid.NewGuid(), manifest.RequestedPermissions, context.Cancellation).ConfigureAwait(false);
        if (!ready.Success) return AbiReply<PreInitResult>.Fail(ready.Error!);
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
    public ValueTask<AbiReply<Unit>> OnActivated(ABIContext abi, InvocationContext invocation) => Callback("Activated", abi, null, invocation.Budget, invocation.Cancellation);
    public ValueTask<AbiReply<Unit>> OnStop(StopContext context) => Callback("Stop", context.ABI, null, context.Budget, context.Cancellation);
    public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context) => Callback("Abort", context.ABI, null, context.Budget, context.Cancellation);
    private async ValueTask<AbiReply<Unit>> Callback(string phase, ABIContext? abi, StageContext? stage, TimeSpan budget, CancellationToken cancellation)
    {
        try
        {
            var identity = _identity ?? throw new InvalidOperationException("PreInitialize was not called.");
            Guid correlation = Guid.NewGuid(); long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var input = DataCodec.Map(("kind", DataCodec.WriteString("callback")), ("phase", DataCodec.WriteString(phase)),
                ("pluginId", DataCodec.WriteString(identity.PluginId)), ("loadInstanceId", DataCodec.WriteGuid(identity.LoadInstanceId)),
                ("correlationId", DataCodec.WriteGuid(correlation)));
            var result = await machine.RunDetailedAsync(input, async (call, token) =>
            {
                if (abi is null) return AbiReply<AbiValue>.Fail(Error(AbiErrors.CONTEXT_NOT_READY, phase));
                var ability = abi.GetAbility(call.Contract);
                if (!ability.Success) return AbiReply<AbiValue>.Fail(ability.Error!);
                var remaining = budget - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) return AbiReply<AbiValue>.Fail(Error(AbiErrors.DEADLINE_EXCEEDED, phase));
                var options = new InvocationOptions(call.OperationId, remaining, token)
                { StageInvocationId = stage?.StageInvocationId ?? Guid.Empty, CorrelationId = correlation, IdempotencyKey = call.IdempotencyKey, ExpectedRevision = call.ExpectedRevision };
                return await ability.Value.Invoke(call.Contract, call.Method, call.Arguments, options).ConfigureAwait(false);
            }, budget, cancellation).ConfigureAwait(false);
            var fields = DataCodec.ReadMap(result);
            return DataCodec.ReadBoolean(DataCodec.Field(fields, "success")) ? AbiReply<Unit>.Ok(new()) :
                AbiReply<Unit>.Fail(Error(fields.TryGetValue("code", out var code) ? DataCodec.ReadString(code) : AbiErrors.PLUGIN_EXCEPTION, phase));
        }
        catch (OperationCanceledException) { return AbiReply<Unit>.Fail(Error(AbiErrors.DEADLINE_EXCEEDED, phase)); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        { return AbiReply<Unit>.Fail(Error(AbiErrors.PLUGIN_EXCEPTION, phase)); }
    }
    private AbiError Error(string code, string phase) => new(code, code, phase, "plugin.callback", _identity?.PluginId ?? manifest.PluginId,
        _identity?.LoadInstanceId ?? Guid.Empty, Guid.NewGuid(), false, ImmutableDictionary<string, string>.Empty);
    public ValueTask DisposeAsync() => machine.DisposeAsync();
}
