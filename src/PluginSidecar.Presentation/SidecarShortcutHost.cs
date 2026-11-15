using Nexa.Sidecar.Host;
using Nexa.Sidecar.Host.ShortcutControl.Data;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace PluginSidecar.Presentation;

/// <summary>Publishes complete shortcut sets through the session-scoped native Host ABI.</summary>
public sealed class SidecarShortcutHost : IPluginShortcutHost
{
    private readonly SidecarShortcutControlClient _client;
    private SidecarShortcutHost(SidecarHostRequestClient requests, IXsrDispatchObserver observer) => _client = new(requests, observer);

    public static async ValueTask<SidecarShortcutHost> CreateAsync(SidecarHostRequestClient requests, IXsrDispatchObserver observer, CancellationToken token = default)
    {
        var host = new SidecarShortcutHost(requests, observer);
        Check(await host._client.DiscoverAsync(token).ConfigureAwait(false), token);
        return host;
    }

    public async ValueTask ReplaceAsync(long revision, IReadOnlyList<PluginShortcutBinding> bindings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count > 64) throw new ArgumentException("Too many shortcuts.", nameof(bindings));
        var snapshot = bindings.ToArray();
        if (snapshot.Any(binding => binding is null || binding.Document is < 0 or >= 32
            || string.IsNullOrWhiteSpace(binding.Gesture) || binding.Gesture.Length > 64 || binding.Gesture.Any(char.IsControl)
            || binding.Argument is null || binding.Argument.Length > 32768))
            throw new ArgumentException("Invalid shortcut binding.", nameof(bindings));
        if (!_client.Commands.TryResolve(SidecarShortcutControlRoutes.Replace, out var route))
            throw new InvalidOperationException("Host shortcut control unavailable.");
        var request = new HostShortcutSet(revision, snapshot.Select(binding =>
            new HostShortcutBinding(binding.Document, binding.Gesture, PluginViewCompiler.ActionCommand, binding.Argument)).ToArray());
        Check(await _client.Commands.Dispatch(route, request, cancellationToken: token).Completion.ConfigureAwait(false), token);
    }

    private static void Check(XsrResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (result.Error is not null) throw new InvalidOperationException("Host shortcut operation failed: " + result.Error.Code.Value);
    }
}
