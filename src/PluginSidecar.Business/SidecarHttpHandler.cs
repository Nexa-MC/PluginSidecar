using System.Net;
using System.Security.Cryptography;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Host.Http.Data;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace PluginSidecar.Business;

/// <summary>Ordinary HttpClient traffic over the session-owned, Host-configured transport.</summary>
public sealed class SidecarHttpHandler : HttpMessageHandler, IAsyncDisposable
{
    private const int ChunkLimit = 60 * 1024, BodyLimit = 16 * 1024 * 1024;
    private static long _ids;
    private readonly object _gate = new();
    private readonly Dictionary<long, Exchange> _exchanges = [];
    private readonly SemaphoreSlim _buffer = new(1, 1);
    private readonly SidecarHttpClient _client;
    private readonly Action<string> _report;
    private Task? _shutdown;
    private sealed class Exchange : IDisposable
    {
        internal readonly long Id;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly CancellationToken Lifetime;
        internal readonly TaskCompletionSource Sending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Begun, Released;
        internal Task? Cleanup;
        internal Exchange(long id) { Id = id; Lifetime = Stop.Token; }
        public void Dispose() => Stop.Dispose();
    }
    private SidecarHttpHandler(SidecarHostRequestClient requests, IXsrDispatchObserver observer, Action<string> report)
    { _client = new(requests, observer); _report = report; }
    public static async ValueTask<SidecarHttpHandler> CreateAsync(SidecarHostRequestClient requests, IXsrDispatchObserver observer,
        Action<string> report, CancellationToken token = default)
    {
        var handler = new SidecarHttpHandler(requests, observer, report);
        try { Check(await handler._client.DiscoverAsync(token).ConfigureAwait(false), token); return handler; }
        catch { await handler.DisposeAsync().ConfigureAwait(false); throw; }
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var headers = request.Headers.Concat(request.Content?.Headers.AsEnumerable() ?? [])
            .Where(pair => !pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(pair => new HostHttpHeader(pair.Key, pair.Value.ToArray())).ToArray();
        Uri address = request.RequestUri ?? throw new HttpRequestException("An absolute HTTP URI is required.");
        string uri = new UriBuilder(address) { Fragment = string.Empty }.Uri.AbsoluteUri;
        long? declared = request.Content?.Headers.ContentLength;
        if (declared is < 0 or > BodyLimit) throw new HttpRequestException("HTTP upload exceeds its byte budget.");
        Exchange exchange;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            if (_exchanges.Count >= 32) throw new HttpRequestException("HTTP exchanges are still in use.");
            long id = Interlocked.Increment(ref _ids);
            if (id <= 0) throw new InvalidOperationException("HTTP identifier space exhausted.");
            exchange = new(id); _exchanges.Add(id, exchange);
        }
        // Capture the token before cleanup is allowed to dispose its source.
        CancellationToken lifetime = exchange.Lifetime;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        BoundedBody? buffered = null;
        bool bufferHeld = false;
        try
        {
            Stream? body = null; int length = -1;
            if (request.Content is not null)
            {
                if (declared is null)
                {
                    await _buffer.WaitAsync(stop.Token).ConfigureAwait(false); bufferHeld = true;
                    buffered = new();
                    await request.Content.CopyToAsync(buffered, stop.Token).ConfigureAwait(false);
                    buffered.Position = 0; body = buffered; length = checked((int)buffered.Length);
                }
                else { length = checked((int)declared.Value); body = await request.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false); }
            }
            stop.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (exchange.Cleanup is not null) throw new OperationCanceledException(lifetime);
                exchange.Begun = true; // An unacknowledged Begin may still have reserved native resources.
            }
            await Command(SidecarHttpRoutes.Begin, new HostHttpBegin(exchange.Id, request.Method.Method, uri, headers, length), stop.Token).ConfigureAwait(false);
            if (body is not null)
            {
                byte[] bytes = new byte[ChunkLimit];
                try
                {
                    int offset = 0;
                    while (offset < length)
                    {
                        int count = await body.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, length - offset)), stop.Token).ConfigureAwait(false);
                        if (count == 0) throw new HttpRequestException("HTTP body ended before its declared length.");
                        byte[] chunk = bytes.AsSpan(0, count).ToArray();
                        try { await Command(SidecarHttpRoutes.Append, new HostHttpAppend(exchange.Id, offset, chunk), stop.Token).ConfigureAwait(false); }
                        finally { CryptographicOperations.ZeroMemory(chunk); }
                        offset += count;
                    }
                    if (await body.ReadAsync(bytes.AsMemory(0, 1), stop.Token).ConfigureAwait(false) != 0)
                        throw new HttpRequestException("HTTP body exceeds its declared length.");
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            var reply = await Query<HostHttpRef, HostHttpResponse>(SidecarHttpRoutes.Send, new(exchange.Id), stop.Token).ConfigureAwait(false);
            if (reply.StatusCode is < 100 or > 599 || reply.Headers.Length > 128) throw new HttpRequestException("Invalid native HTTP reply.");
            var response = new HttpResponseMessage((HttpStatusCode)reply.StatusCode)
            { RequestMessage = request, Content = new StreamContent(new ResponseStream(this, exchange, lifetime)) };
            try
            {
                foreach (var header in reply.Headers)
                    if (!response.Headers.TryAddWithoutValidation(header.Name, header.Values)
                        && !response.Content.Headers.TryAddWithoutValidation(header.Name, header.Values))
                        throw new HttpRequestException("Invalid native HTTP headers.");
                stop.Token.ThrowIfCancellationRequested();
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        catch { await Release(exchange).ConfigureAwait(false); throw; }
        finally
        {
            buffered?.Dispose(); if (bufferHeld) _buffer.Release();
            exchange.Sending.TrySetResult();
            lock (_gate) if (exchange.Released) _exchanges.Remove(exchange.Id);
        }
    }
    private Task Release(Exchange exchange)
    {
        lock (_gate) return exchange.Cleanup ??= Task.Run(async () =>
        {
            try
            {
                try { await exchange.Stop.CancelAsync().ConfigureAwait(false); }
                catch (AggregateException) { _report("http.cancel.callback-failed"); }
                if (exchange.Begun)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await Command(SidecarHttpRoutes.Release, new HostHttpRef(exchange.Id), deadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
            { _report("http.release.unavailable"); } // Native retirement/expiry owns cleanup when the channel has gone away.
            finally
            {
                lock (_gate) { exchange.Released = true; if (exchange.Sending.Task.IsCompleted) _exchanges.Remove(exchange.Id); }
                exchange.Dispose();
            }
        });
    }
    private async ValueTask Command<T>(XsrSemanticId semantic, T value, CancellationToken token) where T : notnull
    {
        if (!_client.Commands.TryResolve(semantic, out var id)) throw new HttpRequestException("Native HTTP operation unavailable.");
        Check(await _client.Commands.Dispatch(id, value, cancellationToken: token).Completion.ConfigureAwait(false), token);
    }
    private async ValueTask<TOut> Query<TIn, TOut>(XsrSemanticId semantic, TIn value, CancellationToken token) where TIn : notnull
    {
        if (!_client.Queries.TryResolve(semantic, out var id)) throw new HttpRequestException("Native HTTP operation unavailable.");
        var result = await _client.Queries.QueryAsync<TIn, TOut>(id, value, cancellationToken: token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess) throw new HttpRequestException("Native HTTP request failed: " + result.Error?.Code.Value);
        return result.Value!;
    }
    private static void Check(XsrResult result, CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (!result.IsSuccess) throw new HttpRequestException("Native HTTP request failed: " + result.Error?.Code.Value); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _ = Shutdown();
        base.Dispose(disposing);
    }
    private Task Shutdown()
    {
        lock (_gate)
        {
            // Set the closing fence before releasing exchanges, including when the set is empty.
            if (_shutdown is not null) return _shutdown;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _shutdown = done.Task;
            _ = Drain(); return _shutdown;
            async Task Drain()
            {
                try
                {
                    var exchanges = _exchanges.Values.ToArray();
                    await Task.WhenAll(exchanges.SelectMany(exchange => new[] { Release(exchange), exchange.Sending.Task }))
                        .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
            }
        }
    }
    public async ValueTask DisposeAsync() { await Shutdown().ConfigureAwait(false); GC.SuppressFinalize(this); }

    private sealed class ResponseStream(SidecarHttpHandler owner, Exchange exchange, CancellationToken lifetime) : Stream
    {
        private readonly SemaphoreSlim _serial = new(1, 1);
        private long _position;
        private bool _eof;
        private int _closed;
        public override bool CanRead => Volatile.Read(ref _closed) == 0;
        public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous HTTP reads.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                if (buffer.Length == 0 || _eof) return 0;
                var chunk = await owner.Query<HostHttpRead, HostHttpChunk>(SidecarHttpRoutes.Read,
                    new(exchange.Id, _position, Math.Min(ChunkLimit, buffer.Length)), stop.Token).ConfigureAwait(false);
                try
                {
                    if (chunk.Offset != _position || chunk.Bytes.Length > Math.Min(ChunkLimit, buffer.Length)) throw new HttpRequestException("Invalid native HTTP sequence.");
                    chunk.Bytes.AsMemory().CopyTo(buffer); _position = checked(_position + chunk.Bytes.Length); _eof = chunk.Bytes.Length == 0;
                    return chunk.Bytes.Length;
                }
                finally { CryptographicOperations.ZeroMemory(chunk.Bytes); }
            }
            catch { Interlocked.Exchange(ref _closed, 1); await owner.Release(exchange).ConfigureAwait(false); throw; }
            finally { _serial.Release(); }
        }
        protected override void Dispose(bool disposing)
        { if (disposing && Interlocked.Exchange(ref _closed, 1) == 0) _ = owner.Release(exchange); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync()
        { Interlocked.Exchange(ref _closed, 1); await owner.Release(exchange).ConfigureAwait(false); GC.SuppressFinalize(this); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class BoundedBody : MemoryStream
    {
        private void Limit(int count) { if (count > BodyLimit - Length) throw new HttpRequestException("HTTP upload exceeds its byte budget."); }
        public override void WriteByte(byte value) { Limit(1); base.WriteByte(value); }
        public override void SetLength(long value) { if (value is < 0 or > BodyLimit) throw new HttpRequestException("HTTP upload exceeds its byte budget."); base.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { Limit(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Limit(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing)
        { if (disposing && TryGetBuffer(out var segment)) CryptographicOperations.ZeroMemory(segment.AsSpan()); base.Dispose(disposing); }
    }
}
