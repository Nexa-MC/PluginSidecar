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
            var policy = RequirePolicy(stop);
            if (_restoreAttempt == policy.Session) return;
            _boundSession = policy.Session;
            List<RestoreEntry> pending = [];
            foreach (var record in _catalog.Records())
            {
                RequirePolicy(stop, policy.Session);
                try
                {
                    using var install = await _catalog.OpenAsync(record, stop).ConfigureAwait(false);
                    using var package = VerifiedPluginPackage.Parse(install.Package, install.Id, install.Version);
                    if (package.Manifest.RequiredDependencies.IsDefault || package.Manifest.RequiredDependencies.Any(d => d is null || string.IsNullOrEmpty(d.PluginId)))
                        throw new InvalidDataException("plugin.package.dependencies-invalid");
                    pending.Add(new(install.Id, install.Version, install.Revision,
                        package.Manifest.RequiredDependencies.Select(d => d.PluginId).ToImmutableArray()));
                }
                catch (Exception error) when (RecordFailure(error))
                { issues.Add(new(Path.GetFileNameWithoutExtension(record)[..Math.Min(12, Path.GetFileNameWithoutExtension(record).Length)], "plugin.restore.record-invalid")); }
            }
            while (pending.Count != 0)
            {
                RequirePolicy(stop, policy.Session);
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
                    using var install = await _catalog.OpenInstalledAsync(next.Id, stop).ConfigureAwait(false);
                    if (install.Revision != next.Revision) { issues.Add(new(next.Id, "plugin.restore.record-changed")); continue; }
                    var result = await _installer.InstallAsync(install.Package, install.Id, install.Version, install.Grants,
                        false, policy.Session, stop).ConfigureAwait(false);
                    if (result.State != LoadState.Active) issues.Add(new(next.Id, result.Error?.Code ?? "plugin.restore.failed"));
                }
                catch (Exception error) when (RecordFailure(error)) { issues.Add(new(next.Id, "plugin.restore.failed")); }
            }
            _restoreAttempt = policy.Session;
        }, token).ConfigureAwait(false);
        return issues.ToImmutable();
    }

    private static bool RecordFailure(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or FormatException or JsonException;
    private sealed record RestoreEntry(string Id, string Version, string Revision, ImmutableArray<string> Dependencies);
}
