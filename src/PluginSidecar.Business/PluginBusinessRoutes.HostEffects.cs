using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Host;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Effects = Nexa.Sidecar.Host.Effects.Data;

namespace PluginSidecar.Business;

public sealed partial class PluginBusinessRoutes
{
    private XsrQueryRouter? _effectsQueries;
    private XsrCommandRouter? _effectsCommands;
    private Effects.HostSettingsDefinitions? _settingsDefinitions;
    public void BindEffects(SidecarEffectsClient effects)
        => BindEffects(effects.Commands, effects.Queries);
    public void BindEffects(XsrCommandRouter commands, XsrQueryRouter queries)
    {
        _effectsQueries = queries;
        _effectsCommands = commands;
        BindAccountLaunch(new(async (index, revision, token) =>
        {
            var value = await Query<Effects.HostAccountRequest, Effects.HostAccountEligibility>(_effectsQueries,
                Effects.SidecarEffectsRoutes.AccountEligibility, new(index, revision), token).ConfigureAwait(false);
            return new(value.Allowed, value.CheckSession);
        }, async (index, revision, token) =>
        {
            var result = await commands.Dispatch(Resolve(commands, Effects.SidecarEffectsRoutes.AccountRefresh),
                new Effects.HostAccountRequest(index, revision), cancellationToken: token).Completion.ConfigureAwait(false);
            if (result.IsSuccess) await store.RefreshAsync(token).ConfigureAwait(false);
            return result;
        }));
        BindJava(new(async (path, token) => ProjectCandidate((await Query<Effects.HostJavaInspectRequest, Effects.HostJavaCandidate>(_effectsQueries,
                Effects.SidecarEffectsRoutes.JavaInspect, new(path), token).ConfigureAwait(false)).Candidate),
            async (instance, local, token) => ProjectCandidate((await Query<Effects.HostJavaSelectRequest, Effects.HostJavaCandidate>(_effectsQueries,
                Effects.SidecarEffectsRoutes.JavaSelect, new(instance, local), token).ConfigureAwait(false)).Candidate),
            async (instance, token) =>
            {
                var value = await Query<Effects.HostJavaRequirementsRequest, Effects.JavaRequirementResolution>(_effectsQueries,
                    Effects.SidecarEffectsRoutes.JavaRequirements, new(instance), token).ConfigureAwait(false);
                return new(value.Success, new(value.Range.Minimum, value.Range.Maximum));
            }));
    }
    private static JavaRuntimeCandidate? ProjectCandidate(Effects.JavaRuntimeCandidate? candidate)
    {
        if (candidate is null) return null;
        var java = candidate.Installation;
        return new(new(java.JavaHome, java.JavaExecutablePath, java.WindowedJavaExecutablePath, java.Version,
            (JavaBrand)java.Brand, (JavaArchitecture)java.Architecture, java.Is64Bit, java.IsJre), candidate.IsEnabled, candidate.IsAvailable, (JavaSource)candidate.Source);
    }
    private async ValueTask EnsureSettingsDefinitions(CancellationToken token)
    {
        if (Volatile.Read(ref _settingsDefinitions) is not null) return;
        var value = await Query<Effects.HostEmptyRequest, Effects.HostSettingsDefinitions>(_effectsQueries,
            Effects.SidecarEffectsRoutes.SettingsDefinitions, new(), token).ConfigureAwait(false);
        Interlocked.CompareExchange(ref _settingsDefinitions, value, null);
    }
    private Effects.HostSettingsDefinitions SettingsSchema => Volatile.Read(ref _settingsDefinitions) ?? throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
    private async ValueTask ValidateSetting(string key, SettingsOverride value, CancellationToken token)
    {
        var result = await Query<Effects.HostSettingsValidateRequest, Effects.HostSettingsValidation>(_effectsQueries,
            Effects.SidecarEffectsRoutes.SettingsValidate, new(key, new((Effects.SettingsOverrideMode)value.Mode, value.Value)), token).ConfigureAwait(false);
        if (!result.Valid) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
    }
}
