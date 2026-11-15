using System.Text;
using Nexa.Plugin.MarketDelivery;
using Nexa.Sidecar.Protocol;
using PluginSidecar;

internal static class MarketServiceTests
{
    internal static async Task Run()
    {
        Guid caller = Guid.NewGuid(), operation = Guid.NewGuid(), second = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var entered = Signal(); var cancelled = Signal(); var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int installs = 0;
        byte[] publicKey = [1, 2, 3];
        await using var service = new PluginMarketService(publicKey, async (delivery, id, version, subject, token) =>
        {
            Interlocked.Increment(ref installs);
            Check(id == "example.test" && version == "1.0.0" && subject == "account");
            Check(delivery.Grant.AsSpan().SequenceEqual(new byte[] { 1 }) && delivery.Signature[0] == 2 && delivery.Ciphertext[0] == 3);
            using var cancel = token.Register(() => cancelled.TrySetResult()); entered.TrySetResult();
            // Deliberately model an effect that committed before observing cancellation.
            return await release.Task;
        }, () => now);
        publicKey[0] = 99;
        Task<MarketTransferReply> Call(MarketTransferRequest request, Guid? owner = null, string name = PluginMarketWire.Caller)
            => Send(service, request, owner ?? caller, name);
        MarketTransferRequest Begin(Guid id) => new(MarketTransferVerb.Begin, id, "example.test", "1.0.0", "account");
        await Reject(() => Call(Begin(operation), name: "Other.Native.Sidecar"));
        var begun = await Call(Begin(operation));
        Check(begun.Phase == MarketTransferPhase.Receiving && begun.DevicePublicKey.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }));
        await Reject(() => Call(Begin(operation)));
        await Call(Begin(second));
        await Reject(() => Call(Begin(Guid.NewGuid())));
        await Reject(() => Call(new(MarketTransferVerb.Status, operation), Guid.NewGuid()));
        await Reject(() => Call(new(MarketTransferVerb.Append, operation, Offset: 1, Chunk: [1])));
        await Reject(() => Call(new(MarketTransferVerb.Append, operation, Chunk: new byte[PluginMarketWire.ChunkLimit + 1])));
        Check((await Call(new(MarketTransferVerb.Status, operation))).Offset == 0);
        Check((await Call(new(MarketTransferVerb.Finish, second))).Phase == MarketTransferPhase.Failed && installs == 0);
        byte[] packet = DeliveryPacket();
        await Call(new(MarketTransferVerb.Append, operation, Chunk: packet[..7]));
        await Call(new(MarketTransferVerb.Append, operation, Offset: 7, Chunk: packet[7..]));
        Check((await Call(new(MarketTransferVerb.Finish, operation))).Phase == MarketTransferPhase.Installing);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(installs == 1 && (await Call(new(MarketTransferVerb.Status, operation))).Offset == packet.Length);
        await Reject(() => Call(new(MarketTransferVerb.Finish, operation)));
        Guid queued = Guid.NewGuid(); await Call(Begin(queued));
        await Call(new(MarketTransferVerb.Append, queued, Chunk: packet));
        await Reject(() => Call(new(MarketTransferVerb.Finish, queued)));
        var abort = Call(new(MarketTransferVerb.Abort, operation));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!abort.IsCompleted);
        await Reject(() => Call(Begin(Guid.NewGuid()))); // Keep the reservation until the admitted installer actually drains.
        release.TrySetResult(true);
        Check((await abort).Phase == MarketTransferPhase.Installed); // Abort must not claim an already committed effect was undone.
        Check(installs == 1);
        await Reject(() => Call(new(MarketTransferVerb.Status, operation)));
        Check((await Call(new(MarketTransferVerb.Abort, queued))).Phase == MarketTransferPhase.Aborted);
        Guid idle1 = Guid.NewGuid(), idle2 = Guid.NewGuid(), reclaimed = Guid.NewGuid();
        await Call(Begin(idle1)); await Call(Begin(idle2));
        now += TimeSpan.FromMinutes(3);
        await Reject(() => Call(new(MarketTransferVerb.Status, idle1)));
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { await Call(Begin(reclaimed)); break; }
            catch (InvalidOperationException) when (DateTime.UtcNow < deadline) { await Task.Delay(25); }
        }
        await Call(new(MarketTransferVerb.Abort, reclaimed));
        Check(installs == 1);
        await Reject(() => service.InvokeAsync(Envelope([.. PluginMarketWire.Encode(Begin(Guid.NewGuid())), 9], caller), default).AsTask());
        await Reject(() => service.InvokeAsync(Envelope(PluginMarketWire.Encode(Begin(Guid.Empty)), caller), default).AsTask());
        await Reject(() => { PluginMarketWire.DecodeDelivery([1, 0, 0, 0, 255, 255, 255, 127, 1, 0, 0, 0, 1, 0, 0, 0]); return Task.CompletedTask; });
        await service.DisposeAsync();
        await Reject(() => Call(Begin(Guid.NewGuid())));
        Console.WriteLine("PASS: Market native service authenticates callers, scopes transfers, bounds sequential uploads, returns before consent, polls without replay, drains cancellation and reclaims idle quotas.");
    }

    private static async Task<MarketTransferReply> Send(PluginMarketService service, MarketTransferRequest request, Guid caller, string name)
        => PluginMarketWire.DecodeReply(await service.InvokeAsync(Envelope(PluginMarketWire.Encode(request), caller, name), default));
    internal static SidecarBinaryValue Envelope(byte[] payload, Guid caller, string name = PluginMarketWire.Caller)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(true); Text(name); Text(caller.ToString("N")); writer.Write(payload.Length); writer.Write(payload); writer.Flush();
        return new(SidecarWireCodecs.GeneratedDto, stream.ToArray());
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    }
    private static byte[] DeliveryPacket()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(1); writer.Write(1); writer.Write(2); writer.Write(new byte[] { 1, 2, 3, 4 });
        writer.Flush(); return stream.ToArray();
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Market service assertion failed."); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Invalid Market transfer accepted.");
    }
}
