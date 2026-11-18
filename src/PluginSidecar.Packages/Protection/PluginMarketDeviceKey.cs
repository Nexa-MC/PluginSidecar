using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PluginSidecar.Application;

namespace PluginSidecar.Protection;

/// <summary>Persistent device identity for Market grants; never exposes private bytes to native peers.</summary>
public sealed class PluginMarketDeviceKey(string directory, IPluginKeyProtector protector)
{
    private static readonly byte[] Magic = "NEXAMD01"u8.ToArray();
    private readonly string _directory = Path.GetFullPath(directory);
    private const int MaximumFileBytes = 32768;

    /// <summary>Return a caller-owned RSA key. Corrupt or inaccessible existing identities are never replaced.</summary>
    public async Task<RSA> OpenAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var lease = await LockAsync(token).ConfigureAwait(false);
        string path = Path.Combine(_directory, "market-device.npkey");
        // Open directly: File.Exists hides access failures and must not authorize key rotation.
        FileStream? existing;
        try { existing = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true); }
        catch (FileNotFoundException) { existing = null; }
        if (existing is not null)
        {
            await using (existing) return await ReadAsync(existing, token).ConfigureAwait(false);
        }
        var rsa = RSA.Create(3072);
        byte[] plain = rsa.ExportPkcs8PrivateKey(), key = RandomNumberGenerator.GetBytes(32);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            byte[] wrapped = await protector.ProtectAsync(key, token).ConfigureAwait(false);
            if (wrapped.Length is < 1 or > 16384) throw new CryptographicException("plugin.device.protection-invalid");
            byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[plain.Length];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Magic);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                using var writer = new BinaryWriter(file, Encoding.UTF8, true);
                writer.Write(Magic); writer.Write(wrapped.Length); writer.Write(wrapped); writer.Write(nonce); writer.Write(tag);
                writer.Write(cipher.Length); writer.Flush();
                await file.WriteAsync(cipher, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, false);
            return rsa;
        }
        catch { rsa.Dispose(); throw; }
        finally
        {
            CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<RSA> ReadAsync(FileStream file, CancellationToken token)
    {
        if (file.Length is < 64 or > MaximumFileBytes) throw new InvalidDataException("plugin.device.record-invalid");
        using var reader = new BinaryReader(file, Encoding.UTF8, true);
        if (!Read(reader, Magic.Length).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("plugin.device.record-invalid");
        int size = reader.ReadInt32();
        if (size is < 1 or > 16384) throw new InvalidDataException("plugin.device.record-invalid");
        byte[] wrapped = Read(reader, size), nonce = Read(reader, 12), tag = Read(reader, 16);
        int length = reader.ReadInt32();
        if (length is < 1 or > 8192 || file.Length - file.Position != length) throw new InvalidDataException("plugin.device.record-invalid");
        byte[] cipher = new byte[length]; await file.ReadExactlyAsync(cipher, token).ConfigureAwait(false);
        byte[] plain = new byte[length], key = await protector.UnprotectAsync(wrapped, token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (key.Length != 32) throw new CryptographicException("plugin.device.protection-invalid");
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, tag, plain, Magic);
            var rsa = RSA.Create();
            try
            {
                rsa.ImportPkcs8PrivateKey(plain, out int read);
                if (read != plain.Length || rsa.KeySize != 3072) throw new CryptographicException("plugin.device.key-invalid");
                token.ThrowIfCancellationRequested();
                return rsa;
            }
            catch { rsa.Dispose(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<FileStream> LockAsync(CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new(Path.Combine(_directory, ".market-device.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33
                || (OperatingSystem.IsMacOS() && (error.HResult & 0xffff) == 35))
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5)) throw new IOException("plugin.device.busy", error);
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }

    private static byte[] Read(BinaryReader reader, int size)
    { var bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException(); return bytes; }
}
