using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.MarketDelivery;

namespace PluginSidecar.Application;

/// <summary>Native transaction for an explicitly approved package. Does not obtain consent or own startup.</summary>
/// <remarks>The management owner must serialize restoration and disable with installation calls.</remarks>
public sealed class PluginMarketInstaller(PluginApplication application, PluginMarketCatalog catalog,
    Func<PluginInstallationPolicy> readPolicy)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PluginLoadReport> InstallAsync(EncryptedDelivery delivery, string id, string version, string subject,
        ImmutableArray<string> approvedGrants, bool remember, Guid approvedSession, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (delivery.Grant.Length is < 1 or > 8192 || delivery.Signature.Length is < 1 or > 1024
            || delivery.Ciphertext.Length is < 1 or > 25 * 1024 * 1024) throw new InvalidDataException("plugin.market.delivery-invalid");
        var snapshot = new EncryptedDelivery(delivery.Grant.ToArray(), delivery.Signature.ToArray(), delivery.Ciphertext.ToArray());
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await InstallCoreAsync(snapshot, id, version, subject, approvedGrants, remember, approvedSession, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<PluginLoadReport> InstallCoreAsync(EncryptedDelivery delivery, string id, string version, string subject,
        ImmutableArray<string> grants, bool remember, Guid session, CancellationToken token)
    {
        EnsurePolicy(session, token);
        if (application.Runtime.Active.Any(owner => owner.PluginId == id)) throw new IOException("plugin.install.already-active");
        using var package = catalog.OpenPackage(delivery, id, version, subject);
        if (grants.IsDefault || grants.Length > 128 || grants.Distinct(StringComparer.Ordinal).Count() != grants.Length ||
            grants.Any(grant => !package.Manifest.RequestedPermissions.Contains(grant))) throw new IOException("plugin.market.grants-invalid");

        PluginMarketCatalog.Snapshot? previous = null;
        string? written = null;
        PluginLoadReport? report = null;
        if (remember)
        {
            previous = await catalog.CaptureAsync(id, token).ConfigureAwait(false);
            string? observed = previous?.Revision;
            EnsurePolicy(session, token);
            written = await catalog.SaveIfUnchangedAsync(delivery, id, version, subject, grants, observed, token).ConfigureAwait(false);
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

    private async Task RollbackAsync(string id, string? written, PluginMarketCatalog.Snapshot? previous)
    {
        if (written is null) return;
        if (previous is null)
            _ = await catalog.RemoveIfUnchangedAsync(id, written, CancellationToken.None).ConfigureAwait(false);
        else
            _ = await catalog.RestoreIfUnchangedAsync(previous, written, CancellationToken.None).ConfigureAwait(false);
    }

    private void EnsurePolicy(Guid session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var policy = readPolicy();
        if (!policy.CanLoadMarket || policy.Session != session) throw new InvalidOperationException("plugin.install.unavailable");
    }
}
