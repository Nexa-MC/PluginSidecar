using Nexa.Sidecar.Host;
using Nexa.Sidecar.Host.DialogControl.Data;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace PluginSidecar.Presentation;

/// <summary>A general Host modal source, identified only inside the invoking Sidecar session.</summary>
public sealed record PluginHostDialogState(int Document, string Target, bool Open);

/// <summary>Controls modal documents under the presentation owner's source/model serialization gate.</summary>
public interface IPluginDialogHost
{
    ValueTask SetOpenAsync(long revision, int document, bool open, CancellationToken token = default);
    ValueTask<IReadOnlyList<PluginHostDialogState>> ReadAsync(long revision, CancellationToken token = default);
}

public sealed class SidecarDialogHost : IPluginDialogHost
{
    private readonly SidecarDialogControlClient _client;
    private SidecarDialogHost(SidecarHostRequestClient requests, IXsrDispatchObserver observer) => _client = new(requests, observer);

    public static async ValueTask<SidecarDialogHost> CreateAsync(SidecarHostRequestClient requests, IXsrDispatchObserver observer, CancellationToken token = default)
    {
        var host = new SidecarDialogHost(requests, observer);
        Check(await host._client.DiscoverAsync(token).ConfigureAwait(false), token);
        return host;
    }

    public async ValueTask SetOpenAsync(long revision, int document, bool open, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentOutOfRangeException.ThrowIfNegative(document);
        if (!_client.Commands.TryResolve(open ? SidecarDialogControlRoutes.Open : SidecarDialogControlRoutes.Close, out var route))
            throw new InvalidOperationException("Host modal control unavailable.");
        Check(await _client.Commands.Dispatch(route, new HostDialogChange(revision, document), cancellationToken: token).Completion.ConfigureAwait(false), token);
    }

    public async ValueTask<IReadOnlyList<PluginHostDialogState>> ReadAsync(long revision, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (!_client.Queries.TryResolve(SidecarDialogControlRoutes.State, out var route)) throw new InvalidOperationException("Host modal state unavailable.");
        var result = await _client.Queries.QueryAsync<HostDialogQuery, HostDialogState>(route, new(revision), cancellationToken: token).ConfigureAwait(false);
        Check(result.Error, token);
        var state = result.Value;
        if (state is null || state.Dialogs is null || state.Revision != revision || state.Dialogs.Length > 32
            || state.Dialogs.Any(dialog => dialog is null || dialog.Document is < 0 or >= 32 || string.IsNullOrWhiteSpace(dialog.Target)
                || dialog.Target.Length > 512 || dialog.Target[0] != '#' || dialog.Target.Length == 1
                || dialog.Target.AsSpan(1).ContainsAny('#', '/') || dialog.Target.Any(char.IsWhiteSpace))
            || state.Dialogs.Select(dialog => dialog.Document).Distinct().Count() != state.Dialogs.Length
            || state.Dialogs.Select(dialog => dialog.Target).Distinct(StringComparer.Ordinal).Count() != state.Dialogs.Length)
            throw new InvalidDataException("Invalid modal presentation state.");
        return state.Dialogs.Select(dialog => new PluginHostDialogState(dialog.Document, dialog.Target, dialog.Open)).ToArray();
    }

    private static void Check(XsrResult result, CancellationToken token) => Check(result.Error, token);
    private static void Check(XsrError? error, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (error is not null) throw new InvalidOperationException("Host modal operation failed: " + error.Code.Value);
    }
}
