using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100.Data;

namespace PluginSidecar.Business;

public sealed partial class PluginBusinessRoutes
{
    private IEnumerable<HostCapabilityRoute> PlatformRoutes => [
        HostRoutes.Typed<Empty, PowerState>("Platform", "QueryPower", async (c, _) =>
        {
            var value = await Query<Nexa.Sidecar.Host.Effects.Data.HostEmptyRequest, Nexa.Sidecar.Host.Effects.Data.HostPowerState>(
                _effectsQueries, Nexa.Sidecar.Host.Effects.Data.SidecarEffectsRoutes.Power, new(), c.Options.Cancellation).ConfigureAwait(false);
            return new(value.OnBattery, value.BatteryPercent, value.Saving);
        }),
        HostRoutes.Typed<Empty, Page<DisplayInfo>>("Platform", "QueryDisplay", async (c, _) =>
        {
            var displays = await Shell.Displays(c.Options.Cancellation).ConfigureAwait(false);
            if (displays.Any(d => d.Width <= 0 || d.Height <= 0 || !double.IsFinite(d.Scale) || d.Scale <= 0)) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
            return ContentPage(displays.Select(d => new DisplayInfo(new(Token(c, "display:" + d.Id)), d.Width, d.Height, d.Scale)).ToImmutableArray(), new(null, 32));
        }),
        HostRoutes.Typed<Empty, PlatformDescription>("Platform", "Describe", (_, _) => ValueTask.FromResult(new PlatformDescription([
            new("nexa.platform/100", 100, Nexa.Plugin.Sdk.Abi100.Data.CapabilityAvailability.Supported, ["application"],
                [new("clipboardTextCharacters", new AbiValue.Int64(65536)), new("clipboardPngBytes", new AbiValue.Int64(16 * 1024 * 1024)),
                 new("clipboardPixels", new AbiValue.Int64(4 * 1024 * 1024)), new("displayLimit", new AbiValue.Int64(32))])
        ])))
    ];
}
