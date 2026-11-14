using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Host.Clipboard.Data;
using Nexa.Xsr;

namespace PluginSidecar.Business;

/// <summary>General clipboard transport. Guest permission, confirmation and blob ownership stay in the business layer.</summary>
public sealed class SidecarClipboard(SidecarClipboardClient client, Action<Exception> cleanupFailure)
{
    private const int ImageLimit = 16 * 1024 * 1024, TextLimit = 65536, ChunkLimit = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SemaphoreSlim BeginGate = new(1, 1);
    private static long _nextId;

    public async Task<(string? Text, byte[]? Image)> ReadAsync(CancellationToken token)
    {
        long id = 0;
        try
        {
            HostClipboardSnapshot snapshot;
            await BeginGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested(); id = AllocateId();
                snapshot = await Query<HostClipboardTransfer, HostClipboardSnapshot>(SidecarClipboardRoutes.Read, new(id), token).ConfigureAwait(false);
            }
            catch
            {
                if (id > 0) { await Release(id).ConfigureAwait(false); id = 0; }
                throw;
            }
            finally { BeginGate.Release(); }
            if (snapshot.TextUtf8?.Length > TextLimit * 4 || !ValidDescriptor(snapshot.ImageLength, snapshot.ImageSha256)) throw Invalid();
            string? text;
            try { text = snapshot.TextUtf8 is null ? null : Utf8.GetString(snapshot.TextUtf8); }
            catch (DecoderFallbackException) { throw Invalid(); }
            if (text?.Length > TextLimit) throw Invalid();
            byte[]? image = snapshot.ImageLength < 0 ? null : new byte[snapshot.ImageLength];
            if (image is not null)
            {
                for (int offset = 0; offset < image.Length;)
                {
                    int count = Math.Min(ChunkLimit, image.Length - offset);
                    var part = await Query<HostClipboardReadChunk, HostClipboardChunk>(SidecarClipboardRoutes.ReadChunk, new(id, offset, count), token).ConfigureAwait(false);
                    if (part.Offset != offset || part.Bytes is null || part.Bytes.Length != count) throw Invalid();
                    part.Bytes.CopyTo(image, offset); offset += count;
                }
                if (!SHA256.HashData(image).AsSpan().SequenceEqual(snapshot.ImageSha256)) throw Invalid();
                ValidatePng(image);
            }
            token.ThrowIfCancellationRequested(); return (text, image);
        }
        finally { if (id > 0) await Release(id).ConfigureAwait(false); }
    }
    public async Task WriteAsync(string? text, byte[]? image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (text?.Length > TextLimit) throw Invalid();
        byte[]? encoded;
        try { encoded = text is null ? null : Utf8.GetBytes(text); }
        catch (EncoderFallbackException) { throw Invalid(); }
        if (image is not null) ValidatePng(image);
        // The caller can reuse a blob copy while IPC is suspended; stage our own bytes.
        image = image?.ToArray(); byte[]? hash = image is null ? null : SHA256.HashData(image);
        long id = 0;
        try
        {
            await BeginGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested(); id = AllocateId();
                await Command(SidecarClipboardRoutes.BeginWrite, new HostClipboardWrite(id, encoded, image?.Length ?? -1, hash), token).ConfigureAwait(false);
            }
            catch
            {
                if (id > 0) { await Release(id).ConfigureAwait(false); id = 0; }
                throw;
            }
            finally { BeginGate.Release(); }
            if (image is not null)
                for (int offset = 0; offset < image.Length; offset += ChunkLimit)
                    await Command(SidecarClipboardRoutes.Append, new HostClipboardAppend(id, offset,
                        image.AsSpan(offset, Math.Min(ChunkLimit, image.Length - offset)).ToArray()), token).ConfigureAwait(false);
            // Never replay a commit whose acknowledgement was lost: the OS may already own the data.
            await Command(SidecarClipboardRoutes.Commit, new HostClipboardTransfer(id), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        finally { if (id > 0) await Release(id).ConfigureAwait(false); }
    }
    private async Task<TOut> Query<TIn, TOut>(XsrSemanticId semantic, TIn value, CancellationToken token) where TIn : notnull
    {
        token.ThrowIfCancellationRequested();
        if (!client.Queries.TryResolve(semantic, out var route)) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        var result = await client.Queries.QueryAsync<TIn, TOut>(route, value, cancellationToken: token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess) throw Failure(result.Error!);
        return result.Value!;
    }
    private async Task Command<T>(XsrSemanticId semantic, T value, CancellationToken token) where T : notnull
    {
        token.ThrowIfCancellationRequested();
        if (!client.Commands.TryResolve(semantic, out var route)) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        var result = await client.Commands.Dispatch(route, value, cancellationToken: token).Completion.ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); if (!result.IsSuccess) throw Failure(result.Error!);
    }
    private async Task Release(long id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await Command(SidecarClipboardRoutes.Release, new HostClipboardTransfer(id), deadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            try { cleanupFailure(error); }
            catch (Exception reporting) when (reporting is not OutOfMemoryException and not AccessViolationException) { }
        }
    }
    private static long AllocateId()
    {
        // Once exhausted, stay exhausted. Never wrap around into an earlier activation's ID space.
        long before;
        do { before = Volatile.Read(ref _nextId); if (before == long.MaxValue) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE); }
        while (Interlocked.CompareExchange(ref _nextId, before + 1, before) != before);
        return before + 1;
    }
    private static bool ValidDescriptor(int length, byte[]? hash) => length == -1 ? hash is null : length is >= 33 and <= ImageLimit && hash?.Length == 32;
    private static void ValidatePng(byte[] bytes)
    {
        if (bytes.Length is < 33 or > ImageLimit || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw Invalid();
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)), height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > 4096 || height is 0 or > 4096 || (ulong)width * height > 4 * 1024 * 1024) throw Invalid();
    }
    private static AbiHostException Invalid() => new(AbiErrors.SCHEMA_INVALID);
    private static AbiHostException Failure(XsrError error) => new(error.Kind switch
    {
        XsrErrorKind.Cancelled => AbiErrors.CANCELLED,
        XsrErrorKind.TimedOut => AbiErrors.DEADLINE_EXCEEDED,
        _ => AbiErrors.CAPABILITY_UNAVAILABLE
    });
}
