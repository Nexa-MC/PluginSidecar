using System.Security.Cryptography;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.MarketDelivery;
using Nexa.Plugin.Hosting;
using PluginSidecar;
using PluginSidecar.Application;
using PluginSidecar.Presentation;
using PluginSidecar.Protection;
using PluginSidecar.WasmRuntime;

internal static class MarketServiceInstallationTests
{
    internal static async Task Run()
    {
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Real guest fixture required.");
        byte[] package = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "desktop.zip"));
        string root = Path.Combine(Path.GetTempPath(), "nexa-market-service-" + Guid.NewGuid().ToString("N"));
        using var signer = RSA.Create(2048); using var device = RSA.Create(3072);
        using var trust = new PluginMarketTrust(Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));
        using var secrets = new KeyProtectionTests.FakeSecrets(); using var keys = new PlatformPluginKeyProtector(secrets);
        using var developer = new PluginOfflineCatalog(Path.Combine(root, "developer"), keys);
        using var market = PluginMarketCatalog.CreateWithVerifier(Path.Combine(root, "market"), keys,
            (delivery, id, version, subject) => trust.Open(device, delivery, id, version, subject));
        var policy = new PluginInstallationPolicy(Guid.NewGuid(), false, true, false);
        int prompts = 0;
        try
        {
            await using (var app = Application())
            await using (var manager = Manager(app))
            await using (var service = new PluginMarketService(device.ExportSubjectPublicKeyInfo(), manager.InstallMarketAsync))
            {
                Guid caller = Guid.NewGuid(), operation = Guid.NewGuid();
                async Task<MarketTransferReply> Send(MarketTransferRequest request) => PluginMarketWire.DecodeReply(await service.InvokeAsync(
                    MarketServiceTests.Envelope(PluginMarketWire.Encode(request), caller), default));
                var begun = await Send(new(MarketTransferVerb.Begin, operation, "example.desktop", "1.0.0", "account"));
                using var recipient = RSA.Create(); recipient.ImportSubjectPublicKeyInfo(begun.DevicePublicKey, out _);
                var delivery = MarketDeliveryIssuer.Issue(package,
                    new("example.desktop", "1.0.0", "account", DateTimeOffset.UtcNow.AddMinutes(-1), null), recipient, signer);
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                writer.Write(1); writer.Write(delivery.Grant.Length); writer.Write(delivery.Signature.Length); writer.Write(delivery.Ciphertext.Length);
                writer.Write(delivery.Grant); writer.Write(delivery.Signature); writer.Write(delivery.Ciphertext); writer.Flush(); byte[] packet = stream.ToArray();
                for (int offset = 0; offset < packet.Length; offset += PluginMarketWire.ChunkLimit)
                    await Send(new(MarketTransferVerb.Append, operation, Offset: offset, Chunk: packet.AsSpan(offset, Math.Min(PluginMarketWire.ChunkLimit, packet.Length - offset)).ToArray()));
                var reply = await Send(new(MarketTransferVerb.Finish, operation));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (reply.Phase == MarketTransferPhase.Installing)
                { await Task.Delay(20, timeout.Token); reply = await Send(new(MarketTransferVerb.Status, operation)); }
                Check(reply.Phase == MarketTransferPhase.Installed && prompts == 1 && app.Runtime.Active.Single().PluginId == "example.desktop");
                Check(market.Records().Length == 1 && developer.Records().IsEmpty);
                await Send(new(MarketTransferVerb.Abort, operation));
                Check(app.Runtime.Active.Length == 1); // Disposing the transfer is not uninstalling an approved plugin.
            }
            policy = policy with { Session = Guid.NewGuid() };
            // No Market service, account bearer, HTTP client, or developer mode exists in this restarted session.
            await using (var offline = Application())
            await using (var manager = Manager(offline))
            {
                Check((await manager.RestoreOfflineAsync()).IsEmpty);
                Check(offline.Runtime.Active.Single().PluginId == "example.desktop" && prompts == 1);
                await manager.DisableAsync(offline.Runtime.Active.Single(), default);
                Check(market.Records().IsEmpty && offline.Runtime.Active.IsEmpty);
            }
            Console.WriteLine("PASS: native encrypted transfer verifies publisher/device, requests normal-mode consent, activates real Wasm and restores offline without Market or another prompt.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        PluginApplication Application() => new(Path.Combine(root, "storage"),
            (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)), [], new Allow(), (_, _) => { }, (_, _) => { },
            PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
        PluginManagementController Manager(PluginApplication app) => new(app, developer, () => policy,
            _ => throw new InvalidOperationException("Developer picker must not run."), (consent, _) =>
            { Check(consent.Source == PluginInstallSource.Market); prompts++; return ValueTask.FromResult(PluginInstallDecision.Remember); }, market);
    }
    private sealed class Allow : IActionConfirmation
    { public ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request, CancellationToken token) => ValueTask.FromResult(true); }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Market end-to-end assertion failed."); }
}
