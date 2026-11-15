using System.Security.Cryptography;
using System.Text;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using PluginSidecar.Presentation;

internal static partial class PresentationTests
{
    private static async Task RasterHostChecks()
    {
        var uploads = new Dictionary<long, RasterUpload>();
        HashSet<long> bound = [];
        int begins = 0, chunks = 0, seals = 0, sets = 0, releases = 0;
        bool loseBegin = false, loseSet = false;
        CancellationTokenSource? cancelSet = null;
        long[] lastBound = [];
        SidecarHostRequestClient? wire = null;
        using var requests = wire = new((frame, token) =>
        {
            if (frame.MessageType == SidecarMessageType.Cancel) return ValueTask.CompletedTask;
            token.ThrowIfCancellationRequested();
            var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
            using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input, Encoding.UTF8);
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            byte[] Bytes() => reader.ReadBytes(reader.ReadInt32());
            string ReadText() => Encoding.UTF8.GetString(Bytes());
            void Text(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
            bool rejected = false;
            if (request.ContractId == 4351)
            {
                writer.Write(1u);
                Text("FCC99AB1237821D1B77D76F080B21569798862A2D27672BA6D5277818FAB4AFE:54689383CAB61E34249D2FBAB4C801FA0E96BE2499769FAA92C1F9C50027DAEB");
                writer.Write(5); for (uint id = 4352; id <= 4356; id++) writer.Write(id);
            }
            else
            {
                Check(reader.ReadBoolean());
                if (request.ContractId == 4355)
                {
                    Check(reader.ReadInt64() == 17); int count = reader.ReadInt32(); Check(count is >= 0 and <= 8);
                    List<long> next = [];
                    for (int i = 0; i < count; i++)
                    {
                        Check(reader.ReadBoolean()); Check(reader.ReadInt32() == i); Check(ReadText() == "ScopedCanvas" + i);
                        long id = reader.ReadInt64(); Check(uploads.TryGetValue(id, out var upload) && upload.Sealed); next.Add(id);
                        Check(reader.ReadDouble() == 64 && reader.ReadDouble() == 64);
                        Check(ReadText() == PluginViewCompiler.DrawingInputCommand); Check(ReadText() == "opaque" + i);
                    }
                    sets++; lastBound = next.ToArray(); bound = next.ToHashSet();
                    if (loseSet) { loseSet = false; throw new IOException("Raster replacement committed without acknowledgement."); }
                    if (cancelSet is { } cancellation) { cancelSet = null; cancellation.Cancel(); return ValueTask.CompletedTask; }
                }
                else
                {
                    long id = reader.ReadInt64(); Check(id > 0);
                    switch (request.ContractId)
                    {
                        case 4352:
                            int length = reader.ReadInt32(); byte[] digest = Bytes(); Check(digest.Length == 32);
                            uploads.Add(id, new(length, digest)); begins++;
                            if (loseBegin) { loseBegin = false; throw new IOException("Raster upload began without acknowledgement."); }
                            break;
                        case 4353:
                            var upload = uploads[id]; Check(reader.ReadInt32() == upload.Offset); byte[] data = Bytes();
                            Check(data.Length is > 0 and <= 65536); data.CopyTo(upload.Bytes, upload.Offset); upload.Offset += data.Length; chunks++;
                            break;
                        case 4354:
                            var complete = uploads[id]; Check(complete.Offset == complete.Bytes.Length);
                            Check(SHA256.HashData(complete.Bytes).SequenceEqual(complete.Digest)); complete.Sealed = true; seals++;
                            break;
                        case 4356:
                            releases++; rejected = bound.Contains(id); if (!rejected) uploads.Remove(id);
                            break;
                        default: throw new Exception("Unexpected raster operation.");
                    }
                }
                writer.Write(!rejected);
                if (rejected) { writer.Write((int)XsrErrorKind.Rejected); Text("xsr.rejected"); }
            }
            Check(input.Position == input.Length);
            Check(wire!.TryAcceptReply(new(SidecarProtocol.Version,
                frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
                SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true, new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null))));
            return ValueTask.CompletedTask;
        });
        requests.Activate(SidecarFeatures.All);
        await using var host = await SidecarRasterHost.CreateAsync(requests, new Observer());
        // This fixture tests transport bytes; Host decoding and real PNG output have separate tests.
        byte[] pixels = Enumerable.Range(0, 150001).Select(i => (byte)(i % 251)).ToArray();
        var raster = new PluginRasterImage(64, 64, pixels);
        PluginRasterBinding[] bindings = [new(0, "ScopedCanvas0", 64, 64, raster, "opaque0"), new(1, "ScopedCanvas1", 64, 64, raster, "opaque1")];
        await host.ReplaceAsync(17, bindings, default);
        Check(begins == 1 && chunks == 3 && seals == 1 && sets == 1 && lastBound[0] == lastBound[1]);
        Check(uploads.Single().Value.Bytes.SequenceEqual(pixels) && pixels.Any(value => value != 0));
        await host.ReplaceAsync(17, bindings, default); Check(begins == 1 && sets == 2);
        foreach (var invalid in new PluginRasterBinding[][]
        {
            [bindings[0] with { Document = 32 }], [bindings[0] with { Slot = "bad\nslot" }],
            [bindings[0] with { Width = double.NaN }], [bindings[0] with { Width = 63 }],
            [bindings[0] with { InputToken = new string('x', 32769) }], [bindings[0], bindings[0]]
        }) await Fails(() => host.ReplaceAsync(17, invalid, default).AsTask());
        Check(begins == 1 && sets == 2);
        await host.ReplaceAsync(17, [], default); Check(uploads.Count == 0 && releases == 1);
        loseBegin = true;
        await Fails(() => host.ReplaceAsync(17, bindings, default).AsTask());
        Check(begins == 2 && uploads.Count == 0 && sets == 3); // No replay of an uncertain begin.
        loseSet = true;
        await Fails(() => host.ReplaceAsync(17, bindings, default).AsTask());
        Check(begins == 3 && sets == 4 && uploads.Count == 1);
        long uncertain = lastBound[0];
        await host.ReplaceAsync(17, bindings, default);
        Check(begins == 4 && sets == 5 && lastBound[0] != uncertain && uploads.Count == 1 && !uploads.ContainsKey(uncertain));
        using var cancelled = new CancellationTokenSource(); cancelSet = cancelled;
        bool stopped = false;
        try { await host.ReplaceAsync(17, [], cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped && uploads.Count == 0); // Cleanup uses an independent token after caller cancellation.
        Console.WriteLine("PASS: native raster binary contract, chunk bounds, digest/cache reuse, local rejection, no uncertain replay and independent cleanup.");

        static async Task Fails(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException) { return; }
            throw new Exception("Invalid raster publication was accepted.");
        }
    }
    private sealed class RasterUpload(int length, byte[] digest)
    {
        internal byte[] Bytes = new byte[length];
        internal byte[] Digest = digest;
        internal int Offset;
        internal bool Sealed;
    }
}
