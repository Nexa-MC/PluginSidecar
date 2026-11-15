using System.Security.Cryptography;
using System.Text;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Host.RasterControl.Data;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace PluginSidecar.Presentation;

/// <summary>Uploads owned pixel copies and atomically replaces the session's native raster set.</summary>
public sealed class SidecarRasterHost : IPluginRasterHost, IAsyncDisposable
{
    private static long _nextResource;
    private readonly SidecarRasterControlClient _client;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<string, long> _uploads = new(StringComparer.Ordinal);
    private readonly HashSet<long> _known = [];
    private int _closed;
    private SidecarRasterHost(SidecarHostRequestClient requests, IXsrDispatchObserver observer)
    { _client = new(requests, observer); _lifetime = _stop.Token; }

    public static async ValueTask<SidecarRasterHost> CreateAsync(SidecarHostRequestClient requests, IXsrDispatchObserver observer, CancellationToken token = default)
    {
        var host = new SidecarRasterHost(requests, observer);
        try { Check(await host._client.DiscoverAsync(token).ConfigureAwait(false), token); return host; }
        catch { await host.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask ReplaceAsync(long revision, IReadOnlyList<PluginRasterBinding> bindings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentOutOfRangeException.ThrowIfNegative(revision); ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count > 8) throw new ArgumentException("Too many raster bindings.", nameof(bindings));
        var snapshot = bindings.ToArray();
        if (snapshot.Any(binding => binding is null || binding.Document is < 0 or >= 32
            || string.IsNullOrWhiteSpace(binding.Slot) || binding.Slot.Length > 256 || binding.Slot.Any(char.IsControl)
            || !double.IsFinite(binding.Width) || !double.IsFinite(binding.Height) || binding.Width is < 1 or > 2048 || binding.Height is < 1 or > 2048
            || binding.Image is null || binding.Image.Width != Math.Ceiling(binding.Width) || binding.Image.Height != Math.Ceiling(binding.Height)
            || binding.Image.Png.Length is < 1 or > 16 * 1024 * 1024 || binding.InputToken is null || binding.InputToken.Length > 32768)
            || snapshot.Sum(binding => (long)binding.Image.Width * binding.Image.Height) > 8L * 1024 * 1024
            || snapshot.Sum(binding => (long)binding.Image.Png.Length) > 64L * 1024 * 1024
            || snapshot.Sum(binding => (long)Encoding.UTF8.GetByteCount(binding.InputToken)) > 256 * 1024
            || snapshot.Select(binding => (binding.Document, binding.Slot)).Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Invalid raster set.", nameof(bindings));
        // The caller may change its buffers while waiting for another publication.
        var copies = snapshot.Select(binding => binding.Image.Png.ToArray()).ToArray();
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
            await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                Dictionary<string, long> next = new(StringComparer.Ordinal);
                HostRasterBinding[] native = new HostRasterBinding[snapshot.Length];
                for (int i = 0; i < snapshot.Length; i++)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    byte[] digest = SHA256.HashData(copies[i]); string key = Convert.ToHexString(digest);
                    if (!next.TryGetValue(key, out long id) && !_uploads.TryGetValue(key, out id))
                    {
                        if (_known.Count >= 32) throw new InvalidOperationException("Raster cleanup is pending; retire the native session before allocating more resources.");
                        id = Interlocked.Increment(ref _nextResource);
                        if (id <= 0) throw new InvalidOperationException("Raster identifier space exhausted.");
                        _known.Add(id); // Begin may commit even if its acknowledgement is lost.
                        await Send(SidecarRasterControlRoutes.Begin, new HostRasterBegin(id, copies[i].Length, digest), stop.Token).ConfigureAwait(false);
                        for (int offset = 0; offset < copies[i].Length; offset += 65536)
                            await Send(SidecarRasterControlRoutes.Append, new HostRasterAppend(id, offset,
                                copies[i].AsSpan(offset, Math.Min(65536, copies[i].Length - offset)).ToArray()), stop.Token).ConfigureAwait(false);
                        await Send(SidecarRasterControlRoutes.Seal, new HostRasterRef(id), stop.Token).ConfigureAwait(false);
                    }
                    next[key] = id;
                    var binding = snapshot[i];
                    native[i] = new(binding.Document, binding.Slot, id, binding.Width, binding.Height, PluginViewCompiler.DrawingInputCommand, binding.InputToken);
                }
                await Send(SidecarRasterControlRoutes.Replace, new HostRasterSet(revision, native), stop.Token).ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                _uploads.Clear(); foreach (var pair in next) _uploads.Add(pair.Key, pair.Value);
                await Cleanup(next.Values.ToHashSet()).ConfigureAwait(false);
            }
            catch
            {
                _uploads.Clear();
                // A lost replacement may leave these IDs bound. Release never removes a bound image;
                // retain rejected IDs for cleanup after the next acknowledged absolute replacement.
                await Cleanup([]).ConfigureAwait(false);
                throw;
            }
            finally { _serial.Release(); }
        }
        finally { foreach (var bytes in copies) CryptographicOperations.ZeroMemory(bytes); }
    }

    private async ValueTask Cleanup(HashSet<long> keep)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        foreach (long id in _known.Where(id => !keep.Contains(id)).ToArray())
        {
            try { await Send(SidecarRasterControlRoutes.Release, new HostRasterRef(id), deadline.Token).ConfigureAwait(false); _known.Remove(id); }
            catch (Exception error) when (error is InvalidOperationException or IOException or OperationCanceledException or TimeoutException)
            { if (deadline.IsCancellationRequested) break; }
        }
    }
    private async ValueTask Send<T>(XsrSemanticId semantic, T value, CancellationToken token) where T : notnull
    {
        if (!_client.Commands.TryResolve(semantic, out var route)) throw new InvalidOperationException("Host raster operation unavailable.");
        Check(await _client.Commands.Dispatch(route, value, cancellationToken: token).Completion.ConfigureAwait(false), token);
    }
    private static void Check(XsrResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (result.Error is not null) throw new InvalidOperationException("Host raster operation failed: " + result.Error.Code.Value);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        try { _uploads.Clear(); _known.Clear(); }
        finally { _serial.Release(); }
        _stop.Dispose();
    }
}
