using System.Collections.Immutable;
using System.Security.Cryptography;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using PluginSidecar.Packages;

namespace PluginSidecar.Application;

public sealed record PluginInstallationPolicy(Guid Session, bool DeveloperMode, bool Ready, bool SafeMode)
{
    public bool CanLoadMarket => Session != Guid.Empty && Ready && !SafeMode;
    public bool CanLoad => DeveloperMode && CanLoadMarket;
}

/// <summary>Native transaction for an explicitly approved package. Does not obtain consent or own startup.</summary>
/// <remarks>The management owner must serialize restoration and disable with installation calls.</remarks>
public sealed class PluginApprovedInstaller(PluginApplication application, PluginOfflineCatalog catalog,
    Func<PluginInstallationPolicy> readPolicy)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PluginLoadReport> InstallAsync(ReadOnlyMemory<byte> approvedBytes, string id, string version,
        ImmutableArray<string> approvedGrants, bool remember, Guid approvedSession, CancellationToken token)
    {
        if (approvedBytes.Length is < 1 or > 25 * 1024 * 1024) throw new InvalidDataException("plugin.package.size-invalid");
        byte[] bytes = approvedBytes.ToArray();
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try { return await InstallCoreAsync(bytes, id, version, approvedGrants, remember, approvedSession, token).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<PluginLoadReport> InstallCoreAsync(byte[] bytes, string id, string version,
        ImmutableArray<string> grants, bool remember, Guid session, CancellationToken token)
    {
        EnsurePolicy(session, token);
        if (application.Runtime.Active.Any(owner => owner.PluginId == id)) throw new IOException("plugin.install.already-active");
        using var package = VerifiedPluginPackage.Parse(bytes, id, version);
        if (grants.IsDefault || grants.Length > 128 || grants.Distinct(StringComparer.Ordinal).Count() != grants.Length ||
            grants.Any(grant => !package.Manifest.RequestedPermissions.Contains(grant))) throw new IOException("plugin.catalog.grants-invalid");

        PluginOfflineInstall? previous = null;
        string? written = null;
        PluginLoadReport? report = null;
        try
        {
            if (remember)
            {
                string? observed = await catalog.ReadRevisionAsync(id, token).ConfigureAwait(false);
                if (observed is not null)
                {
                    previous = await catalog.OpenInstalledAsync(id, token).ConfigureAwait(false);
                    if (previous.Revision != observed) throw new IOException("plugin.catalog.conflict");
                }
                EnsurePolicy(session, token);
                written = await catalog.SaveIfUnchangedAsync(bytes, id, version, grants, observed, token).ConfigureAwait(false);
            }
            try
            {
                EnsurePolicy(session, token);
                report = await application.LoadAsync(package, grants, token).ConfigureAwait(false);
                EnsurePolicy(session, token);
            }
            catch (Exception failure)
            {
                List<Exception> errors = [failure];
                if (report?.State == LoadState.Active && application.Runtime.Active.Contains(report.Identity))
                {
                    try { await application.StopLoadAsync(report.Identity, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception cleanup) { errors.Add(cleanup); }
                }
                try { await RollbackAsync(id, written, previous).ConfigureAwait(false); }
                catch (Exception cleanup) { errors.Add(cleanup); }
                if (errors.Count != 1) throw new AggregateException("plugin.install.cleanup-failed", errors);
                throw;
            }
            if (report.State != LoadState.Active)
            {
                try { await RollbackAsync(id, written, previous).ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    throw new AggregateException("plugin.install.cleanup-failed",
                        new IOException(report.Error?.Code ?? "plugin.load.failed"), cleanup);
                }
            }
            return report;
        }
        finally { previous?.Dispose(); }
    }

    private async Task RollbackAsync(string id, string? written, PluginOfflineInstall? previous)
    {
        if (written is null) return;
        if (previous is null)
            _ = await catalog.RemoveIfUnchangedAsync(id, written, CancellationToken.None).ConfigureAwait(false);
        else
        {
            try
            {
                await catalog.SaveIfUnchangedAsync(previous.Package, previous.Id, previous.Version, previous.Grants,
                    written, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException error) when (error.Message == "plugin.catalog.conflict") { }
        }
    }

    private void EnsurePolicy(Guid session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var policy = readPolicy();
        if (!policy.CanLoad || policy.Session != session) throw new InvalidOperationException("plugin.install.unavailable");
    }
}
