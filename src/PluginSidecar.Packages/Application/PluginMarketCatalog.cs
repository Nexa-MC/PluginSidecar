using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Nexa.Plugin.MarketDelivery;
using PluginSidecar.Packages;

namespace PluginSidecar.Application;

/// <summary>Locally approved Market entitlements; original signatures and ciphertext are verified on every open.</summary>
public sealed partial class PluginMarketCatalog : IDisposable
{
    private const int MaximumBytes = 27 * 1024 * 1024;
    private static readonly byte[] Magic = "NEXAMC01"u8.ToArray();
    private readonly string _directory;
    private readonly IPluginKeyProtector _protector;
    private readonly OfflinePluginVerifier _verifier;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public PluginMarketCatalog(string directory, IPluginKeyProtector protector, OfflinePluginVerifier verifier)
    { _directory = Path.GetFullPath(directory); _protector = protector ?? throw new ArgumentNullException(nameof(protector)); _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier)); }

    public ImmutableArray<string> Records()
    {
        if (!Directory.Exists(_directory)) return [];
        var files = Directory.EnumerateFiles(_directory, "*.npmarket").Take(65).Order(StringComparer.Ordinal).ToImmutableArray();
        if (files.Length > 64) throw new IOException("plugin.market.limit");
        return files;
    }
    private string PathFor(string id) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant() + ".npmarket");

    /// <summary>Replace exactly the observed record, or create it when expectedRevision is null.</summary>
    public Task<string> SaveIfUnchangedAsync(EncryptedDelivery delivery, string id, string version, string subject,
        ImmutableArray<string> grants, string? expectedRevision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateRevision(expectedRevision);
        // Snapshot the mutable delivery before waiting for either local or cross-process ownership.
        var copy = Copy(delivery);
        return SaveCoreAsync(copy, id, version, subject, grants, expectedRevision, token);
    }

    private async Task<string> SaveCoreAsync(EncryptedDelivery delivery, string id, string version, string subject,
        ImmutableArray<string> grants, string? expectedRevision, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        byte[]? plain = null; byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            using (var package = OpenPackage(delivery, id, version, subject)) ValidateGrants(package, grants);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
            else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var lease = await AcquireFileGateAsync(token).ConfigureAwait(false);
            string destination = PathFor(id);
            if (await ReadRevisionCoreAsync(destination, token).ConfigureAwait(false) != expectedRevision)
                throw new IOException("plugin.market.conflict");
            if (!File.Exists(destination) && Records().Length >= 64) throw new IOException("plugin.market.limit");
            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
            {
                Text(writer, id); Text(writer, version); Text(writer, subject); writer.Write(grants.Length);
                foreach (var grant in grants) Text(writer, grant);
                Blob(writer, delivery.Grant); Blob(writer, delivery.Signature); Blob(writer, delivery.Ciphertext); writer.Flush();
            }
            plain = payload.ToArray();
            CryptographicOperations.ZeroMemory(payload.GetBuffer().AsSpan(0, (int)payload.Length));
            byte[] wrapped = await _protector.ProtectAsync(key, token).ConfigureAwait(false), nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[plain.Length];
            if (wrapped.Length is < 1 or > 16384 || plain.Length > MaximumBytes - 16432)
                throw new InvalidDataException("plugin.market.record-invalid");
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Magic);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string revision;
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true))
                {
                    using var writer = new BinaryWriter(file, Encoding.UTF8, true);
                    writer.Write(Magic); writer.Write(wrapped.Length); writer.Write(wrapped); writer.Write(nonce); writer.Write(tag); writer.Write(cipher.Length); writer.Flush();
                    await file.WriteAsync(cipher, token).ConfigureAwait(false); await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true);
                    file.Position = 0;
                    revision = await HashAsync(file, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
                return revision;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(key); if (plain is not null) CryptographicOperations.ZeroMemory(plain); _gate.Release(); }
    }

    /// <summary>Open the existing installation for this ID; missing or invalid records fail closed.</summary>
    public Task<PluginMarketInstall> OpenInstalledAsync(string id, CancellationToken token)
        => OpenAsync(PathFor(id), token);

    public async Task<PluginMarketInstall> OpenAsync(string record, CancellationToken token)
    {
        if (!Records().Contains(record, StringComparer.Ordinal)) throw new IOException("plugin.market.record-invalid");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        byte[]? key = null, plain = null;
        try
        {
            using var lease = await AcquireFileGateAsync(token).ConfigureAwait(false);
            await using var file = new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length is < 64 or > MaximumBytes) throw new IOException("plugin.market.record-invalid");
            string revision = await HashAsync(file, token).ConfigureAwait(false);
            file.Position = 0;
            using var reader = new BinaryReader(file, new UTF8Encoding(false, true), true);
            if (!Read(reader, Magic.Length).AsSpan().SequenceEqual(Magic)) throw new IOException("plugin.market.record-invalid");
            int wrappedSize = reader.ReadInt32();
            if (wrappedSize is < 1 or > 16384) throw new IOException("plugin.market.record-invalid");
            byte[] wrapped = Read(reader, wrappedSize);
            key = await _protector.UnprotectAsync(wrapped, token).ConfigureAwait(false);
            byte[] nonce = Read(reader, 12), tag = Read(reader, 16); int size = reader.ReadInt32();
            if (key.Length != 32 || size is < 1 or > MaximumBytes || file.Length - file.Position != size) throw new IOException("plugin.market.record-invalid");
            byte[] cipher = new byte[size]; await file.ReadExactlyAsync(cipher, token).ConfigureAwait(false); plain = new byte[size];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, tag, plain, Magic);
            using var payload = new MemoryStream(plain, false); using var input = new BinaryReader(payload, new UTF8Encoding(false, true));
            string id = Text(input), version = Text(input), subject = Text(input); int count = input.ReadInt32();
            if (count is < 0 or > 128 || PathFor(id) != record) throw new IOException("plugin.market.record-invalid");
            var grants = ImmutableArray.CreateBuilder<string>(count); for (int i = 0; i < count; i++) grants.Add(Text(input));
            var delivery = new EncryptedDelivery(Blob(input, 8192), Blob(input, 1024), Blob(input, 25 * 1024 * 1024));
            if (payload.Position != payload.Length) throw new IOException("plugin.market.record-invalid");
            using var package = OpenPackage(delivery, id, version, subject);
            ValidateGrants(package, grants.ToImmutable());
            return new(id, version, subject, grants.ToImmutable(), delivery) { Revision = revision };
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); if (plain is not null) CryptographicOperations.ZeroMemory(plain); _gate.Release(); }
    }
    private static void ValidateGrants(VerifiedPluginPackage package, ImmutableArray<string> grants)
    {
        if (grants.IsDefault || grants.Length > 128 || grants.Distinct(StringComparer.Ordinal).Count() != grants.Length || grants.Any(g => !package.Manifest.RequestedPermissions.Contains(g)))
            throw new IOException("plugin.market.grants-invalid");
    }
    private static void Text(BinaryWriter writer, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); if (bytes.Length is < 1 or > 1024) throw new IOException("plugin.market.text-invalid"); writer.Write(bytes.Length); writer.Write(bytes); }
    private static string Text(BinaryReader reader)
    { int size = reader.ReadInt32(); if (size is < 1 or > 1024) throw new IOException("plugin.market.text-invalid"); return new UTF8Encoding(false, true).GetString(Read(reader, size)); }
    private static byte[] Read(BinaryReader reader, int size)
    { var bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException(); return bytes; }
    public void Dispose() => _gate.Dispose();

    public VerifiedPluginPackage OpenPackage(EncryptedDelivery delivery, string id, string version, string subject)
    {
        byte[] bytes = _verifier.Open(delivery, id, version, subject);
        try { return VerifiedPluginPackage.Parse(bytes, id, version); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static EncryptedDelivery Copy(EncryptedDelivery delivery)
    {
        if (delivery.Grant.Length is < 1 or > 8192 || delivery.Signature.Length is < 1 or > 1024
            || delivery.Ciphertext.Length is < 1 or > 25 * 1024 * 1024) throw new InvalidDataException("plugin.market.delivery-invalid");
        return new(delivery.Grant.ToArray(), delivery.Signature.ToArray(), delivery.Ciphertext.ToArray());
    }
    private static void Blob(BinaryWriter writer, byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    private static byte[] Blob(BinaryReader reader, int limit)
    { int length = reader.ReadInt32(); if (length is < 1 || length > limit) throw new InvalidDataException("plugin.market.delivery-invalid"); return Read(reader, length); }
}

public sealed record PluginMarketInstall(string Id, string Version, string Subject, ImmutableArray<string> Grants, EncryptedDelivery Delivery)
{
    public string Revision { get; init; } = "";
}
