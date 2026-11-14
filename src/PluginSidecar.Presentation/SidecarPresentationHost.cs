using System.Text;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Pages = Nexa.Sidecar.Host.PageControl.Data;
using Views = Nexa.Sidecar.Host.ViewState.Data;

namespace PluginSidecar.Presentation;

/// <summary>Generated binary Host clients; no launcher service or plugin DTO is sent across IPC.</summary>
public sealed class SidecarPresentationHost : IPluginPresentationHost
{
    private readonly SidecarViewStateClient _views;
    private readonly SidecarPageControlClient _pages;
    private SidecarPresentationHost(SidecarHostRequestClient requests, IXsrDispatchObserver observer)
    { _views = new(requests, observer); _pages = new(requests, observer); }

    public static async ValueTask<SidecarPresentationHost> CreateAsync(SidecarHostRequestClient requests, IXsrDispatchObserver observer, CancellationToken token = default)
    {
        var host = new SidecarPresentationHost(requests, observer);
        Check(await host._views.DiscoverAsync(token).ConfigureAwait(false), token);
        Check(await host._pages.DiscoverAsync(token).ConfigureAwait(false), token);
        return host;
    }

    public async ValueTask<long> ReadRevisionAsync(CancellationToken token)
    {
        if (!_views.Queries.TryResolve(Views.SidecarViewStateRoutes.Revision, out var route)) throw new InvalidOperationException("Host view-state query unavailable.");
        var result = await _views.Queries.QueryAsync<Views.HostViewEmpty, Views.HostViewRevision>(route, new(), cancellationToken: token).ConfigureAwait(false);
        Check(result.Error, token);
        if (result.Value.Revision < 0 || result.Value.Revision == long.MaxValue) throw new InvalidDataException("Invalid presentation revision.");
        return result.Value.Revision;
    }

    public async ValueTask ReplaceAsync(long revision, IReadOnlyList<IPluginHostView> views, CancellationToken token)
    {
        if (!_views.Commands.TryResolve(Views.SidecarViewStateRoutes.Replace, out var route)) throw new InvalidOperationException("Host view-state replacement unavailable.");
        Check(await _views.Commands.Dispatch(route, new Views.HostViewReplace(revision,
            views.Select(view => SidecarNxmlDocument.Encode(view.Document)).ToArray(), Models(views)), cancellationToken: token).Completion.ConfigureAwait(false), token);
    }

    public async ValueTask UpdateModelsAsync(long revision, IReadOnlyList<IPluginHostView> views, CancellationToken token)
    {
        if (!_views.Commands.TryResolve(Views.SidecarViewStateRoutes.Models, out var route)) throw new InvalidOperationException("Host view-state models unavailable.");
        Check(await _views.Commands.Dispatch(route, new Views.HostViewModelsUpdate(revision, Models(views)), cancellationToken: token).Completion.ConfigureAwait(false), token);
    }

    public async ValueTask SetOpenAsync(long revision, int document, bool open, CancellationToken token)
    {
        if (!_pages.Commands.TryResolve(open ? Pages.SidecarPageControlRoutes.Open : Pages.SidecarPageControlRoutes.Close, out var route)) throw new InvalidOperationException("Host page control unavailable.");
        Check(await _pages.Commands.Dispatch(route, new Pages.HostPageChange(revision, document), cancellationToken: token).Completion.ConfigureAwait(false), token);
    }

    public async ValueTask<IReadOnlyList<int>> ReadVisibleAsync(long revision, CancellationToken token)
    {
        if (!_pages.Queries.TryResolve(Pages.SidecarPageControlRoutes.State, out var route)) throw new InvalidOperationException("Host page state unavailable.");
        var result = await _pages.Queries.QueryAsync<Pages.HostPageQuery, Pages.HostPageState>(route, new(revision), cancellationToken: token).ConfigureAwait(false);
        Check(result.Error, token);
        var state = result.Value;
        if (state.Revision != revision || state.Pages.Length > 32 || state.Pages.Any(page => page.Document < 0)
            || state.Pages.Select(page => page.Document).Distinct().Count() != state.Pages.Length)
            throw new InvalidDataException("Invalid presentation state.");
        return state.Pages.Where(page => page.Visible).Select(page => page.Document).ToArray();
    }

    private static Views.HostViewModel[] Models(IReadOnlyList<IPluginHostView> views) => views.Select((view, index) =>
        new Views.HostViewModel(index, view.Model.OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => new Views.HostViewValue(field.Key, Encoding.UTF8.GetBytes(field.Value))).ToArray())).ToArray();
    private static void Check(XsrResult result, CancellationToken token) => Check(result.Error, token);
    private static void Check(XsrError? error, CancellationToken token)
    {
        if (error is null) return;
        token.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Host presentation operation failed: " + error.Code.Value);
    }
}
