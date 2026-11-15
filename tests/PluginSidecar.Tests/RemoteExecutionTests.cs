using System.Diagnostics;
using System.IO.Compression;
using Nexa.Plugin.Bootstrap.V1;
using System.Security.Cryptography;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Packages;
using PluginSidecar.WasmRuntime;

internal static class RemoteExecutionTests
{
    internal static async Task Run()
    {
        using (var endpoint = new ChunkedPluginEndpoint(new Echo()))
        {
            var exchange = new ChunkedPluginExchange(endpoint); var value = RandomNumberGenerator.GetBytes(200000);
            Check((await exchange.ExchangeAsync(value, default)).SequenceEqual(value));
        }
        await ChunkRejections();
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Real Wasm fixtures are required.");
        await FaultSealsGuest(sample);
        Console.WriteLine("PASS: legacy binary codec limits, chunk rejection and sealed guest effects after uncertain transport failure.");
    }
    private static async Task FaultSealsGuest(string sample)
    {
        using var package = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(sample), "example.wasm", "1.0.0");
        byte[] bytes = package.CopyPayload(package.Manifest.WebAssemblyEntryPath!);
        await using var server = new GuestExecutionServer(); var flaky = new Flaky(server);
        await using var machine = await RemoteWasmPluginMachine.CreateAsync(flaky, bytes);
        CryptographicOperations.ZeroMemory(bytes); flaky.Fail = true;
        try { await Run(); throw new Exception("Expected disconnected exchange."); } catch (IOException) { }
        int calls = flaky.Calls;
        try { await Run(); throw new Exception("Expected sealed guest."); } catch (InvalidOperationException) { }
        Check(flaky.Calls == calls); flaky.Fail = false;
        ValueTask<AbiValue> Run() => machine.RunDetailedAsync(DataCodec.Map(("phase", new AbiValue.Utf8String("Initialize"))),
            (_, _) => throw new Exception("No Host effects may run after transport loss."), TimeSpan.FromSeconds(5));
    }
    private sealed class Flaky(IPluginBinaryExchange inner) : IPluginBinaryExchange
    {
        internal bool Fail; internal int Calls;
        public ValueTask<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken token)
        { Calls++; if (Fail) throw new IOException("Disconnected"); return inner.ExchangeAsync(request, token); }
    }
    private static byte[] Packet(byte op, Guid id, Action<BinaryWriter>? body = null)
    { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); writer.Write(op); writer.Write(id.ToByteArray()); body?.Invoke(writer); return stream.ToArray(); }
    private static async Task ChunkRejections()
    {
        using var endpoint = new ChunkedPluginEndpoint(new Echo()); Guid id = Guid.NewGuid(); byte[] bytes = [1, 2, 3];
        var begin = Packet(1, id, w => { w.Write(3); w.Write(SHA256.HashData(bytes)); }); await endpoint.ExchangeAsync(begin, default);
        await Reject(begin);
        await Reject(Packet(2, id, w => { w.Write(1); w.Write(1); w.Write((byte)1); }));
        await Reject(Packet(3, id));
        await endpoint.ExchangeAsync(Packet(2, id, w => { w.Write(0); w.Write(3); w.Write(bytes); }), default);
        await endpoint.ExchangeAsync(Packet(3, id), default);
        await Reject(Packet(3, id));
        await endpoint.ExchangeAsync(Packet(5, id), default); await Reject(Packet(4, id, w => { w.Write(0); w.Write(3); }));
        await Reject(Packet(1, Guid.NewGuid(), w => { w.Write(ChunkedPluginExchange.MaximumBytes + 1); w.Write(new byte[32]); }));
        Guid corrupt = Guid.NewGuid(); await endpoint.ExchangeAsync(Packet(1, corrupt, w => { w.Write(3); w.Write(new byte[32]); }), default);
        await endpoint.ExchangeAsync(Packet(2, corrupt, w => { w.Write(0); w.Write(3); w.Write(bytes); }), default); await Reject(Packet(3, corrupt));
        async Task Reject(byte[] packet)
        { try { await endpoint.ExchangeAsync(packet, default); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid transfer accepted."); }
    }
    private static void Check(bool value, string message = "Remote execution assertion failed.") { if (!value) throw new InvalidOperationException(message); }
    private sealed class Echo : IPluginBinaryExchange
    { public ValueTask<byte[]> ExchangeAsync(ReadOnlyMemory<byte> input, CancellationToken cancellation) => ValueTask.FromResult(input.ToArray()); }
}
