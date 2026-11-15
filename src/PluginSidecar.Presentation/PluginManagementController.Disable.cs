using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;

namespace PluginSidecar.Presentation;

public sealed partial class PluginManagementController
{
    public ValueTask DisableAsync(PluginIdentity exactLoad, CancellationToken token) => ExecuteAsync(async stop =>
    {
        var before = _application.Runtime.Active;
        if (!before.Contains(exactLoad)) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
        var revisions = new Dictionary<string, (bool Market, string? Revision, Exception? Error)>(StringComparer.Ordinal);
        foreach (var owner in before)
        {
            bool market = _marketLoads.Contains(owner);
            try
            {
                string? revision = market
                    ? await _marketCatalog!.ReadRevisionAsync(owner.PluginId, stop).ConfigureAwait(false)
                    : await _catalog.ReadRevisionAsync(owner.PluginId, stop).ConfigureAwait(false);
                revisions.Add(owner.PluginId, (market, revision, null));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { revisions.Add(owner.PluginId, (market, null, error)); }
        }
        List<Exception> errors = [];
        try { await _application.StopLoadAsync(exactLoad, stop).ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        // Once retirement starts, cancellation must not leave a matching record enabled for next startup.
        foreach (var retired in before.Where(owner => !_application.Runtime.Active.Contains(owner)))
        {
            var observed = revisions[retired.PluginId];
            if (observed.Error is not null) { errors.Add(observed.Error); continue; }
            if (observed.Revision is null) continue;
            try
            {
                bool removed = observed.Market
                    ? await _marketCatalog!.RemoveIfUnchangedAsync(retired.PluginId, observed.Revision, CancellationToken.None).ConfigureAwait(false)
                    : await _catalog.RemoveIfUnchangedAsync(retired.PluginId, observed.Revision, CancellationToken.None).ConfigureAwait(false);
                if (!removed)
                {
                    string? current = observed.Market
                        ? await _marketCatalog!.ReadRevisionAsync(retired.PluginId, CancellationToken.None).ConfigureAwait(false)
                        : await _catalog.ReadRevisionAsync(retired.PluginId, CancellationToken.None).ConfigureAwait(false);
                    if (current is not null) errors.Add(new IOException("plugin.disable.record-changed"));
                }
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count != 0) throw new AggregateException("plugin.disable.cleanup-failed", errors);
    }, token);
}
