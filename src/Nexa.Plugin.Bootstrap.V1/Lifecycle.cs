using System.Collections.Immutable;
namespace Nexa.Plugin.Bootstrap.V1;

public interface IAbiNegotiatorV1
{
    ValueTask<AbiReply<AbiContextHandle>> GetABIContext(uint version, InvocationOptions options);
    ValueTask<AbiReply<ExperimentRef>> GetExpermentABI(Guid expId, InvocationOptions options);
    ValueTask<AbiReply<AbiContextHandle>> MakeABIContext(AbiContextHandle draft, ImmutableArray<ExperimentRef> experiments, MakeOptions options, CancellationToken cancellation);
}
public interface IAbilityV1
{
    AbiContextHandle Context { get; }
    ValueTask<AbiReply<AbiValue>> Invoke(string contractId, uint methodId, AbiValue arguments, InvocationOptions options);
}
public interface EventAbility : IAbilityV1 { }
public interface UiAbility : IAbilityV1 { }
public interface InterPluginAbility : IAbilityV1 { }
public interface IDiagnosticsV1 { ValueTask<AbiReply<Unit>> ReportDiagnostic(Finding finding, InvocationOptions options); }
public interface IResourcesV1 { ValueTask<AbiReply<RegistrationHandle>> RegisterCleanup(CallbackHandle callback, InvocationOptions options); }
public interface ICallbacksV1
{
    CallbackHandle Register(string contractId, Func<AbiValue, InvocationOptions, ValueTask<AbiReply<AbiValue>>> callback);
    void Release(CallbackHandle callback);
}
public sealed record PreInitContext(PluginIdentity Identity, uint BootstrapVersion, IAbiNegotiatorV1 Negotiator, IDiagnosticsV1 Diagnostics, IResourcesV1 Resources, ICallbacksV1 Callbacks, CancellationToken Cancellation, TimeSpan Budget);
public sealed record StageContext(PluginIdentity Identity, LoadStage Stage, AbiContextHandle FinalABIContext, Guid LoadGroupId, IDiagnosticsV1 Diagnostics, IResourcesV1 Resources, CancellationToken Cancellation, TimeSpan Budget);
public sealed record PreInitResult(AbiContextHandle FinalContext, ImmutableArray<PluginDependency> RequiredDependencies, ImmutableArray<PluginDependency> OptionalDependencies, ImmutableArray<PluginConflict> Conflicts, ImmutableArray<string> DeclaredDegradationModes);
public sealed record CheckupResult(CheckupStatus Status, ImmutableArray<Finding> Findings, ImmutableArray<string> EnabledDegradationModes);
public interface CheckupContext
{
    ValueTask<AbiReply<Unit>> ReportFinding(Finding finding, InvocationOptions options);
    ValueTask<AbiReply<AbiValue>> ProbeService(ServiceHandle service, uint probeId, AbiValue arguments, InvocationOptions options);
    ValueTask<AbiReply<AbiValue>> QueryRegistrations(InvocationOptions options);
    ValueTask<AbiReply<AbiValue>> QueryBindingStatus(InvocationOptions options);
}
public sealed record AbortContext(PluginIdentity Identity, LoadStage FailedStage, AbiError RootCause, ImmutableArray<LoadStage> CompletedStages, AbiValue Resources, CancellationToken Cancellation, TimeSpan Budget);
public sealed record StopContext(PluginIdentity Identity, string ReasonCode, CancellationToken Cancellation, TimeSpan Budget);
public interface IPluginBootstrapV1
{
    ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context);
    ValueTask<AbiReply<Unit>> OnInitialize(StageContext stage, EventAbility events, UiAbility ui);
    ValueTask<AbiReply<Unit>> OnPostInitialize(StageContext stage, InterPluginAbility plugins);
    ValueTask<AbiReply<CheckupResult>> OnCheckup(StageContext stage, CheckupContext checks);
    ValueTask<AbiReply<Unit>> OnFinalize(StageContext stage);
    ValueTask<AbiReply<Unit>> OnAbort(AbortContext context);
    ValueTask<AbiReply<Unit>> OnStop(StopContext context);
}
public static class StageBudgets
{
    public static TimeSpan For(LoadStage stage) => TimeSpan.FromSeconds(stage switch
    {
        LoadStage.PreInitialize => 15,
        LoadStage.Initialize => 30,
        LoadStage.PostInitialize => 15,
        LoadStage.Checkup => 15,
        LoadStage.Finalize => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    });
    public static TimeSpan Cleanup => TimeSpan.FromSeconds(5);
}
