using System.Net;

namespace PluginSidecar.Business;

public sealed partial class PluginBusinessRoutes
{
    private async Task DownloadFileAsync(PluginDownload item, string stage, CancellationToken token)
    {
        Exception? failure = null;
        foreach (var source in item.Plan.Sources)
        {
            var connection = new PluginDownloadConnection(_pluginHttp, source.Uri.Value, item.Plan.Size);
            try
            {
                token.ThrowIfCancellationRequested();
                long resume = item.Plan.Sha256 is not null && File.Exists(stage) ? new FileInfo(stage).Length : 0;
                var info = await connection.StartAsync(resume, token).ConfigureAwait(false);
                await using var output = new FileStream(stage, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 65536, true);
                output.SetLength(info.From); output.Position = info.From;
                long bytes = info.From;
                byte[] buffer = new byte[65536];
                while (true)
                {
                    int count = await connection.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    bytes += count;
                    lock (_planGate) { item.Bytes = bytes; item.Total = info.Size >= 0 ? info.Size : item.Plan.Size; item.Revision++; }
                    _changes.Writer.TryWrite(true);
                }
                if (info.Size >= 0 && bytes != info.Size) throw new IOException("Download ended before its declared length.");
                await output.FlushAsync(token).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            { failure = error; }
            finally { await connection.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        throw new IOException("All download sources failed.", failure);
    }
    private sealed record DownloadConnectionInfo(long Size, long From, long To, bool Compressed);
    private sealed class PluginDownloadConnection(HttpClient client, string address, long? expected)
    {
        private HttpResponseMessage? _response;
        private Stream? _stream;
        private long _read;
        public async ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (beginOffset > 0) request.Headers.Range = new(beginOffset, null);
            _response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            _response.EnsureSuccessStatusCode();
            long size = _response.Content.Headers.ContentLength ?? -1, from = 0;
            if (_response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (_response.Content.Headers.ContentRange is not { Unit: "bytes", From: { } start, To: { } end, Length: { } total }
                    || start != beginOffset || end != total - 1 || size >= 0 && size != total - start) throw new IOException("Invalid download range.");
                size = total; from = start;
            }
            if (size > MaximumPluginDownloadBytes || expected is { } declared && size >= 0 && size != declared) throw new IOException("Invalid download size.");
            _read = from; _stream = await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new(size, from, size < 0 ? -1 : size - 1, false);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await _stream!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); _read += count;
            if (_read > (expected ?? MaximumPluginDownloadBytes)) throw new IOException("Download exceeded its size limit.");
            return count;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        { _stream?.Dispose(); _response?.Dispose(); _stream = null; _response = null; return ValueTask.CompletedTask; }
    }
}
