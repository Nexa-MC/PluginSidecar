using System.Security.Cryptography;
using System.Text;
namespace Nexa.Plugin.MarketDelivery;

public sealed record PluginEntitlement(string PluginId, string Version, string Subject, DateTimeOffset NotBefore, DateTimeOffset? Expires);
public sealed record EncryptedDelivery(byte[] Grant, byte[] Signature, byte[] Ciphertext);
/// <summary>Market adapter: returns only a server-authorized, signed encrypted delivery. Cancellation is never offline fallback.</summary>
public interface IMarketAcquisition
{
    Task<EncryptedDelivery> AcquireAsync(string pluginId, string version, byte[] devicePublicKey, CancellationToken token);
}
public static class MarketDeliveryIssuer
{
    /// <summary>Trusted Market-side operation AFTER ownership/security/eligibility checks. Never ship signing keys to clients.</summary>
    public static EncryptedDelivery Issue(ReadOnlySpan<byte> payload, PluginEntitlement entitlement, RSA devicePublicKey, RSA publisherSigningKey)
    {
        Format.ValidateEntitlement(entitlement);
        if (payload.Length is <= 0 or > Format.MaximumPayload) throw new InvalidDataException("PACKAGE_INVALID");
        if (devicePublicKey.KeySize < 2048 || publisherSigningKey.KeySize < 2048) throw new CryptographicException("Key too small.");
        byte[] key = RandomNumberGenerator.GetBytes(32), nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[payload.Length];
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, payload, cipher, tag);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(1); Format.Text(writer, entitlement.PluginId); Format.Text(writer, entitlement.Version); Format.Text(writer, entitlement.Subject);
            writer.Write(entitlement.NotBefore.ToUnixTimeSeconds()); writer.Write(entitlement.Expires?.ToUnixTimeSeconds() ?? long.MaxValue);
            writer.Write(SHA256.HashData(devicePublicKey.ExportSubjectPublicKeyInfo()));
            writer.Write(SHA256.HashData(payload)); writer.Write(SHA256.HashData(cipher)); writer.Write(cipher.Length);
            writer.Write(nonce); writer.Write(tag); var wrapped = devicePublicKey.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
            writer.Write(wrapped.Length); writer.Write(wrapped); writer.Flush();
            byte[] grant = stream.ToArray();
            return new(grant, publisherSigningKey.SignData(grant, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), cipher);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
public sealed class OfflinePluginVerifier(RSA trustedPublisherKey, RSA devicePrivateKey, TimeProvider? clock = null)
{
    /// <summary>Returns caller-owned plaintext. Caller must clear it after parsing/loading and still obtain Host LoadPermit.</summary>
    public byte[] Open(EncryptedDelivery delivery, string pluginId, string version, string subject)
    {
        // Snapshot mutable input once; cache/verification must refer to identical bytes.
        delivery = Format.Copy(delivery);
        if (trustedPublisherKey.KeySize < 2048 || devicePrivateKey.KeySize < 2048 ||
            !trustedPublisherKey.VerifyData(delivery.Grant, delivery.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("ABI_CONTRACT_MISMATCH");
        using var stream = new MemoryStream(delivery.Grant, false); using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        if (reader.ReadInt32() != 1) throw new InvalidDataException("PACKAGE_INVALID");
        string id = Format.Text(reader), v = Format.Text(reader), owner = Format.Text(reader);
        long start = reader.ReadInt64(), end = reader.ReadInt64();
        if (id != pluginId || v != version || owner != subject) throw new UnauthorizedAccessException("AUTHORIZATION_MISMATCH");
        long now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        if (end <= start) throw new InvalidDataException("PACKAGE_INVALID");
        if (now < start) throw new UnauthorizedAccessException("AUTHORIZATION_NOT_YET_VALID");
        if (now >= end) throw new UnauthorizedAccessException("AUTHORIZATION_EXPIRED");
        if (!CryptographicOperations.FixedTimeEquals(Format.Read(reader, 32), SHA256.HashData(devicePrivateKey.ExportSubjectPublicKeyInfo()))) throw new UnauthorizedAccessException("AUTHORIZATION_MISMATCH");
        byte[] plainHash = Format.Read(reader, 32), cipherHash = Format.Read(reader, 32); int size = reader.ReadInt32();
        if (size != delivery.Ciphertext.Length || !CryptographicOperations.FixedTimeEquals(cipherHash, SHA256.HashData(delivery.Ciphertext))) throw new CryptographicException("HASH_MISMATCH");
        byte[] nonce = Format.Read(reader, 12), tag = Format.Read(reader, 16); int wrappedSize = reader.ReadInt32();
        if (wrappedSize != devicePrivateKey.KeySize / 8) throw new InvalidDataException("PACKAGE_INVALID");
        byte[] wrapped = Format.Read(reader, wrappedSize); if (stream.Position != stream.Length) throw new InvalidDataException("PACKAGE_INVALID");
        byte[] key = devicePrivateKey.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256), plain = new byte[size]; bool success = false;
        try
        {
            if (key.Length != 32) throw new CryptographicException("DECRYPT_FAILED");
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, delivery.Ciphertext, tag, plain);
            if (!CryptographicOperations.FixedTimeEquals(plainHash, SHA256.HashData(plain))) throw new CryptographicException("HASH_MISMATCH");
            success = true; return plain;
        }
        finally { CryptographicOperations.ZeroMemory(key); if (!success) CryptographicOperations.ZeroMemory(plain); }
    }
}
internal static class Format
{
    internal const int MaximumPayload = 25 * 1024 * 1024;
    internal static void ValidateEntitlement(PluginEntitlement e)
    {
        foreach (var value in new[] { e.PluginId, e.Version, e.Subject }) if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 1024 || value.Any(char.IsControl)) throw new InvalidDataException("PACKAGE_INVALID");
        if (e.Expires is { } end && end <= e.NotBefore) throw new InvalidDataException("PACKAGE_INVALID");
    }
    internal static void Text(BinaryWriter w, string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); w.Write(bytes.Length); w.Write(bytes); }
    internal static string Text(BinaryReader r) { int size = r.ReadInt32(); if (size is < 1 or > 1024) throw new InvalidDataException("PACKAGE_INVALID"); return new UTF8Encoding(false, true).GetString(Read(r, size)); }
    internal static byte[] Read(BinaryReader r, int size) { var b = r.ReadBytes(size); if (b.Length != size) throw new EndOfStreamException(); return b; }
    internal static EncryptedDelivery Copy(EncryptedDelivery d)
    {
        if (d.Grant.Length is < 1 or > 8192 || d.Signature.Length is < 1 or > 1024 || d.Ciphertext.Length is < 1 or > MaximumPayload) throw new InvalidDataException("PACKAGE_INVALID");
        return new(d.Grant.ToArray(), d.Signature.ToArray(), d.Ciphertext.ToArray());
    }
}
