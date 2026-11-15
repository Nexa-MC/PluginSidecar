using System.Security.Cryptography;
using Nexa.Plugin.MarketDelivery;

namespace PluginSidecar;

/// <summary>Reviewed publisher SPKI pins embedded in the Worker, never supplied by Market or a guest.</summary>
internal sealed class PluginMarketTrust : IDisposable
{
    private readonly List<RSA> _publishers = [];
    private readonly object _gate = new();
    private bool _disposed;
    internal bool Available => _publishers.Count != 0;
    internal static PluginMarketTrust Load()
    {
        using var stream = typeof(PluginMarketTrust).Assembly.GetManifestResourceStream("PluginSidecar.MarketPublishers")
            ?? throw new InvalidDataException("plugin.market.trust-missing");
        using var reader = new StreamReader(stream);
        return new(reader.ReadToEnd());
    }
    internal PluginMarketTrust(string reviewedPins)
    {
        if (reviewedPins.Length > 32768) throw new InvalidDataException("plugin.market.trust-invalid");
        try
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string raw in reviewedPins.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (_publishers.Count >= 4 || !seen.Add(line)) throw new InvalidDataException("plugin.market.trust-invalid");
                byte[] spki = Convert.FromBase64String(line);
                var key = RSA.Create();
                try
                {
                    key.ImportSubjectPublicKeyInfo(spki, out int used);
                    if (used != spki.Length || key.KeySize is < 2048 or > 8192
                        || !spki.AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
                        throw new InvalidDataException("plugin.market.trust-invalid");
                    _publishers.Add(key);
                }
                catch { key.Dispose(); throw; }
            }
        }
        catch { Dispose(); throw; }
    }
    internal byte[] Open(RSA device, EncryptedDelivery delivery, string id, string version, string subject)
    {
        if (delivery.Grant.Length is < 1 or > 8192 || delivery.Signature.Length is < 1 or > 1024
            || delivery.Ciphertext.Length is < 1 or > 25 * 1024 * 1024) throw new InvalidDataException("plugin.market.delivery-invalid");
        // Verify the same immutable snapshot when selecting a pin and opening the entitlement.
        var copy = new EncryptedDelivery(delivery.Grant.ToArray(), delivery.Signature.ToArray(), delivery.Ciphertext.ToArray());
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var key in _publishers)
                if (key.VerifyData(copy.Grant, copy.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    return new OfflinePluginVerifier(key, device).Open(copy, id, version, subject);
            throw new CryptographicException("plugin.market.publisher-untrusted");
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var key in _publishers) key.Dispose();
        }
    }
}
