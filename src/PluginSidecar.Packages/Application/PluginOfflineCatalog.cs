using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PluginSidecar.Packages;

namespace PluginSidecar.Application;

/// <summary>Explicitly approved developer installations. Never accepts plugin-provided grants or paths.</summary>
public sealed partial class PluginOfflineCatalog : IDisposable
{
    private const int MaximumBytes = 27 * 1024 * 1024;
    private static readonly byte[] Magic = "NEXAPC01"u8.ToArray();
    private readonly string _directory;
    private readonly IPluginKeyProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public PluginOfflineCatalog(string directory, IPluginKeyProtector protector)
    { _directory = Path.GetFullPath(directory); _protector = protector ?? throw new ArgumentNullException(nameof(protector)); }

    public ImmutableArray<string> Records()
    {
        if (!Directory.Exists(_directory)) return [];
        var files = Directory.EnumerateFiles(_directory, "*.npinstall").Take(65).Order(StringComparer.Ordinal).ToImmutableArray();
        if (files.Length > 64) throw new IOException("plugin.catalog.limit");
        return files;
    }
    private string PathFor(string id) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant() + ".npinstall");

    public async Task SaveAsync(byte[] packageBytes, string id, string version, ImmutableArray<string> grants, CancellationToken token)
        => _ = await SaveCoreAsync(packageBytes, id, version, grants, null, false, token).ConfigureAwait(false);

    /// <summary>Replace exactly the observed record, or create it when expectedRevision is null.</summary>
    public Task<string> SaveIfUnchangedAsync(byte[] packageBytes, string id, string version, ImmutableArray<string> grants,
        string? expectedRevision, CancellationToken token)
    {
        ValidateRevision(expectedRevision);
        return SaveCoreAsync(packageBytes, id, version, grants, expectedRevision, true, token);
    }

    private async Task<string> SaveCoreAsync(byte[] packageBytes, string id, string version, ImmutableArray<string> grants,
        string? expectedRevision, bool conditional, CancellationToken token)
    {
        using var package = VerifiedPluginPackage.Parse(packageBytes, id, version);
        ValidateGrants(package, grants);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        byte[]? plain = null; byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
            else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var lease = await AcquireFileGateAsync(token).ConfigureAwait(false);
            string destination = PathFor(id);
            if (conditional && await ReadRevisionCoreAsync(destination, token).ConfigureAwait(false) != expectedRevision)
                throw new IOException("plugin.catalog.conflict");
            if (!File.Exists(destination) && Records().Length >= 64) throw new IOException("plugin.catalog.limit");
            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
            {
                Text(writer, id); Text(writer, version); writer.Write(grants.Length);
                foreach (var grant in grants) Text(writer, grant);
                writer.Write(packageBytes.Length); writer.Write(packageBytes); writer.Flush();
            }
            plain = payload.ToArray();
            CryptographicOperations.ZeroMemory(payload.GetBuffer().AsSpan(0, (int)payload.Length));
            byte[] wrapped = await _protector.ProtectAsync(key, token).ConfigureAwait(false), nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[plain.Length];
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

    public async Task<PluginOfflineInstall> OpenAsync(string record, CancellationToken token)
    {
        if (!Records().Contains(record, StringComparer.Ordinal)) throw new IOException("plugin.catalog.record-invalid");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        byte[]? key = null, plain = null;
        try
        {
            using var lease = await AcquireFileGateAsync(token).ConfigureAwait(false);
            await using var file = new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length is < 64 or > MaximumBytes) throw new IOException("plugin.catalog.record-invalid");
            string revision = await HashAsync(file, token).ConfigureAwait(false);
            file.Position = 0;
            using var reader = new BinaryReader(file, new UTF8Encoding(false, true), true);
            if (!Read(reader, Magic.Length).AsSpan().SequenceEqual(Magic)) throw new IOException("plugin.catalog.record-invalid");
            int wrappedSize = reader.ReadInt32();
            if (wrappedSize is < 1 or > 16384) throw new IOException("plugin.catalog.record-invalid");
            byte[] wrapped = Read(reader, wrappedSize);
            key = await _protector.UnprotectAsync(wrapped, token).ConfigureAwait(false);
            byte[] nonce = Read(reader, 12), tag = Read(reader, 16); int size = reader.ReadInt32();
            if (key.Length != 32 || size is < 1 or > MaximumBytes || file.Length - file.Position != size) throw new IOException("plugin.catalog.record-invalid");
            byte[] cipher = new byte[size]; await file.ReadExactlyAsync(cipher, token).ConfigureAwait(false); plain = new byte[size];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, tag, plain, Magic);
            using var payload = new MemoryStream(plain, false); using var input = new BinaryReader(payload, new UTF8Encoding(false, true));
            string id = Text(input), version = Text(input); int count = input.ReadInt32();
            if (count is < 0 or > 128 || PathFor(id) != record) throw new IOException("plugin.catalog.record-invalid");
            var grants = ImmutableArray.CreateBuilder<string>(count); for (int i = 0; i < count; i++) grants.Add(Text(input));
            int packageSize = input.ReadInt32();
            if (packageSize is < 1 or > 25 * 1024 * 1024 || payload.Length - payload.Position != packageSize) throw new IOException("plugin.catalog.record-invalid");
            byte[] bytes = Read(input, packageSize);
            try
            {
                using var package = VerifiedPluginPackage.Parse(bytes, id, version);
                ValidateGrants(package, grants.ToImmutable());
                return new(id, version, grants.ToImmutable(), bytes) { Revision = revision };
            }
            catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); if (plain is not null) CryptographicOperations.ZeroMemory(plain); _gate.Release(); }
    }
    public async Task RemoveAsync(string id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory)) return;
            using var lease = await AcquireFileGateAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Delete(PathFor(id));
        }
        finally { _gate.Release(); }
    }
    private static void ValidateGrants(VerifiedPluginPackage package, ImmutableArray<string> grants)
    {
        if (grants.Length > 128 || grants.Distinct(StringComparer.Ordinal).Count() != grants.Length || grants.Any(g => !package.Manifest.RequestedPermissions.Contains(g)))
            throw new IOException("plugin.catalog.grants-invalid");
    }
    private static void Text(BinaryWriter writer, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); if (bytes.Length is < 1 or > 1024) throw new IOException("plugin.catalog.text-invalid"); writer.Write(bytes.Length); writer.Write(bytes); }
    private static string Text(BinaryReader reader)
    { int size = reader.ReadInt32(); if (size is < 1 or > 1024) throw new IOException("plugin.catalog.text-invalid"); return new UTF8Encoding(false, true).GetString(Read(reader, size)); }
    private static byte[] Read(BinaryReader reader, int size)
    { var bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException(); return bytes; }
    public void Dispose() { if (_protector is IDisposable disposable) disposable.Dispose(); _gate.Dispose(); }
}

public sealed record PluginOfflineInstall(string Id, string Version, ImmutableArray<string> Grants, byte[] Package) : IDisposable
{
    public string Revision { get; init; } = "";
    public void Dispose() => CryptographicOperations.ZeroMemory(Package);
}
