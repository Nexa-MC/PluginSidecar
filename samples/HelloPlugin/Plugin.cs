using Nexa.Plugin.Bootstrap.V1;
namespace HelloPlugin;

public sealed class Plugin : IPluginBootstrapV1
{
    public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
    {
        var options = new InvocationOptions(Guid.NewGuid(), context.Budget, context.Cancellation);
        var draft = await context.Negotiator.GetABIContext(100, options);
        if (!draft.Success) return AbiReply<PreInitResult>.Fail(draft.Error!);
        var ready = await context.Negotiator.MakeABIContext(draft.Value, [], new(Guid.NewGuid(), []), context.Cancellation);
        return ready.Success ? AbiReply<PreInitResult>.Ok(new(ready.Value, [], [], [], [])) : AbiReply<PreInitResult>.Fail(ready.Error!);
    }
    public ValueTask<AbiReply<Unit>> OnInitialize(StageContext stage, EventAbility events, UiAbility ui) => Ok();
    public ValueTask<AbiReply<Unit>> OnPostInitialize(StageContext stage, InterPluginAbility plugins) => Ok();
    public ValueTask<AbiReply<CheckupResult>> OnCheckup(StageContext stage, CheckupContext checks) => ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed, [], [])));
    public ValueTask<AbiReply<Unit>> OnFinalize(StageContext stage) => Ok();
    public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context) => Ok();
    public ValueTask<AbiReply<Unit>> OnStop(StopContext context) => Ok();
    private static ValueTask<AbiReply<Unit>> Ok() => ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
}
