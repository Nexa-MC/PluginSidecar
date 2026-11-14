using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

/// <summary>Owns one Sidecar session's page publication, revision and action admission.</summary>
public sealed class PluginPagePublisher : IAsyncDisposable
{
    public const string ManagementCommand = PluginManagementView.Command;
    private readonly PluginApplication _application;
    private readonly IPluginPresentationHost _host;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _lifetime;
    private PluginPreparedView[] _published = [];
    private IPluginHostView[] _documents = [];
    private readonly PluginManagementOptions? _management;
    private PluginManagementView? _manager;
    private int _managementWorking;
    private long? _revision;
    private string _epoch = Guid.NewGuid().ToString("N");
    private int _closed;

    public PluginPagePublisher(PluginApplication application, IPluginPresentationHost host, PluginManagementOptions? management = null)
    { _application = application; _host = host; _management = management; _lifetime = _stop.Token; }

    /// <summary>Publish the current active ordinary pages. Call again explicitly after an uncertain Host outcome.</summary>
    public async ValueTask SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            var snapshot = _application.Snapshot();
            var pages = snapshot.Pages.Where(page => _application.Runtime.Active.Contains(page.Owner))
                .OrderBy(page => page.Page.Id.Value, StringComparer.Ordinal).ToArray();
            if (pages.Length > 32) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var contributions = _application.AdvancedUi();
            var next = Prepare(snapshot, pages, contributions, _epoch);
            bool replace = _revision is null || !SameSources(_documents, next.Documents);
            if (replace)
            {
                string epoch = Guid.NewGuid().ToString("N");
                next = Prepare(snapshot, pages, contributions, epoch);
                // Validate the whole candidate before affecting the acknowledged source set.
                ValidateBudget(next.Documents);
                long revision = _revision ?? await _host.ReadRevisionAsync(stop.Token).ConfigureAwait(false);
                ClearPublished();
                await _host.ReplaceAsync(revision, next.Documents, stop.Token).ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                _revision = checked(revision + 1); _epoch = epoch; Install(next);
                _application.ReportVisiblePages([]);
            }
            else if (!SameModels(_documents, next.Documents))
            {
                ValidateBudget(next.Documents);
                long revision = _revision!.Value;
                ClearPublished();
                await _host.UpdateModelsAsync(revision, next.Documents, stop.Token).ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                _revision = checked(revision + 1); Install(next);
            }
            else Install(next); // Typed models and guest revisions may change without changing rendered text.
        }
        catch
        {
            // A lost response may conceal a committed Host mutation. Never admit old tokens on guesswork.
            ClearPublished(); _revision = null; _application.ReportVisiblePages([]);
            throw;
        }
        finally { _serial.Release(); }
    }

    public async ValueTask SetOpenAsync(PluginIdentity owner, QualifiedId page, bool open, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            int index = Array.FindIndex(_documents, view => view is PluginPreparedView guest && !guest.IsWorkspace && guest.Owner == owner && guest.Page == page);
            if (index < 0 || _revision is null || !IsLive((PluginPreparedView)_documents[index])) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
            await _host.SetOpenAsync(_revision.Value, index, open, stop.Token).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    public async ValueTask ReportVisibilityAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            if (_revision is null) { _application.ReportVisiblePages([]); return; }
            var visible = await _host.ReadVisibleAsync(_revision.Value, stop.Token).ConfigureAwait(false);
            if (visible.Any(index => index < 0 || index >= _documents.Length || _documents[index].Document.Operation != SidecarNxmlOperation.Page)
                || visible.Distinct().Count() != visible.Count)
                throw new InvalidDataException("Host returned invalid page visibility.");
            _application.ReportVisiblePages(visible.Select(index => _documents[index]).OfType<PluginPreparedView>()
                .Where(view => !view.IsWorkspace && IsLive(view)).Select(view => view.Page).ToImmutableArray());
        }
        catch { _application.ReportVisiblePages([]); throw; }
        finally { _serial.Release(); }
    }

    /// <summary>Invoke the ordinary asynchronous guest action pipeline, including its confirmation policy.</summary>
    public async ValueTask<OperationRef> InvokeActionAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        ActionRequest request;
        PluginIdentity owner;
        await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            var form = SidecarUiForms.Decode(payload);
            var view = _published.SingleOrDefault(candidate => candidate.OwnsAction(form.Argument));
            if (view is null || !IsLive(view)) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
            owner = view.Owner; request = view.TranslateAction(payload, owner);
        }
        finally { _serial.Release(); }
        var operation = Abi100Catalog.Operations.Single(operation => operation.Group == "Actions" && operation.Name == "Invoke");
        var result = await _application.Runtime.InvokeAsync(owner, operation.ContractId, operation.MethodId, ActionRequest.Write(request),
            new(Guid.NewGuid(), TimeSpan.FromSeconds(30), stop.Token) { IdempotencyKey = request.IdempotencyKey }).ConfigureAwait(false);
        if (!result.Success) throw new AbiHostException(result.Error!.Code);
        return OperationRef.Read(result.Value);
    }

    public async ValueTask OpenManagerAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            if (_manager is null || _revision is null) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
            await _host.SetOpenAsync(_revision.Value, Array.IndexOf(_documents, _manager), true, stop.Token).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    /// <summary>Handle only the separately registered management command, never a guest action.</summary>
    public async ValueTask InvokeManagementAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        PluginManagementAction action;
        await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            var form = SidecarUiForms.Decode(payload);
            if (_manager is null || _management is null || _revision is null || form.Fields.Length != 0
                || !_manager.TryResolve(form.Argument, out var resolved) || resolved is null) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
            action = resolved;
            var policy = ReadManagementPolicy();
            if (policy.Busy) throw new AbiHostException(AbiErrors.CONFLICT);
            switch (action.Kind)
            {
                case PluginManagementActionKind.Import:
                    if (!policy.CanImport) throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
                    break;
                case PluginManagementActionKind.Disable:
                    if (action.Owner is null || !_application.Runtime.Active.Contains(action.Owner)) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
                    break;
                case PluginManagementActionKind.OpenPage:
                    int index = Array.FindIndex(_documents, view => view is PluginPreparedView guest && !guest.IsWorkspace
                        && guest.Owner == action.Owner && guest.Page == action.Page && IsLive(guest));
                    if (index < 0) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
                    await _host.SetOpenAsync(_revision.Value, index, true, stop.Token).ConfigureAwait(false);
                    return;
                default: throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            }
            Volatile.Write(ref _managementWorking, 1);
        }
        finally { _serial.Release(); }
        try
        {
            if (action.Kind == PluginManagementActionKind.Import) await _management!.Actions.ImportDeveloperPackageAsync(stop.Token).ConfigureAwait(false);
            else await _management!.Actions.DisableAsync(action.Owner!, stop.Token).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _managementWorking, 0); }
    }

    private PluginManagementPolicy ReadManagementPolicy()
    {
        var policy = _management!.ReadPolicy();
        return policy with { Busy = policy.Busy || Volatile.Read(ref _managementWorking) != 0 };
    }

    private Candidate Prepare(PluginPresentation snapshot, PluginPage[] pages, PluginAdvancedUi ui, string epoch)
    {
        var ordinary = pages.Select(page => PluginViewCompiler.Prepare(page, ui, pages, epoch)).ToArray();
        if (_management is null) return new(ordinary, null, ordinary);
        var active = _application.Runtime.Active;
        var manager = new PluginManagementView(snapshot, active, ordinary, ReadManagementPolicy(), _management, epoch);
        var workspace = ui.Registrations.Where(r => active.Contains(r.Owner) && (r.Kind == "commands"
                || r.Kind == "contributions" && UIContribution.Read(r.Value).View == PluginApplication.WorkspaceView
                || r.Kind == "replacements" && UIViewReplacement.Read(r.Value).View == PluginApplication.WorkspaceView))
            .Select(r => r.Owner).Distinct().OrderBy(owner => owner.PluginId, StringComparer.Ordinal)
            .Select(owner => PluginWorkspaceCompiler.Prepare(owner, ui, pages, manager.WorkspaceTarget, epoch)).ToArray();
        return new([.. ordinary, .. workspace], manager, [manager, manager.Navigation, .. ordinary, .. workspace]);
    }

    private sealed record Candidate(PluginPreparedView[] Guests, PluginManagementView? Manager, IPluginHostView[] Documents);
    private void Install(Candidate value) { _published = value.Guests; _manager = value.Manager; _documents = value.Documents; }
    private void ClearPublished() { _published = []; _documents = []; _manager = null; }

    private bool IsLive(PluginPreparedView view)
    {
        if (!_application.Runtime.Active.Contains(view.Owner)
            || view.ContributionStamp != PluginPageContributions.Stamp(view.Owner, _application.AdvancedUi())) return false;
        var pages = _application.Snapshot().Pages;
        return (view.IsWorkspace || pages.Any(page => page.Owner == view.Owner && page.Page.Id == view.Page))
            && (view.ReplacementDependency is not { } target || pages.Any(page => page.Owner == target.Owner
                && page.Page == target.Page && page.Revision == target.Revision));
    }

    private static bool SameSources(IPluginHostView[] left, IPluginHostView[] right) => left.Length == right.Length
        && left.Zip(right).All(pair => SameOwnership(pair.First, pair.Second)
            && pair.First.Document.Operation == pair.Second.Document.Operation
            && pair.First.Document.Target == pair.Second.Document.Target && pair.First.Document.EntryPoint == pair.Second.Document.EntryPoint
            && pair.First.Document.Files.Count == pair.Second.Document.Files.Count
            && pair.First.Document.Files.All(file => pair.Second.Document.Files.TryGetValue(file.Key, out var text) && file.Value == text));

    private static bool SameOwnership(IPluginHostView first, IPluginHostView second) => (first, second) switch
    {
        (PluginPreparedView a, PluginPreparedView b) => a.Owner == b.Owner && a.Page == b.Page && a.IsWorkspace == b.IsWorkspace && a.ContributionStamp == b.ContributionStamp,
        _ => first.GetType() == second.GetType()
    };

    private static bool SameModels(IPluginHostView[] left, IPluginHostView[] right) => left.Length == right.Length
        && left.Zip(right).All(pair => pair.First.Model.Count == pair.Second.Model.Count
            && pair.First.Model.All(field => pair.Second.Model.TryGetValue(field.Key, out var value) && field.Value == value));

    private static void ValidateBudget(IPluginHostView[] views)
    {
        if (views.Length > 32) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        long sources = 0, models = 0;
        foreach (var view in views)
        {
            sources += SidecarNxmlDocument.Encode(view.Document).Length;
            if (view.Model.Count > 4096) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            foreach (var field in view.Model)
            {
                int size = System.Text.Encoding.UTF8.GetByteCount(field.Value);
                if (string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 256 || field.Key.Any(char.IsControl) || size > 512 * 1024) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
                models += System.Text.Encoding.UTF8.GetByteCount(field.Key) + size;
            }
        }
        if (sources > 4 * 1024 * 1024 || models > 4 * 1024 * 1024) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        try { ClearPublished(); _revision = null; _application.ReportVisiblePages([]); }
        finally { _serial.Release(); }
        _stop.Dispose();
    }
}
