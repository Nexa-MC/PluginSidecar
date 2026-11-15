using System.Security.Cryptography;
using Nexa.Plugin.MarketDelivery;
using Nexa.Sidecar.Protocol;
using PluginSidecar;
using PluginSidecar.Protection;

internal static partial class StandaloneRuntimeTests
{
    private static async Task MarketComposition(string parent, string package)
    {
        string root = Path.Combine(parent, "normal-market"); Directory.CreateDirectory(root);
        using var signer = RSA.Create(2048); using var secrets = new KeyProtectionTests.FakeSecrets();
        string pin = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
        using (var host = new NativeHost(root, package) { Developer = false, Remember = true })
        await using (var runtime = Runtime())
        {
            await runtime.ActivateAsync(host.Requests, default);
            Guid caller = Guid.NewGuid(), operation = Guid.NewGuid();
            async Task<MarketTransferReply> Send(MarketTransferRequest request) => PluginMarketWire.DecodeReply(await runtime.QueryAsync(1,
                MarketServiceTests.Envelope(PluginMarketWire.Encode(request), caller), default));
            var begin = await Send(new(MarketTransferVerb.Begin, operation, "example.desktop", "1.0.0", "account"));
            using var device = RSA.Create(); device.ImportSubjectPublicKeyInfo(begin.DevicePublicKey, out _);
            var delivery = MarketDeliveryIssuer.Issue(await File.ReadAllBytesAsync(package),
                new("example.desktop", "1.0.0", "account", DateTimeOffset.UtcNow.AddMinutes(-1), null), device, signer);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(1); writer.Write(delivery.Grant.Length); writer.Write(delivery.Signature.Length); writer.Write(delivery.Ciphertext.Length);
            writer.Write(delivery.Grant); writer.Write(delivery.Signature); writer.Write(delivery.Ciphertext); writer.Flush(); var packet = stream.ToArray();
            for (int offset = 0; offset < packet.Length; offset += PluginMarketWire.ChunkLimit)
                await Send(new(MarketTransferVerb.Append, operation, Offset: offset,
                    Chunk: packet.AsSpan(offset, Math.Min(PluginMarketWire.ChunkLimit, packet.Length - offset)).ToArray()));
            Check((await Send(new(MarketTransferVerb.Finish, operation))).Phase == MarketTransferPhase.Installing);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            MarketTransferReply result;
            do { await Task.Delay(20, timeout.Token); result = await Send(new(MarketTransferVerb.Status, operation)); }
            while (result.Phase == MarketTransferPhase.Installing);
            Check(result.Phase == MarketTransferPhase.Installed && host.Picks == 0 && host.Confirmations == 2);
            await Until(() => host.Button("停用") is not null, runtime);
        }
        // The production composition reconstructs its device key and catalog without a Market peer or bearer.
        using (var offline = new NativeHost(root, package) { Developer = false })
        await using (var runtime = Runtime())
        {
            await runtime.ActivateAsync(offline.Requests, default);
            await Until(() => offline.Button("停用") is not null, runtime);
            Check(offline.Picks == 0 && offline.Confirmations == 0 && offline.Button("加载开发包") is null);
            await runtime.ExecuteAsync(2, Form(offline.Button("停用")!), default);
            await Until(() => offline.Button("停用") is null, runtime);
        }
        Console.WriteLine("PASS: production composition installs a signed Market plugin in normal mode and reconstructs device/catalog for offline restart without Market.");
        StandalonePluginRuntime Runtime() => new((_, _) => ValueTask.FromResult(new HttpClient(new TrackingHttp())), _ => { },
            () => new PluginMarketTrust(pin), () => new PlatformPluginKeyProtector(secrets));
    }
}
