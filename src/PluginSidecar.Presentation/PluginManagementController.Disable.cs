using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;

namespace PluginSidecar.Presentation;

public sealed partial class PluginManagementController
{
    public ValueTask DisableAsync(PluginIdentity exactLoad, CancellationToken token) => ExecuteAsync(async stop =>
    {
        var before = _application.Runtime.Active;
        if (!before.Contains(exactLoad)) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
        var revisions = new Dictionary<string, (string? Revision, Exception? Error)>(StringComparer.Ordinal);
        foreach (var owner in before)
        {
            try { revisions.Add(owner.PluginId, (await _catalog.ReadRevisionAsync(owner.PluginId, stop).ConfigureAwait(false), null)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { revisions.Add(owner.PluginId, (null, error)); }
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
                if (!await _catalog.RemoveIfUnchangedAsync(retired.PluginId, observed.Revision, CancellationToken.None).ConfigureAwait(false)
                    && await _catalog.ReadRevisionAsync(retired.PluginId, CancellationToken.None).ConfigureAwait(false) is not null)
                    errors.Add(new IOException("plugin.disable.record-changed"));
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count != 0) throw new AggregateException("plugin.disable.cleanup-failed", errors);
    }, token);
}
