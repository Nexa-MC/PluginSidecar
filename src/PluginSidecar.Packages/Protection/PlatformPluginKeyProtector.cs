using System.Security.Cryptography;
using PluginSidecar.Application;

namespace PluginSidecar.Protection;

/// <summary>Protects local catalog keys without loading launcher account services.</summary>
public sealed class PlatformPluginKeyProtector : IPluginKeyProtector, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IPluginSecretStore? _secrets;
    private readonly Dictionary<(bool Legacy, Guid Id), byte[]> _keys = [];
    private Guid? _writeKey;
    private bool _closed;
    public PlatformPluginKeyProtector() { if (!OperatingSystem.IsWindows()) _secrets = new PlatformPluginSecretStore(); }
    internal PlatformPluginKeyProtector(IPluginSecretStore secrets) => _secrets = secrets;

    public async ValueTask<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, CancellationToken cancellation)
    {
        if (plaintext.Length != 32) throw new CryptographicException("A catalog key must contain 32 bytes.");
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        byte[] owned = plaintext.ToArray();
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            cancellation.ThrowIfCancellationRequested();
            if (_secrets is null) return [3, .. PlatformPluginNativeProtection.Windows(owned, true, false)];
            if (_writeKey is null)
            {
                if (_keys.Count >= 128) throw new CryptographicException("Catalog key cache budget exceeded.");
                Guid id = Guid.NewGuid();
                byte[] created = RandomNumberGenerator.GetBytes(32);
                try
                {
                    await _secrets.StoreAsync(id, created, cancellation).ConfigureAwait(false);
                    _keys.Add((false, id), created.ToArray()); _writeKey = id;
                }
                finally { CryptographicOperations.ZeroMemory(created); }
            }
            cancellation.ThrowIfCancellationRequested();
            byte[] envelope = new byte[77]; envelope[0] = 4;
            _writeKey.Value.TryWriteBytes(envelope.AsSpan(1, 16));
            RandomNumberGenerator.Fill(envelope.AsSpan(17, 12));
            using var cipher = new AesGcm(_keys[(false, _writeKey.Value)], 16);
            cipher.Encrypt(envelope.AsSpan(17, 12), owned, envelope.AsSpan(45), envelope.AsSpan(29, 16), envelope.AsSpan(0, 17));
            return envelope;
        }
        finally { CryptographicOperations.ZeroMemory(owned); _gate.Release(); }
    }

    public async ValueTask<byte[]> UnprotectAsync(ReadOnlyMemory<byte> ciphertext, CancellationToken cancellation)
    {
        if (ciphertext.Length is < 2 or > 16384) throw new CryptographicException("Invalid protected catalog key.");
        // Own the envelope before an asynchronous key-store lookup.
        byte[] envelope = ciphertext.ToArray();
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            cancellation.ThrowIfCancellationRequested();
            if (_secrets is null)
            {
                if (envelope[0] is not (1 or 3)) throw new CryptographicException("Unsupported catalog key protection.");
                byte[] key = PlatformPluginNativeProtection.Windows(envelope.AsSpan(1), false, envelope[0] == 1);
                if (key.Length == 32) return key;
                CryptographicOperations.ZeroMemory(key); throw new CryptographicException("Invalid catalog key length.");
            }
            if (envelope.Length != 77 || envelope[0] is not (2 or 4)) throw new CryptographicException("Unsupported catalog key protection.");
            bool legacy = envelope[0] == 2;
            Guid id = new(envelope.AsSpan(1, 16));
            if (!_keys.TryGetValue((legacy, id), out var secret))
            {
                if (_keys.Count >= 128) throw new CryptographicException("Catalog key cache budget exceeded.");
                secret = await _secrets.ReadAsync(id, legacy, cancellation).ConfigureAwait(false);
                if (secret.Length != 32) { CryptographicOperations.ZeroMemory(secret); throw new CryptographicException("Invalid OS catalog key."); }
                _keys.Add((legacy, id), secret);
            }
            byte[] plaintext = new byte[32];
            try
            {
                cancellation.ThrowIfCancellationRequested();
                using var cipher = new AesGcm(secret, 16);
                cipher.Decrypt(envelope.AsSpan(17, 12), envelope.AsSpan(45), envelope.AsSpan(29, 16), plaintext, envelope.AsSpan(0, 17));
                if (!legacy) _writeKey = id;
                return plaintext;
            }
            catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_closed) return;
            _closed = true;
            foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
            _keys.Clear(); _writeKey = null;
        }
        finally { _gate.Release(); }
    }
}

internal interface IPluginSecretStore
{
    ValueTask StoreAsync(Guid id, ReadOnlyMemory<byte> key, CancellationToken token);
    ValueTask<byte[]> ReadAsync(Guid id, bool legacy, CancellationToken token);
}
