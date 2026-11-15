using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using PluginSidecar.Application;
using PluginSidecar.Packages;

namespace PluginSidecar.Presentation;

public enum PluginInstallDecision { Declined, SessionOnly, Remember }
public enum PluginInstallSource { Developer, Market }
public sealed record PluginInstallConsent(string Id, string Version, ImmutableArray<string> Permissions)
{
    public PluginInstallSource Source { get; init; }
}
public sealed record PluginRestoreIssue(string Id, string Code);

/// <summary>Single native owner for developer/Market management and offline restoration; no automatic startup side effects.</summary>
public sealed partial class PluginManagementController : IPluginManagementActions, IAsyncDisposable
{
    private readonly PluginApplication _application;
    private readonly PluginOfflineCatalog _catalog;
    private readonly PluginApprovedInstaller _installer;
    private readonly PluginMarketCatalog? _marketCatalog;
    private readonly PluginMarketInstaller? _marketInstaller;
    private readonly HashSet<PluginIdentity> _marketLoads = [];
    private bool _activeDeveloperMode, _restoredDeveloperMode;
    private readonly Func<PluginInstallationPolicy> _policy;
    private readonly Func<CancellationToken, ValueTask<string?>> _pick;
    private readonly Func<PluginInstallConsent, CancellationToken, ValueTask<PluginInstallDecision>> _confirm;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _lifetime;
    private readonly object _actionGate = new();
    private readonly object _disposeGate = new();
    private Task? _disposal;
    private CancellationTokenSource? _activeAction;
    private Guid _activeSession;
    private Guid _boundSession, _restoreAttempt;
    private int _closed, _busy;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public PluginManagementController(PluginApplication application, PluginOfflineCatalog catalog,
        Func<PluginInstallationPolicy> policy, Func<CancellationToken, ValueTask<string?>> pick,
        Func<PluginInstallConsent, CancellationToken, ValueTask<PluginInstallDecision>> confirm, PluginMarketCatalog? marketCatalog = null)
    {
        if (!application.Runtime.Active.IsEmpty) throw new InvalidOperationException("Management must bind before loading plugins.");
        _application = application; _catalog = catalog; _policy = policy; _pick = pick; _confirm = confirm;
        _installer = new(application, catalog, policy); _lifetime = _stop.Token;
        _marketCatalog = marketCatalog;
        if (marketCatalog is not null) _marketInstaller = new(application, marketCatalog, policy);
    }

