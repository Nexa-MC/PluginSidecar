using System.Diagnostics;
using System.Security.Cryptography;

namespace PluginSidecar.Application;

public sealed partial class PluginOfflineCatalog
{
    /// <summary>Read the opaque revision without decrypting or granting access to package contents.</summary>
    public async Task<string?> ReadRevisionAsync(string id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory)) return null;
            using var lease = await AcquireFileGateAsync(token).ConfigureAwait(false);
            return await ReadRevisionCoreAsync(PathFor(id), token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Remove only this record revision. A newer or already absent record is left unchanged.</summary>
    public async Task<bool> RemoveIfUnchangedAsync(string id, string expectedRevision, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ValidateRevision(expectedRevision);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory)) return false;
            using var lease = await AcquireFileGateAsync(token).ConfigureAwait(false);
            string path = PathFor(id);
            if (await ReadRevisionCoreAsync(path, token).ConfigureAwait(false) != expectedRevision) return false;
            token.ThrowIfCancellationRequested(); File.Delete(path); return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<FileStream> AcquireFileGateAsync(CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // Keep the lock file: unlinking it on close can let waiters lock different inodes.
                return new FileStream(Path.Combine(_directory, ".catalog.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33
                || (OperatingSystem.IsMacOS() && (error.HResult & 0xffff) == 35))
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5)) throw new IOException("plugin.catalog.busy", error);
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string?> ReadRevisionCoreAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (file.Length is < 64 or > MaximumBytes) throw new IOException("plugin.catalog.record-invalid");
        return await HashAsync(file, token).ConfigureAwait(false);
    }

    private static async Task<string> HashAsync(Stream stream, CancellationToken token)
        => Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();

    private static void ValidateRevision(string? revision)
    {
        if (revision is not null && (revision.Length != 64 || revision.Any(c => !char.IsAsciiHexDigit(c))))
            throw new ArgumentException("Invalid offline record revision.", nameof(revision));
    }
}
