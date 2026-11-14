using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Host;
using ShellData = Nexa.Sidecar.Host.Shell.Data;

namespace PluginSidecar.Business;

public sealed record PluginShellEffects(
    Func<string, CancellationToken, Task> Reveal,
    Func<Uri, CancellationToken, Task> OpenUri,
    Func<CancellationToken, Task<(string Id, int Width, int Height, double Scale)[]>> Displays);

public sealed partial class PluginBusinessRoutes
{
    private PluginShellEffects? _shell;
    public void BindShell(PluginShellEffects effects) => _shell = effects;
    private PluginShellEffects Shell => _shell ?? throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);

    /// <summary>Call DiscoverAsync before binding; no synchronous IPC or background fire-and-forget work.</summary>
    public void BindShell(SidecarShellClient client) => BindShell(new PluginShellEffects(
        async (path, token) =>
        {
            await Command(client.Commands, ShellData.SidecarShellRoutes.RevealDirectory, new ShellData.HostDirectoryRequest(path), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        },
        async (uri, token) =>
        {
            await Command(client.Commands, ShellData.SidecarShellRoutes.OpenHttps, new ShellData.HostHttpsRequest(uri.AbsoluteUri), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        },
        async token =>
        {
            var value = await Query<ShellData.HostShellEmpty, ShellData.HostDisplays>(client.Queries, ShellData.SidecarShellRoutes.Displays, new(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (value.Items.Length > 32 || value.Items.Any(item => string.IsNullOrWhiteSpace(item.Id))
                || value.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != value.Items.Length)
                throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            return value.Items.Select(item => (item.Id, item.Width, item.Height, item.Scale)).ToArray();
        }));
}
