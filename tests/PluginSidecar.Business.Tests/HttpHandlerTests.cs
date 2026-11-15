using System.Net;
using System.Text;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr.Runtime;
using PluginSidecar.Business;

internal static class HttpHandlerTests
{
    internal static async Task Run(IXsrDispatchObserver observer)
    {
        using var native = new NativeHttp();
        List<string> diagnostics = [];
        await using var handler = await SidecarHttpHandler.CreateAsync(native.Requests, observer, diagnostics.Add);
        using var client = new HttpClient(handler);
        byte[] body = Enumerable.Range(0, 130001).Select(i => (byte)(i % 251)).ToArray();
        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://configured.invalid/test#local-fragment") { Content = new ByteArrayContent(body) })
        {
            request.Headers.Add("X-Probe", "snapshot");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            BusinessTests.Check(native.Body.SequenceEqual(body) && native.Sends == 1 && native.Chunks == 3);
            await using var stream = await response.Content.ReadAsStreamAsync();
            byte[] buffer = new byte[100000];
            BusinessTests.Check(await stream.ReadAsync(Memory<byte>.Empty) == 0 && native.Reads == 0);
            long total = 0; int read;
            while ((read = await stream.ReadAsync(buffer)) != 0)
            { BusinessTests.Check(buffer.AsSpan(0, read).ToArray().All(value => value == 71)); total += read; }
            BusinessTests.Check(total == native.Length && native.Reads > 1);
            bool syncRejected = false; try { stream.ReadByte(); } catch (NotSupportedException) { syncRejected = true; }
            BusinessTests.Check(syncRejected);
        }
        await native.Empty();
        using (var request = new HttpRequestMessage(HttpMethod.Post, "https://configured.invalid/unknown") { Content = new UnknownContent(body) })
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            BusinessTests.Check(native.Body.SequenceEqual(body));
        await native.Empty();
        native.Length = 9L * 1024 * 1024 * 1024;
        using (var response = await client.GetAsync("https://configured.invalid/large", HttpCompletionOption.ResponseHeadersRead))
        {
            BusinessTests.Check(response.Content.Headers.ContentLength == native.Length);
            var stream = await response.Content.ReadAsStreamAsync();
            BusinessTests.Check(await stream.ReadAsync(new byte[123]) == 123);
        }
        await native.Empty(); // No whole-response allocation, even with a multi-GiB declared length.
        native.FailSend = true; int sends = native.Sends;
        await Rejected(() => client.GetAsync("https://configured.invalid/lost-send", HttpCompletionOption.ResponseHeadersRead));
        BusinessTests.Check(native.Sends == sends + 1); await native.Empty();
        native.FailRead = true;
        using (var response = await client.GetAsync("https://configured.invalid/lost-read", HttpCompletionOption.ResponseHeadersRead))
        {
            var stream = await response.Content.ReadAsStreamAsync(); int reads = native.Reads;
            await Rejected(() => stream.ReadAsync(new byte[100]).AsTask());
            BusinessTests.Check(native.Reads == reads + 1); await native.Empty();
        }
        native.BlockSend = true;
        using (var stop = new CancellationTokenSource())
        {
            var pending = client.GetAsync("https://configured.invalid/cancel", HttpCompletionOption.ResponseHeadersRead, stop.Token);
            await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            await Rejected(() => pending); await native.Empty();
        }
        native.BlockSend = false;
        using (var bad = new HttpRequestMessage(HttpMethod.Post, "https://configured.invalid/bad-length") { Content = new ByteArrayContent([1, 2]) })
        {
            bad.Content.Headers.ContentLength = 3; sends = native.Sends;
            await Rejected(() => client.SendAsync(bad)); BusinessTests.Check(native.Sends == sends); await native.Empty();
        }
        using (var response = await client.GetAsync("https://configured.invalid/retire", HttpCompletionOption.ResponseHeadersRead))
        {
            var stream = await response.Content.ReadAsStreamAsync(); client.Dispose();
            await handler.DisposeAsync(); await native.Empty();
            await Rejected(() => stream.ReadAsync(new byte[1]).AsTask());
        }
        BusinessTests.Check(diagnostics.Count == 0);
        Console.WriteLine("PASS: native HTTP adapter streams upload/download, bounds IPC, preserves headers, retires on cancellation/disposal and never replays uncertain sends or reads.");
    }
    private static async Task Rejected(Func<Task> work)
    {
        try { await work(); }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException) { return; }
        throw new InvalidOperationException("Expected HTTP rejection.");
    }
    private sealed class UnknownContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    private sealed class NativeHttp : IDisposable
    {
        private readonly Dictionary<long, Upload> _uploads = [];
        private readonly object _gate = new();
        internal readonly SidecarHostRequestClient Requests;
        internal long Length = 250003;
        internal int Sends, Reads, Chunks;
        internal byte[] Body = [];
        internal bool FailSend, FailRead, BlockSend;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal NativeHttp() { Requests = new(Send); Requests.Activate(SidecarFeatures.BinaryPayloads | SidecarFeatures.HostRequests); }
        private async ValueTask Send(SidecarFrame frame, CancellationToken token)
        {
            if (frame.MessageType == SidecarMessageType.Cancel) return;
            token.ThrowIfCancellationRequested();
            var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
            if (request.ContractId == 4610 && BlockSend) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            lock (_gate)
            {
                using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input, Encoding.UTF8);
                using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
                byte[] Bytes() => reader.ReadBytes(reader.ReadInt32());
                string Text() => Encoding.UTF8.GetString(Bytes());
                void WriteText(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
                if (request.ContractId == 4607)
                {
                    writer.Write(1u); WriteText("2CC322DE0424831D687B8731AAE9777B5BBBA3292938490517B9D1E34434E070:D2CC7CB27081AB4A5C52342203E468C3982A5FF873FBC1733BA6D9ADFA7D25AC");
                    writer.Write(5); for (uint id = 4608; id <= 4612; id++) writer.Write(id);
                }
                else
                {
                    BusinessTests.Check(reader.ReadBoolean()); long id = reader.ReadInt64(); writer.Write(true);
                    switch (request.ContractId)
                    {
                        case 4608:
                            _ = Text(); string address = Text();
                            BusinessTests.Check(address.StartsWith("https://configured.invalid/", StringComparison.Ordinal) && !address.Contains('#'));
                            int headers = reader.ReadInt32();
                            for (int i = 0; i < headers; i++)
                            {
                                BusinessTests.Check(reader.ReadBoolean()); string name = Text(); BusinessTests.Check(name != "Content-Length");
                                int values = reader.ReadInt32(); for (int j = 0; j < values; j++) _ = Text();
                            }
                            int length = reader.ReadInt32(); _uploads.Add(id, new(Math.Max(0, length))); break;
                        case 4609:
                            var upload = _uploads[id]; BusinessTests.Check(reader.ReadInt32() == upload.Offset);
                            byte[] chunk = Bytes(); BusinessTests.Check(chunk.Length is > 0 and <= 61440);
                            chunk.CopyTo(upload.Bytes, upload.Offset); upload.Offset += chunk.Length; Chunks++; break;
                        case 4610:
                            var sent = _uploads[id]; BusinessTests.Check(sent.Offset == sent.Bytes.Length); Body = sent.Bytes.ToArray(); Sends++;
                            if (FailSend) { FailSend = false; throw new IOException("Uncertain send."); }
                            writer.Write(true); writer.Write(200); writer.Write(1); writer.Write(true); WriteText("Content-Length"); writer.Write(1); WriteText(Length.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                        case 4611:
                            var response = _uploads[id]; long offset = reader.ReadInt64(); int count = reader.ReadInt32();
                            BusinessTests.Check(offset == response.Read && count is > 0 and <= 61440);
                            int size = (int)Math.Min(count, Length - offset); response.Read += size; Reads++;
                            if (FailRead) { FailRead = false; throw new IOException("Uncertain read."); }
                            writer.Write(true); writer.Write(offset); writer.Write(size); writer.Write(Enumerable.Repeat((byte)71, size).ToArray()); break;
                        case 4612: _uploads.Remove(id); break;
                        default: throw new InvalidOperationException("Unexpected HTTP operation.");
                    }
                }
                BusinessTests.Check(input.Position == input.Length);
                BusinessTests.Check(Requests.TryAcceptReply(new(SidecarProtocol.Version,
                    frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
                    SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true, new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null))));
            }
        }
        internal async Task Empty()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true) { lock (_gate) if (_uploads.Count == 0) return; await Task.Delay(10, timeout.Token); }
        }
        public void Dispose() => Requests.Dispose();
        private sealed class Upload(int length) { internal readonly byte[] Bytes = new byte[length]; internal int Offset; internal long Read; }
    }
}
