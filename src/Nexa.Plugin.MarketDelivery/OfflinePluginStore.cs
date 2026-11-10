using System.Security.Cryptography;
using System.Text;
namespace Nexa.Plugin.MarketDelivery;

public sealed class OfflinePluginStore(string directory, OfflinePluginVerifier verifier)
{
    public async Task AcquireAsync(IMarketAcquisition market, string pluginId, string version, string subject, byte[] devicePublicKey, CancellationToken token = default)
    {
        var delivery = await market.AcquireAsync(pluginId, version, devicePublicKey, token).ConfigureAwait(false);
        await SaveAsync(delivery, pluginId, version, subject, token).ConfigureAwait(false);
    }
    public async Task SaveAsync(EncryptedDelivery delivery, string pluginId, string version, string subject, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); delivery = Format.Copy(delivery);
        var plain = verifier.Open(delivery, pluginId, version, subject); CryptographicOperations.ZeroMemory(plain);
        Directory.CreateDirectory(directory);
        string target = PathFor(pluginId, version, subject), stage = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var writer = new BinaryWriter(file, Encoding.UTF8, true);
                writer.Write(1); writer.Write(delivery.Grant.Length); writer.Write(delivery.Signature.Length); writer.Write(delivery.Ciphertext.Length); writer.Flush();
                await file.WriteAsync(delivery.Grant, token); await file.WriteAsync(delivery.Signature, token); await file.WriteAsync(delivery.Ciphertext, token);
                await file.FlushAsync(token); file.Flush(true);
            }
            token.ThrowIfCancellationRequested(); File.Move(stage, target, true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    // Explicit offline operation: never contacts Market and never needs a bearer token.
    public byte[] OpenOffline(string pluginId, string version, string subject)
    {
        using var file = new FileStream(PathFor(pluginId, version, subject), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new BinaryReader(file, Encoding.UTF8);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("PACKAGE_INVALID");
        int grant = reader.ReadInt32(), signature = reader.ReadInt32(), cipher = reader.ReadInt32();
        if (grant is < 1 or > 8192 || signature is < 1 or > 1024 || cipher is < 1 or > Format.MaximumPayload || file.Length != 16L + grant + signature + cipher) throw new InvalidDataException("PACKAGE_INVALID");
        return verifier.Open(new(Format.Read(reader, grant), Format.Read(reader, signature), Format.Read(reader, cipher)), pluginId, version, subject);
    }
    private string PathFor(string id, string version, string subject)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        Format.Text(writer, id); Format.Text(writer, version); Format.Text(writer, subject); writer.Flush();
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(memory.ToArray())).ToLowerInvariant() + ".npcache");
    }
}
