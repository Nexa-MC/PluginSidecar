using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.MarketDelivery;
using PluginSidecar.Packages;

namespace PluginSidecar.Presentation;

public sealed partial class PluginManagementController
{
    /// <summary>Verify the exact Market delivery, obtain native consent and install under the current session.</summary>
    public async ValueTask<bool> InstallMarketAsync(EncryptedDelivery delivery, string id, string version, string subject, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (delivery.Grant.Length is < 1 or > 8192 || delivery.Signature.Length is < 1 or > 1024
            || delivery.Ciphertext.Length is < 1 or > 25 * 1024 * 1024) throw new InvalidDataException("plugin.market.delivery-invalid");
        var copy = new EncryptedDelivery(delivery.Grant.ToArray(), delivery.Signature.ToArray(), delivery.Ciphertext.ToArray());
        bool installed = false;
        await ExecuteAsync(async stop =>
        {
            await ReconcilePolicyCoreAsync().ConfigureAwait(false);
            var policy = RequirePolicy(stop, market: true);
            if (_marketCatalog is null || _marketInstaller is null) throw new InvalidOperationException("plugin.market.unavailable");
            if (await _catalog.ReadRevisionAsync(id, stop).ConfigureAwait(false) is not null)
                throw new IOException("plugin.install.origin-conflict");
            _boundSession = policy.Session;
            PluginManifest manifest;
            using (var verified = _marketCatalog.OpenPackage(copy, id, version, subject)) manifest = verified.Manifest;
            RequirePolicy(stop, policy.Session, market: true);
            var decision = await _confirm(new(id, version, manifest.RequestedPermissions) { Source = PluginInstallSource.Market }, stop).ConfigureAwait(false);
            RequirePolicy(stop, policy.Session, market: true);
            if (decision == PluginInstallDecision.Declined) return;
            if (decision is not (PluginInstallDecision.SessionOnly or PluginInstallDecision.Remember))
                throw new InvalidOperationException("plugin.install.consent-invalid");
            if (await _catalog.ReadRevisionAsync(id, stop).ConfigureAwait(false) is not null)
                throw new IOException("plugin.install.origin-conflict");
            var result = await _marketInstaller.InstallAsync(copy, id, version, subject, manifest.RequestedPermissions,
                decision == PluginInstallDecision.Remember, policy.Session, stop).ConfigureAwait(false);
            if (result.State != LoadState.Active) throw new AbiHostException(result.Error?.Code ?? "plugin.load.failed");
            _marketLoads.Add(result.Identity); installed = true;
        }, token).ConfigureAwait(false);
        return installed;
    }
}
