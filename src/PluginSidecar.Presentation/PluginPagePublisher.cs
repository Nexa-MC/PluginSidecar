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
    private readonly PluginApplication _application;
    private readonly IPluginPresentationHost _host;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _lifetime;
    private PluginPreparedView[] _published = [];
    private long? _revision;
    private string _epoch = Guid.NewGuid().ToString("N");
    private int _closed;

    public PluginPagePublisher(PluginApplication application, IPluginPresentationHost host)
    { _application = application; _host = host; _lifetime = _stop.Token; }

    /// <summary>Publish the current active ordinary pages. Call again explicitly after an uncertain Host outcome.</summary>
    public async ValueTask SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            var pages = _application.Snapshot().Pages.Where(page => _application.Runtime.Active.Contains(page.Owner))
                .OrderBy(page => page.Page.Id.Value, StringComparer.Ordinal).ToArray();
            if (pages.Length > 32) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var contributions = _application.AdvancedUi();
            var next = pages.Select(page => PluginViewCompiler.Prepare(page, contributions, _epoch)).ToArray();
            bool replace = _revision is null || !SameSources(_published, next);
            if (replace)
            {
                string epoch = Guid.NewGuid().ToString("N");
                next = pages.Select(page => PluginViewCompiler.Prepare(page, contributions, epoch)).ToArray();
                // Validate the whole candidate before affecting the acknowledged source set.
                ValidateBudget(next);
                long revision = _revision ?? await _host.ReadRevisionAsync(stop.Token).ConfigureAwait(false);
                _published = [];
                await _host.ReplaceAsync(revision, next, stop.Token).ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                _revision = checked(revision + 1); _epoch = epoch; _published = next;
                _application.ReportVisiblePages([]);
            }
            else if (!SameModels(_published, next))
            {
                ValidateBudget(next);
                long revision = _revision!.Value;
                _published = [];
                await _host.UpdateModelsAsync(revision, next, stop.Token).ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                _revision = checked(revision + 1); _published = next;
            }
            else _published = next; // Typed models and guest revisions may change without changing rendered text.
        }
        catch
        {
            // A lost response may conceal a committed Host mutation. Never admit old tokens on guesswork.
            _published = []; _revision = null; _application.ReportVisiblePages([]);
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
            int index = Array.FindIndex(_published, view => view.Owner == owner && view.Page == page);
            if (index < 0 || _revision is null || !IsLive(_published[index])) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
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
            if (visible.Any(index => index < 0 || index >= _published.Length) || visible.Distinct().Count() != visible.Count)
                throw new InvalidDataException("Host returned invalid page visibility.");
            _application.ReportVisiblePages(visible.Where(index => IsLive(_published[index])).Select(index => _published[index].Page).ToImmutableArray());
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

    private bool IsLive(PluginPreparedView view) => _application.Runtime.Active.Contains(view.Owner)
        && view.ContributionStamp == PluginPageContributions.Stamp(view.Owner, _application.AdvancedUi())
        && _application.Snapshot().Pages.Any(page => page.Owner == view.Owner && page.Page.Id == view.Page);

    private static bool SameSources(PluginPreparedView[] left, PluginPreparedView[] right) => left.Length == right.Length
        && left.Zip(right).All(pair => pair.First.Owner == pair.Second.Owner && pair.First.Page == pair.Second.Page
            && pair.First.ContributionStamp == pair.Second.ContributionStamp
            && pair.First.Document.Target == pair.Second.Document.Target && pair.First.Document.EntryPoint == pair.Second.Document.EntryPoint
            && pair.First.Document.Files.Count == pair.Second.Document.Files.Count
            && pair.First.Document.Files.All(file => pair.Second.Document.Files.TryGetValue(file.Key, out var text) && file.Value == text));

    private static bool SameModels(PluginPreparedView[] left, PluginPreparedView[] right) => left.Length == right.Length
        && left.Zip(right).All(pair => pair.First.Model.Count == pair.Second.Model.Count
            && pair.First.Model.All(field => pair.Second.Model.TryGetValue(field.Key, out var value) && field.Value == value));

    private static void ValidateBudget(PluginPreparedView[] views)
    {
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
        try { _published = []; _revision = null; _application.ReportVisiblePages([]); }
        finally { _serial.Release(); }
        _stop.Dispose();
    }
}
