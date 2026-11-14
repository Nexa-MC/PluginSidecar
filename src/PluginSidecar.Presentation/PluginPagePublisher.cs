using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

/// <summary>Owns one Sidecar session's page publication, revision and action admission.</summary>
public sealed partial class PluginPagePublisher : IAsyncDisposable
{
    public const string ManagementCommand = PluginManagementView.Command;
    private readonly PluginApplication _application;
    private readonly IPluginPresentationHost _host;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _lifetime;
    private PluginPreparedView[] _published = [];
    private HashSet<(PluginIdentity Owner, QualifiedId Page)> _visible = [];
    private IPluginHostView[] _documents = [];
    private readonly PluginManagementOptions? _management;
    private PluginManagementView? _manager;
    private int _managementWorking;
    private long? _revision;
    private string _epoch = Guid.NewGuid().ToString("N");
    private int _closed;

    public PluginPagePublisher(PluginApplication application, IPluginPresentationHost host, PluginManagementOptions? management = null, IPluginDialogHost? dialogs = null)
    {
        if (dialogs is not null && host is not IPluginRetainedPresentationHost) throw new ArgumentException("Modal publication requires retained presentation.", nameof(host));
        _application = application; _host = host; _management = management; _lifetime = _stop.Token; _modalHost = dialogs;
    }

