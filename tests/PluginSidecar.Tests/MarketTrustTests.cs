using System.Security.Cryptography;
using Nexa.Plugin.MarketDelivery;
using PluginSidecar;

internal static class MarketTrustTests
{
    internal static void Run()
    {
        using var publisher = RSA.Create(2048); using var old = RSA.Create(2048); using var device = RSA.Create(3072); using var stranger = RSA.Create(2048);
        string current = Convert.ToBase64String(publisher.ExportSubjectPublicKeyInfo()), prior = Convert.ToBase64String(old.ExportSubjectPublicKeyInfo());
        using var trust = new PluginMarketTrust("# reviewed publisher rotation\n" + current + "\n" + prior);
        var grant = new PluginEntitlement("example.test", "1.0.0", "account", DateTimeOffset.UtcNow.AddMinutes(-1), null);
        byte[] plain = [1, 2, 3, 4];
        foreach (var signer in new[] { publisher, old })
        {
            var delivery = MarketDeliveryIssuer.Issue(plain, grant, device, signer);
            byte[] opened = trust.Open(device, delivery, grant.PluginId, grant.Version, grant.Subject);
            Check(opened.SequenceEqual(plain)); CryptographicOperations.ZeroMemory(opened);
            Reject(() => trust.Open(device, delivery, grant.PluginId, grant.Version, "other-account"));
            Reject(() => trust.Open(stranger, delivery, grant.PluginId, grant.Version, grant.Subject));
            delivery.Ciphertext[0] ^= 1;
            Reject(() => trust.Open(device, delivery, grant.PluginId, grant.Version, grant.Subject));
        }
        var untrusted = MarketDeliveryIssuer.Issue(plain, grant, device, stranger);
        Reject(() => trust.Open(device, untrusted, grant.PluginId, grant.Version, grant.Subject));
        using var absent = new PluginMarketTrust("# not provisioned"); Check(!absent.Available);
        Reject(() => absent.Open(device, untrusted, grant.PluginId, grant.Version, grant.Subject));
        Reject(() => { using var bad = new PluginMarketTrust(current + "\n" + current); });
        Reject(() => { using var bad = new PluginMarketTrust(Convert.ToBase64String(publisher.ExportPkcs8PrivateKey())); });
        Reject(() => { using var bad = new PluginMarketTrust(Convert.ToBase64String([.. publisher.ExportSubjectPublicKeyInfo(), 0])); });
        trust.Dispose(); Reject(() => trust.Open(device, untrusted, grant.PluginId, grant.Version, grant.Subject));
        Console.WriteLine("PASS: reviewed publisher pins preserve old offline grants, reject foreign signatures/devices/accounts, malformed keys and ciphertext tampering.");
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Market trust assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { return; }
        throw new InvalidOperationException("Invalid Market trust accepted.");
    }
}
