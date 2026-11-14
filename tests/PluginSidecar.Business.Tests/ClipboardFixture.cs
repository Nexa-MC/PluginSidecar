using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Business;

internal sealed class ClipboardFixture : IDisposable
{
    private readonly SidecarHostRequestClient _requests;
    internal SidecarClipboardClient Client { get; }
    internal string? Text { get; set; }
    internal byte[]? Image { get; set; }
    internal int Reads, Writes, Releases, Begins, Appends;
    internal bool BadChunk, BadHash, BadUtf8, RejectRelease;
    internal uint Hold;
    internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal long LastReleased;
    private long _lastId, _live;
    private byte[]? _staged;
    private string? _text;
    private int _received;
    internal ClipboardFixture(IXsrDispatchObserver observer)
    { _requests = new(Reply); _requests.Activate(SidecarFeatures.All); Client = new(_requests, observer); }
    private async ValueTask Reply(SidecarFrame frame, CancellationToken token)
    {
        if (frame.MessageType == SidecarMessageType.Cancel) return;
        var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        void Bytes(byte[]? bytes) { writer.Write(bytes?.Length ?? -1); if (bytes is not null) writer.Write(bytes); }
        if (request.ContractId == 3071)
        {
            BusinessTests.Check(request.Value.Length == 0); writer.Write(1u);
            Bytes(Encoding.UTF8.GetBytes("BBC8EE6AC026ACC01B11DB1019BB4503F19E353381E1A79B1578D013526BA906:37ECB6E60042319F81AB00E5CB44CADEE2FC9AA15755F47096E03D1DBED64620"));
            writer.Write(6); for (uint id = 3072; id <= 3077; id++) writer.Write(id);
        }
        else
        {
            using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input);
            byte[]? ReadBytes() { int length = reader.ReadInt32(); return length == -1 ? null : reader.ReadBytes(length); }
            BusinessTests.Check(reader.ReadBoolean()); long id = reader.ReadInt64(); BusinessTests.Check(id > 0);
            if (request.ContractId == 3077)
            {
                Releases++; LastReleased = id; _lastId = Math.Max(_lastId, id);
                if (_live == id) { _live = 0; _staged = null; }
                writer.Write(!RejectRelease);
                if (RejectRelease) { writer.Write((int)XsrErrorKind.Unavailable); Bytes(Encoding.UTF8.GetBytes("xsr.unavailable")); }
            }
            else
            {
                writer.Write(true);
                switch (request.ContractId)
                {
                    case 3072:
                        BusinessTests.Check(id > _lastId); _lastId = _live = id; Reads++;
                        writer.Write(true); Bytes(BadUtf8 ? [0xff] : Text is null ? null : Encoding.UTF8.GetBytes(Text));
                        writer.Write(Image?.Length ?? -1); Bytes(Image is null ? null : BadHash ? new byte[32] : SHA256.HashData(Image));
                        break;
                    case 3073:
                        BusinessTests.Check(_live == id && Image is not null);
                        int offset = reader.ReadInt32(), length = reader.ReadInt32();
                        BusinessTests.Check(length is > 0 and <= 256 * 1024);
                        writer.Write(true); writer.Write(BadChunk ? offset + 1 : offset); Bytes(Image!.AsSpan(offset, length).ToArray());
                        break;
                    case 3074:
                        BusinessTests.Check(id > _lastId); _lastId = _live = id; Begins++;
                        byte[]? text = ReadBytes(); _text = text is null ? null : Encoding.UTF8.GetString(text);
                        int size = reader.ReadInt32(); _staged = size < 0 ? null : new byte[size]; _received = 0;
                        var hash = ReadBytes(); BusinessTests.Check(size < 0 ? hash is null : hash?.Length == 32);
                        break;
                    case 3075:
                        BusinessTests.Check(id == _live); int start = reader.ReadInt32(); var bytes = ReadBytes()!;
                        BusinessTests.Check(start == _received && bytes.Length is > 0 and <= 256 * 1024);
                        bytes.CopyTo(_staged!, _received); _received += bytes.Length; Appends++; break;
                    case 3076:
                        BusinessTests.Check(id == _live && _received == (_staged?.Length ?? 0));
                        Writes++; Text = _text; Image = _staged; break;
                    default: throw new InvalidDataException("Unexpected clipboard contract.");
                }
            }
            BusinessTests.Check(input.Position == input.Length);
            if (request.ContractId == Hold) { Entered.TrySetResult(); await Resume.Task.WaitAsync(token); }
        }
        // A cancelled caller may already have discarded its request. Late replies are harmless.
        _requests.TryAcceptReply(new(SidecarProtocol.Version, request.ContractId <= 3073 ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
            SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true,
                new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null)));
    }
    internal static byte[] Png(int length = 33)
    {
        byte[] bytes = new byte[length]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13); "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 1); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 1); return bytes;
    }
    internal static async Task AdapterChecks(IXsrDispatchObserver observer)
    {
        using var fixture = new ClipboardFixture(observer); int cleanupErrors = 0;
        var adapter = new SidecarClipboard(fixture.Client, _ => cleanupErrors++);
        static async Task Fails(Func<Task> call)
        {
            try { await call(); throw new InvalidOperationException("Invalid clipboard operation accepted."); }
            catch (Nexa.Plugin.Hosting.AbiHostException) { }
        }
        await Fails(async () => await adapter.ReadAsync(default)); BusinessTests.Check(fixture.Reads == 0);
        BusinessTests.Check((await fixture.Client.DiscoverAsync()).IsSuccess);
        fixture.Text = new('中', 65536); fixture.Image = Png(16 * 1024 * 1024);
        var read = await adapter.ReadAsync(default);
        BusinessTests.Check(read.Text == fixture.Text && read.Image!.SequenceEqual(fixture.Image));
        await adapter.WriteAsync(read.Text, read.Image, default);
        BusinessTests.Check(fixture.Writes == 1 && fixture.Appends == 64 && fixture.Text == read.Text && fixture.Image!.SequenceEqual(read.Image!));
        foreach (string? text in new string?[] { null, "" })
        {
            await adapter.WriteAsync(text, null, default); var value = await adapter.ReadAsync(default);
            BusinessTests.Check(value.Text == text && value.Image is null);
        }
        await Fails(() => adapter.WriteAsync(new string('x', 65537), null, default));
        await Fails(() => adapter.WriteAsync("\ud800", null, default));
        await Fails(() => adapter.WriteAsync(null, new byte[33], default));
        var excessive = Png(); BinaryPrimitives.WriteUInt32BigEndian(excessive.AsSpan(20), 4097);
        await Fails(() => adapter.WriteAsync(null, excessive, default));
        fixture.Image = Png(); fixture.BadChunk = true; await Fails(async () => await adapter.ReadAsync(default)); fixture.BadChunk = false;
        fixture.BadHash = true; await Fails(async () => await adapter.ReadAsync(default)); fixture.BadHash = false;
        fixture.BadUtf8 = true; await Fails(async () => await adapter.ReadAsync(default)); fixture.BadUtf8 = false;
        // A failed cleanup is reported without hiding a completed write.
        fixture.RejectRelease = true; await adapter.WriteAsync(null, null, default); BusinessTests.Check(cleanupErrors >= 1); fixture.RejectRelease = false;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); int before = fixture.Begins;
            try { await adapter.WriteAsync(null, null, cancelled.Token); throw new InvalidOperationException("Pre-cancellation ignored."); }
            catch (OperationCanceledException) { }
            BusinessTests.Check(fixture.Begins == before);
        }
        foreach (uint hold in new uint[] { 3072, 3074, 3076 })
        {
            using var stalled = new ClipboardFixture(observer) { Hold = hold };
            BusinessTests.Check((await stalled.Client.DiscoverAsync()).IsSuccess);
            var pendingAdapter = new SidecarClipboard(stalled.Client, _ => { });
            using var stop = new CancellationTokenSource();
            Task pending = hold == 3072 ? pendingAdapter.ReadAsync(stop.Token) : pendingAdapter.WriteAsync(null, null, stop.Token);
            await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            BusinessTests.Check(stalled.Releases == 1 && stalled.LastReleased > 0 && stalled.Writes == (hold == 3076 ? 1 : 0));
            stalled.Resume.TrySetResult();
        }
        Console.WriteLine("PASS: clipboard binary adapter preserves full text/PNG, verifies chunks and digests, releases known IDs after lost replies and never replays writes.");
    }
    public void Dispose() { Resume.TrySetResult(); _requests.Dispose(); }
}