    /// <summary>Publish the current active guest views. Call again explicitly after an uncertain Host outcome.</summary>
    public async ValueTask SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            await ObserveDialogsAsync(stop.Token).ConfigureAwait(false);
            await PublishAsync(stop.Token).ConfigureAwait(false);
            await OpenNewDialogsAsync(stop.Token).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }

    private async ValueTask PublishAsync(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var snapshot = _application.Snapshot();
            var pages = snapshot.Pages.Where(page => _application.Runtime.Active.Contains(page.Owner))
                .OrderBy(page => page.Page.Id.Value, StringComparer.Ordinal).ToArray();
            if (pages.Length > 32) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var contributions = _application.AdvancedUi();
            bool retain = _host is IPluginRetainedPresentationHost && _revision is not null;
            var next = retain ? PrepareRetained(snapshot, pages, contributions) : Prepare(snapshot, pages, contributions, _epoch);
            bool replace = _revision is null || !SameSources(_documents, next.Documents);
            if (replace)
            {
                string epoch = Guid.NewGuid().ToString("N");
                if (!retain) next = Prepare(snapshot, pages, contributions, epoch);
                // Validate the whole candidate before affecting the acknowledged source set.
                ValidateBudget(next.Documents);
                long revision = _revision ?? await _host.ReadRevisionAsync(token).ConfigureAwait(false);
                int[] previous = next.Documents.Select(document => Array.FindIndex(_documents, prior => SameSource(prior, document))).ToArray();
                var visible = next.Documents.Where((view, index) => retain && previous[index] >= 0
                    && view is PluginPreparedView guest && !guest.IsWorkspace && guest.DialogReference is null && _visible.Contains((guest.Owner, guest.Page))).OfType<PluginPreparedView>().ToArray();
                ClearPublished();
                if (retain) await ((IPluginRetainedPresentationHost)_host).ReconcileAsync(revision, next.Documents, previous, token).ConfigureAwait(false);
                else await _host.ReplaceAsync(revision, next.Documents, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                _revision = checked(revision + 1); _epoch = epoch; Install(next);
                ReportVisible(visible);
            }
            else if (!SameModels(_documents, next.Documents))
            {
                ValidateBudget(next.Documents);
                long revision = _revision!.Value;
                ClearPublished();
                await _host.UpdateModelsAsync(revision, next.Documents, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                _revision = checked(revision + 1); Install(next);
            }
            else Install(next); // Typed models and guest revisions may change without changing rendered text.
        }
        catch
        {
            // A lost response may conceal a committed Host mutation. Never admit old tokens on guesswork.
            ClearPublished(); _revision = null; ReportVisible([]);
            throw;
        }
    }

    public async ValueTask SetOpenAsync(PluginIdentity owner, QualifiedId page, bool open, CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            int index = Array.FindIndex(_documents, view => view is PluginPreparedView guest && !guest.IsWorkspace && guest.DialogReference is null && guest.Owner == owner && guest.Page == page);
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
            if (_revision is null) { ReportVisible([]); return; }
            var visible = await _host.ReadVisibleAsync(_revision.Value, stop.Token).ConfigureAwait(false);
            if (visible.Any(index => index < 0 || index >= _documents.Length || _documents[index].Document.Operation != SidecarNxmlOperation.Page)
                || visible.Distinct().Count() != visible.Count)
                throw new InvalidDataException("Host returned invalid page visibility.");
            ReportVisible(visible.Select(index => _documents[index]).OfType<PluginPreparedView>());
        }
        catch { ReportVisible([]); throw; }
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
        var dialogs = PrepareDialogs(ui, pages, epoch, retain: false);
        if (_management is null) return new([.. ordinary, .. dialogs], null, [.. ordinary, .. dialogs]);
        var active = _application.Runtime.Active;
        var manager = new PluginManagementView(snapshot, active, ordinary, ReadManagementPolicy(), _management, epoch);
        var workspace = ui.Registrations.Where(r => active.Contains(r.Owner) && (r.Kind == "commands"
                || r.Kind == "contributions" && UIContribution.Read(r.Value).View == PluginApplication.WorkspaceView
                || r.Kind == "replacements" && UIViewReplacement.Read(r.Value).View == PluginApplication.WorkspaceView))
            .Select(r => r.Owner).Distinct().OrderBy(owner => owner.PluginId, StringComparer.Ordinal)
            .Select(owner => PluginWorkspaceCompiler.Prepare(owner, ui, pages, manager.WorkspaceTarget, epoch)).ToArray();
        return new([.. ordinary, .. workspace, .. dialogs], manager, [manager, manager.Navigation, .. ordinary, .. workspace, .. dialogs]);
    }

    private Candidate PrepareRetained(PluginPresentation snapshot, PluginPage[] pages, PluginAdvancedUi ui)
    {
        PluginPreparedView PrepareView(PluginIdentity owner, QualifiedId page, bool workspace, Func<string, PluginPreparedView> compile)
        {
            var prior = _published.SingleOrDefault(view => view.Owner == owner && view.Page == page && view.IsWorkspace == workspace && view.DialogReference is null);
            var candidate = compile(prior?.PublicationEpoch ?? Guid.NewGuid().ToString("N"));
            return prior is not null && !SameSource(prior, candidate) ? compile(Guid.NewGuid().ToString("N")) : candidate;
        }
        var ordinary = pages.Select(page => PrepareView(page.Owner, page.Page.Id, false,
            epoch => PluginViewCompiler.Prepare(page, ui, pages, epoch))).ToArray();
        var dialogs = PrepareDialogs(ui, pages, "", retain: true);
        if (_management is null) return new([.. ordinary, .. dialogs], null, [.. ordinary, .. dialogs]);
        var active = _application.Runtime.Active;
        PluginManagementView Manager(string epoch) => new(snapshot, active, ordinary, ReadManagementPolicy(), _management, epoch);
        var manager = Manager(_manager?.PublicationEpoch ?? Guid.NewGuid().ToString("N"));
        if (_manager is not null && !SameSource(_manager, manager)) manager = Manager(Guid.NewGuid().ToString("N"));
        var workspace = ui.Registrations.Where(r => active.Contains(r.Owner) && (r.Kind == "commands"
                || r.Kind == "contributions" && UIContribution.Read(r.Value).View == PluginApplication.WorkspaceView
                || r.Kind == "replacements" && UIViewReplacement.Read(r.Value).View == PluginApplication.WorkspaceView))
            .Select(r => r.Owner).Distinct().OrderBy(owner => owner.PluginId, StringComparer.Ordinal)
            .Select(owner => PrepareView(owner, PluginApplication.WorkspaceView, true,
                epoch => PluginWorkspaceCompiler.Prepare(owner, ui, pages, manager.WorkspaceTarget, epoch))).ToArray();
        return new([.. ordinary, .. workspace, .. dialogs], manager, [manager, manager.Navigation, .. ordinary, .. workspace, .. dialogs]);
    }

    private sealed record Candidate(PluginPreparedView[] Guests, PluginManagementView? Manager, IPluginHostView[] Documents);
    private void Install(Candidate value) { _published = value.Guests; _manager = value.Manager; _documents = value.Documents; }
    private void ClearPublished() { _published = []; _documents = []; _manager = null; }

    private void ReportVisible(IEnumerable<PluginPreparedView> views)
    {
        _visible = views.Where(view => !view.IsWorkspace && view.DialogReference is null && IsLive(view)).Select(view => (view.Owner, view.Page)).ToHashSet();
        _application.ReportVisiblePages(_visible.Select(view => view.Page).ToImmutableArray());
    }

    private bool IsLive(PluginPreparedView view)
    {
        if (!_application.Runtime.Active.Contains(view.Owner)) return false;
        var ui = _application.AdvancedUi();
        string stamp = view.DialogReference is null ? PluginPageContributions.Stamp(view.Owner, ui)
            : PluginPageContributions.Stamp(view.Owner, ui, view.Page);
        if (view.ContributionStamp != stamp) return false;
        var pages = _application.Snapshot().Pages;
        bool exists = view.DialogReference is { } reference
            ? ui.Dialogs.Any(dialog => dialog.Owner == view.Owner && dialog.Reference == reference && dialog.Page.Revision == view.Revision)
            : view.IsWorkspace || pages.Any(page => page.Owner == view.Owner && page.Page.Id == view.Page);
        return exists
            && (view.ReplacementDependency is not { } target || pages.Any(page => page.Owner == target.Owner
                && page.Page == target.Page && page.Revision == target.Revision));
    }

    private static bool SameSources(IPluginHostView[] left, IPluginHostView[] right) => left.Length == right.Length
        && left.Zip(right).All(pair => SameSource(pair.First, pair.Second));

    private static bool SameSource(IPluginHostView first, IPluginHostView second) => SameOwnership(first, second)
        && first.Document.Operation == second.Document.Operation
        && first.Document.Target == second.Document.Target && first.Document.EntryPoint == second.Document.EntryPoint
        && first.Document.Files.Count == second.Document.Files.Count
        && first.Document.Files.All(file => second.Document.Files.TryGetValue(file.Key, out var text) && file.Value == text);

    private static bool SameOwnership(IPluginHostView first, IPluginHostView second) => (first, second) switch
    {
        (PluginPreparedView a, PluginPreparedView b) => a.Owner == b.Owner && a.Page == b.Page && a.IsWorkspace == b.IsWorkspace && a.DialogReference == b.DialogReference && a.ContributionStamp == b.ContributionStamp,
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
        try { ClearPublished(); _revision = null; ReportVisible([]); }
        finally { _serial.Release(); }
        _stop.Dispose();
    }
}
