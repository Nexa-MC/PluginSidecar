using System.Security.Cryptography;
using Nexa.Plugin.MarketDelivery;

internal static class MarketOfflineTests
{
    internal static async Task Run()
    {
        using var publisher = RSA.Create(2048); using var device = RSA.Create(2048); using var other = RSA.Create(2048);
        var clock = new Clock(); var verifier = new OfflinePluginVerifier(publisher, device, clock);
        byte[] payload = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "HelloPlugin.dll"));
        var identity = new PluginEntitlement("example.plugin", "1.0.0", "owner", clock.Now.AddMinutes(-1), null);
        var delivery = MarketDeliveryIssuer.Issue(payload, identity, device, publisher);
        string root = Path.Combine(Path.GetTempPath(), "nexa-offline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OfflinePluginStore(root, verifier); var market = new Market(delivery);
            await store.AcquireAsync(market, identity.PluginId, identity.Version, identity.Subject, device.ExportSubjectPublicKeyInfo());
            market.Offline = true; clock.Now = clock.Now.AddYears(3);
            byte[] offlineAssembly = store.OpenOffline(identity.PluginId, identity.Version, identity.Subject);
            try
            {
                using var plugin = PluginSidecar.RuntimeAdapter.MemoryPlugin.LoadTrustedDevelopmentPayloads("HelloPlugin", "HelloPlugin.Plugin", new Dictionary<string, byte[]> { ["HelloPlugin"] = offlineAssembly });
                if (plugin.Entry.GetType().FullName != "HelloPlugin.Plugin") throw new Exception("Offline plugin entry unavailable.");
            }
            finally { CryptographicOperations.ZeroMemory(offlineAssembly); }

            Equal(payload, store.OpenOffline(identity.PluginId, identity.Version, identity.Subject));
            Equal(payload, new OfflinePluginStore(root, new(publisher, device, clock)).OpenOffline(identity.PluginId, identity.Version, identity.Subject));
            if (market.Calls != 1) throw new Exception("Offline load contacted Market.");
            Reject(() => verifier.Open(delivery, identity.PluginId, identity.Version, "other"));
            Reject(() => new OfflinePluginVerifier(publisher, other, clock).Open(delivery, identity.PluginId, identity.Version, identity.Subject));
            Reject(() => new OfflinePluginVerifier(other, device, clock).Open(delivery, identity.PluginId, identity.Version, identity.Subject));
            var tampered = delivery with { Ciphertext = delivery.Ciphertext.ToArray() }; tampered.Ciphertext[0] ^= 1;
            Reject(() => verifier.Open(tampered, identity.PluginId, identity.Version, identity.Subject));
            var grant = delivery with { Grant = delivery.Grant.ToArray() }; grant.Grant[8] ^= 1;
            Reject(() => verifier.Open(grant, identity.PluginId, identity.Version, identity.Subject));
            try { await store.SaveAsync(tampered, identity.PluginId, identity.Version, identity.Subject); throw new Exception("Corrupt update accepted."); } catch (CryptographicException) { }
            Equal(payload, store.OpenOffline(identity.PluginId, identity.Version, identity.Subject));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await store.SaveAsync(delivery, identity.PluginId, identity.Version, identity.Subject, canceled.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
            Equal(payload, store.OpenOffline(identity.PluginId, identity.Version, identity.Subject));
            var expired = MarketDeliveryIssuer.Issue(payload, identity with { Expires = clock.Now }, device, publisher);
            Reject(() => verifier.Open(expired, identity.PluginId, identity.Version, identity.Subject));
            var future = MarketDeliveryIssuer.Issue(payload, identity with { NotBefore = clock.Now.AddMinutes(1) }, device, publisher);
            Reject(() => verifier.Open(future, identity.PluginId, identity.Version, identity.Subject));
            var file = Directory.GetFiles(root, "*.npcache").Single(); File.WriteAllBytes(file, [1, 0, 0, 0, 255, 255, 255, 127]);
            Reject(() => store.OpenOffline(identity.PluginId, identity.Version, identity.Subject));
            Console.WriteLine("PASS: encrypted Market acquisition, offline restart, permanent entitlement, expiry, identity/key binding, tamper rejection, atomic update preservation and cancellation.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Equal(byte[] a, byte[] b) { try { if (!a.SequenceEqual(b)) throw new Exception("Payload mismatch."); } finally { CryptographicOperations.ZeroMemory(b); } }
    private static void Reject(Func<byte[]> action) { try { action(); } catch (Exception e) when (e is CryptographicException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException) { return; } throw new Exception("Invalid package accepted."); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Market(EncryptedDelivery delivery) : IMarketAcquisition
    {
        public bool Offline; public int Calls;
        public Task<EncryptedDelivery> AcquireAsync(string pluginId, string version, byte[] devicePublicKey, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; if (Offline) throw new IOException("Offline"); return Task.FromResult(delivery); }
    }
}