    public ValueTask ImportDeveloperPackageAsync(CancellationToken token) => ExecuteAsync(async stop =>
    {
        await ReconcilePolicyCoreAsync().ConfigureAwait(false);
        var policy = RequirePolicy(stop);
        _boundSession = policy.Session;
        string? path = await _pick(stop).ConfigureAwait(false);
        RequirePolicy(stop, policy.Session);
        if (path is null) return;
        byte[] bytes;
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        {
            if (file.Length is < 1 or > 25 * 1024 * 1024) throw new InvalidDataException("plugin.package.size-invalid");
            bytes = new byte[(int)file.Length];
            try
            {
                await file.ReadExactlyAsync(bytes, stop).ConfigureAwait(false);
                if (file.ReadByte() != -1) throw new InvalidDataException("plugin.package.size-invalid");
            }
            catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }
        try
        {
            PluginManifest manifest;
            using (var archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry("plugin.json");
                if (entry is null || entry.Length > 65536) throw new InvalidDataException("plugin.package.manifest-invalid");
                using var stream = entry.Open();
                manifest = JsonSerializer.Deserialize(stream, PackageJsonContext.Default.PluginManifest)
                    ?? throw new InvalidDataException("plugin.package.manifest-invalid");
            }
            using (var verified = VerifiedPluginPackage.Parse(bytes, manifest.PluginId, manifest.PluginVersion))
                manifest = verified.Manifest;
            RequirePolicy(stop, policy.Session);
            if (_marketCatalog is not null && await _marketCatalog.ReadRevisionAsync(manifest.PluginId, stop).ConfigureAwait(false) is not null)
                throw new IOException("plugin.install.origin-conflict");
            var decision = await _confirm(new(manifest.PluginId, manifest.PluginVersion, manifest.RequestedPermissions), stop).ConfigureAwait(false);
            RequirePolicy(stop, policy.Session);
            if (decision == PluginInstallDecision.Declined) return;
            if (decision is not (PluginInstallDecision.SessionOnly or PluginInstallDecision.Remember))
                throw new InvalidOperationException("plugin.install.consent-invalid");
            if (_marketCatalog is not null && await _marketCatalog.ReadRevisionAsync(manifest.PluginId, stop).ConfigureAwait(false) is not null)
                throw new IOException("plugin.install.origin-conflict");
            var result = await _installer.InstallAsync(bytes, manifest.PluginId, manifest.PluginVersion, manifest.RequestedPermissions,
                decision == PluginInstallDecision.Remember, policy.Session, stop).ConfigureAwait(false);
            if (result.State != LoadState.Active) throw new AbiHostException(result.Error?.Code ?? "plugin.load.failed");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }, token);

    public async ValueTask SynchronizePolicyAsync(CancellationToken token = default)
    {
        var policy = _policy();
        CancellationTokenSource? pending;
        lock (_actionGate)
            pending = !policy.CanLoadMarket || _activeSession != policy.Session || _activeDeveloperMode != policy.DeveloperMode ? _activeAction : null;
        if (pending is not null)
        {
            try { await pending.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
        if (IsBusy && pending is null) return;
        await ExecuteAsync(_ => ReconcilePolicyCoreAsync(), token).ConfigureAwait(false);
    }

    private async ValueTask ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            Volatile.Write(ref _busy, 1);
            lock (_actionGate) { var current = _policy(); _activeAction = linked; _activeSession = current.Session; _activeDeveloperMode = current.DeveloperMode; }
            Exception? failure = null;
            try { await action(linked.Token).ConfigureAwait(false); }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                try { await ReconcilePolicyCoreAsync().ConfigureAwait(false); }
                catch (Exception cleanup) when (failure is not null)
                { throw new AggregateException("plugin.management.cleanup-failed", failure, cleanup); }
            }
        }
        finally
        {
            lock (_actionGate) { _activeAction = null; _activeSession = Guid.Empty; }
            Volatile.Write(ref _busy, 0); _gate.Release();
        }
    }

    private PluginInstallationPolicy RequirePolicy(CancellationToken token, Guid? session = null, bool market = false)
    {
        token.ThrowIfCancellationRequested();
        var policy = _policy();
        if (!(market ? policy.CanLoadMarket : policy.CanLoad) || session is not null && policy.Session != session)
            throw new InvalidOperationException("plugin.install.unavailable");
        return policy;
    }

    private async Task ReconcilePolicyCoreAsync()
    {
        var policy = _policy();
        _marketLoads.RemoveWhere(identity => !_application.Runtime.Active.Contains(identity));
        if (!policy.CanLoadMarket || _boundSession != Guid.Empty && _boundSession != policy.Session)
        {
            _restoreAttempt = Guid.Empty;
            await RetireAllAsync().ConfigureAwait(false);
            _marketLoads.Clear(); _boundSession = Guid.Empty;
        }
        else if (!policy.DeveloperMode)
        {
            List<Exception> errors = [];
            foreach (var identity in _application.Runtime.Active.Where(identity => !_marketLoads.Contains(identity)))
            {
                if (!_application.Runtime.Active.Contains(identity)) continue;
                try { await _application.StopLoadAsync(identity, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            }
            _marketLoads.RemoveWhere(identity => !_application.Runtime.Active.Contains(identity));
            if (errors.Count != 0) throw new AggregateException("plugin.management.retirement-failed", errors);
        }
    }

    private async Task RetireAllAsync()
    {
        List<Exception> errors = [];
        foreach (var identity in _application.Runtime.Active)
        {
            if (!_application.Runtime.Active.Contains(identity)) continue;
            try { await _application.StopLoadAsync(identity, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("plugin.management.retirement-failed", errors);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        Exception? cancellationFailure = null;
        try { await _stop.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { cancellationFailure = error; }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await RetireAllAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (cancellationFailure is not null)
            { throw new AggregateException("plugin.management.cleanup-failed", cancellationFailure, cleanup); }
            if (cancellationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
        }
        finally { _gate.Release(); _stop.Dispose(); }
    }
}
