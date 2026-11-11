using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace PluginSidecar.Packages;

/// <summary>Static content and optional code share one context and one registration transaction.</summary>
public sealed class DeclarativePluginEntry(PluginManifest manifest, DeclarativeContentPack content, IPluginBootstrapV1? code = null) : IPluginBootstrapV1
{
    public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
    {
        if (code is not null) return await code.OnPreInitialize(context).ConfigureAwait(false);
        var draft = await context.Negotiator.GetABIContext(100, new(Guid.NewGuid(), context.Budget, context.Cancellation)).ConfigureAwait(false);
        if (!draft.Success) return AbiReply<PreInitResult>.Fail(draft.Error!);
        var builder = new Abi100Draft(draft.Value);
        foreach (var required in manifest.RequiredCapabilities) builder = builder.Require(required.ContractId, required.MinimumVersion);
        foreach (var optional in manifest.OptionalCapabilities) builder = builder.Optional(optional.ContractId, optional.MinimumVersion);
        var ready = await builder.Make(context.Negotiator, Guid.NewGuid(), manifest.RequestedPermissions, context.Cancellation).ConfigureAwait(false);
        return ready.Success ? AbiReply<PreInitResult>.Ok(new(ready.Value, manifest.RequiredDependencies, manifest.OptionalDependencies, manifest.Conflicts, [])) : AbiReply<PreInitResult>.Fail(ready.Error!);
    }
    public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext abi, StageContext stage)
    {
        using var api = abi.ForStage100(stage);
        var registration = await api.Definitions.RegisterBatch(new(content.Definitions),
            new(Guid.NewGuid(), stage.RemainingBudget, stage.Cancellation) { IdempotencyKey = "content:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content.Id.Value))) }).ConfigureAwait(false);
        if (!registration.Success) return AbiReply<Unit>.Fail(registration.Error!);
        return code is null ? AbiReply<Unit>.Ok(new()) : await code.OnInitialize(abi, stage).ConfigureAwait(false);
    }
    public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext abi, StageContext stage) => code?.OnPostInitialize(abi, stage) ?? Ok();
    public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext abi, StageContext stage) => code?.OnCheckup(abi, stage) ?? ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed, [], [])));
    public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext abi, StageContext stage) => code?.OnFinalize(abi, stage) ?? Ok();
    public ValueTask<AbiReply<Unit>> OnActivated(ABIContext abi, InvocationContext invocation) => code?.OnActivated(abi, invocation) ?? Ok();
    public ValueTask<AbiReply<Unit>> OnStop(StopContext context) => code?.OnStop(context) ?? Ok();
    public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context) => code?.OnAbort(context) ?? Ok();
    private static ValueTask<AbiReply<Unit>> Ok() => ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
}
