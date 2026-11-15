using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Nexa.Plugin.Bootstrap.V1;
using PluginSidecar.Packages;

namespace PluginSidecar.Presentation;

public sealed partial class PluginManagementController
{
    public async ValueTask<ImmutableArray<PluginRestoreIssue>> RestoreOfflineAsync(CancellationToken token = default)
    {
        var issues = ImmutableArray.CreateBuilder<PluginRestoreIssue>();
        await ExecuteAsync(async stop =>
        {
            await ReconcilePolicyCoreAsync().ConfigureAwait(false);
            var policy = RequirePolicy(stop, market: true);
            if (_restoreAttempt == policy.Session && _restoredDeveloperMode == policy.DeveloperMode) return;
            _boundSession = policy.Session;
            List<RestoreEntry> pending = [];
            if (_marketCatalog is not null)
                foreach (var record in _marketCatalog.Records())
                {
                    RequirePolicy(stop, policy.Session, market: true);
                    try
                    {
                        var install = await _marketCatalog.OpenAsync(record, stop).ConfigureAwait(false);
                        if (await _catalog.ReadRevisionAsync(install.Id, stop).ConfigureAwait(false) is not null)
                        { issues.Add(new(install.Id, "plugin.install.origin-conflict")); continue; }
                        using var package = _marketCatalog.OpenPackage(install.Delivery, install.Id, install.Version, install.Subject);
                        Add(install.Id, install.Version, install.Revision, package, true);
                    }
                    catch (Exception error) when (RecordFailure(error)) { RecordIssue(record); }
                }
            if (policy.DeveloperMode)
                foreach (var record in _catalog.Records())
                {
                    RequirePolicy(stop, policy.Session);
                    try
                    {
                        using var install = await _catalog.OpenAsync(record, stop).ConfigureAwait(false);
                        if (_marketCatalog is not null && await _marketCatalog.ReadRevisionAsync(install.Id, stop).ConfigureAwait(false) is not null)
                        { issues.Add(new(install.Id, "plugin.install.origin-conflict")); continue; }
                        using var package = VerifiedPluginPackage.Parse(install.Package, install.Id, install.Version);
                        Add(install.Id, install.Version, install.Revision, package, false);
                    }
                    catch (Exception error) when (RecordFailure(error)) { RecordIssue(record); }
                }
            // Never choose an installation origin by enumeration order after competing writes.
            var conflicts = pending.GroupBy(entry => entry.Id, StringComparer.Ordinal).Where(group => group.Count() != 1)
                .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            foreach (string id in conflicts) issues.Add(new(id, "plugin.install.origin-conflict"));
            pending.RemoveAll(entry => conflicts.Contains(entry.Id));
            while (pending.Count != 0)
            {
                RequirePolicy(stop, policy.Session, market: true);
                var next = pending.FirstOrDefault(candidate => candidate.Dependencies.All(id => !pending.Any(other => other.Id == id)));
                if (next is null)
                {
                    issues.AddRange(pending.Select(item => new PluginRestoreIssue(item.Id, "plugin.restore.dependencies")));
                    break;
                }
                pending.Remove(next);
                if (_application.Runtime.Active.Any(owner => owner.PluginId == next.Id)) continue;
                try
                {
                    if (next.Market)
                    {
                        var install = await _marketCatalog!.OpenInstalledAsync(next.Id, stop).ConfigureAwait(false);
                        if (install.Revision != next.Revision) { issues.Add(new(next.Id, "plugin.restore.record-changed")); continue; }
                        var result = await _marketInstaller!.InstallAsync(install.Delivery, install.Id, install.Version, install.Subject,
                            install.Grants, false, policy.Session, stop).ConfigureAwait(false);
                        if (result.State == LoadState.Active) _marketLoads.Add(result.Identity);
                        else issues.Add(new(next.Id, result.Error?.Code ?? "plugin.restore.failed"));
                    }
                    else
                    {
                        RequirePolicy(stop, policy.Session);
                        using var install = await _catalog.OpenInstalledAsync(next.Id, stop).ConfigureAwait(false);
                        if (install.Revision != next.Revision) { issues.Add(new(next.Id, "plugin.restore.record-changed")); continue; }
                        var result = await _installer.InstallAsync(install.Package, install.Id, install.Version, install.Grants,
                            false, policy.Session, stop).ConfigureAwait(false);
                        if (result.State != LoadState.Active) issues.Add(new(next.Id, result.Error?.Code ?? "plugin.restore.failed"));
                    }
                }
                catch (Exception error) when (RecordFailure(error)) { issues.Add(new(next.Id, "plugin.restore.failed")); }
            }
            _restoreAttempt = policy.Session; _restoredDeveloperMode = policy.DeveloperMode;
            void Add(string id, string version, string revision, VerifiedPluginPackage package, bool market)
            {
                if (package.Manifest.RequiredDependencies.IsDefault || package.Manifest.RequiredDependencies.Any(d => d is null || string.IsNullOrEmpty(d.PluginId)))
                    throw new InvalidDataException("plugin.package.dependencies-invalid");
                pending.Add(new(id, version, revision, package.Manifest.RequiredDependencies.Select(d => d.PluginId).ToImmutableArray(), market));
            }
            void RecordIssue(string record)
            { issues.Add(new(Path.GetFileNameWithoutExtension(record)[..Math.Min(12, Path.GetFileNameWithoutExtension(record).Length)], "plugin.restore.record-invalid")); }
        }, token).ConfigureAwait(false);
        return issues.ToImmutable();
    }

    private static bool RecordFailure(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or FormatException or JsonException;
    private sealed record RestoreEntry(string Id, string Version, string Revision, ImmutableArray<string> Dependencies, bool Market);
}
