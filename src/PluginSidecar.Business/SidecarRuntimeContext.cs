using System.Text;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Host.Context.Data;

namespace PluginSidecar.Business;

public sealed record SidecarRuntimeFacts(string HostVersion, string DataRoot, bool NativeWindowReady);

/// <summary>Native Worker startup facts. Never expose the data root directly to a guest.</summary>
public sealed class SidecarRuntimeContext(SidecarContextClient client)
{
    /// <summary>Call after activation and discovery; the caller supplies the activation lifetime and a finite startup budget.</summary>
    public async Task<SidecarRuntimeFacts> WaitReadyAsync(bool requireNativeWindow, TimeSpan budget, CancellationToken token)
    {
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(budget));
        token.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(budget);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        int delay = 100;
        try
        {
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!client.Queries.TryResolve(SidecarContextRoutes.Snapshot, out var route))
                    throw new InvalidOperationException("Host context is unavailable.");
                var result = await client.Queries.QueryAsync<HostContextQuery, HostContextSnapshot>(route, new(),
                    cancellationToken: lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                if (!result.IsSuccess) throw new InvalidOperationException("Host context query failed: " + result.Error?.Code.Value);
                var snapshot = result.Value;
                if (!Enum.IsDefined(snapshot.Phase)) throw new InvalidDataException("Unknown Host startup phase.");
                if (snapshot.Phase == HostStartupPhase.Stopping) throw new InvalidOperationException("Host is stopping.");
                if (snapshot.NativeWindowReady && snapshot.Phase != HostStartupPhase.Ready)
                    throw new InvalidDataException("Native readiness contradicts Host startup phase.");
                if (snapshot.Phase == HostStartupPhase.Ready && (!requireNativeWindow || snapshot.NativeWindowReady))
                {
                    if (string.IsNullOrWhiteSpace(snapshot.Version) || snapshot.Version.Length > 256 || snapshot.Version.Any(char.IsControl)
                        || snapshot.Utf8DataRoot is null || snapshot.Utf8DataRoot.Length > 131072)
                        throw new InvalidDataException("Invalid Host context metadata.");
                    string path;
                    try { path = new UTF8Encoding(false, true).GetString(snapshot.Utf8DataRoot); }
                    catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid UTF-8 Host data root.", error); }
                    if (!Path.IsPathFullyQualified(path) || path.Contains('\0')) throw new InvalidDataException("Invalid Host data root.");
                    lifetime.Token.ThrowIfCancellationRequested();
                    return new(snapshot.Version, path, snapshot.NativeWindowReady);
                }
                await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 1000);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Host did not become ready within the startup budget."); }
    }
}
