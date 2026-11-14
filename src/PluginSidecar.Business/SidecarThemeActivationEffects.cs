using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Host;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Data = Nexa.Sidecar.Host.ThemeActivation.Data;

namespace PluginSidecar.Business;

public sealed record PluginThemeActivationEffects(
    Func<string, string, long, bool, CancellationToken, Task<XsrResult<long>>> Activate,
    Func<long, CancellationToken, Task<XsrResult>> Revert);

/// <summary>Assigns preview identities before IPC so uncertain activations can be ended safely.</summary>
public static class SidecarThemeActivationEffects
{
    private static long _nextLease;
    public static PluginThemeActivationEffects Create(SidecarThemeActivationClient client, Action<XsrError> cleanupFailed)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(cleanupFailed);
        Task<XsrResult> Send<T>(XsrSemanticId name, T input, CancellationToken token) where T : notnull
        {
            token.ThrowIfCancellationRequested();
            return client.Commands.TryResolve(name, out var route)
                ? client.Commands.Dispatch(route, input, cancellationToken: token).Completion
                : throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        }
        Task<XsrResult> End(long id, CancellationToken token) => Send(Data.SidecarThemeActivationRoutes.EndPreview, new Data.HostThemePreviewEnd(id), token);
        async Task Compensate(long id)
        {
            XsrError? failure = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { failure = (await End(id, deadline.Token).ConfigureAwait(false)).Error; }
            catch (OperationCanceledException) { failure = new(XsrErrorKind.TimedOut, XsrSemanticId.Parse("xsr.timed_out"), "Preview release was not acknowledged."); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { failure = new(XsrErrorKind.Unavailable, XsrSemanticId.Parse("xsr.unavailable"), "Preview release was not acknowledged."); }
            if (failure is not null)
                try { cleanupFailed(failure); }
                catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
        }
        return new(async (identity, revision, expected, preview, token) =>
        {
            var input = new Data.HostThemeActivation(identity, revision, expected);
            long id = preview ? Interlocked.Increment(ref _nextLease) : 0;
            if (preview && id <= 0) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
            bool completed = false;
            try
            {
                var result = preview
                    ? await Send(Data.SidecarThemeActivationRoutes.Preview, new Data.HostThemePreview(input, id), token).ConfigureAwait(false)
                    : await Send(Data.SidecarThemeActivationRoutes.Activate, input, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!result.IsSuccess) return XsrResult.Failure<long>(result.Error!);
                completed = true;
                return XsrResult.Success(id);
            }
            catch (OperationCanceledException) { return XsrResult.Failure<long>(XsrRuntimeErrors.Cancelled()); }
            finally { if (preview && !completed) await Compensate(id).ConfigureAwait(false); }
        }, End);
    }
}
