namespace PluginSidecar.Application;

/// <summary>Asynchronous, OS-backed protection of the local installation catalog's encryption keys.</summary>
/// <remarks>The Sidecar owns catalog parsing and grants. This port never grants a guest access to keys.</remarks>
public interface IPluginKeyProtector
{
    ValueTask<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, CancellationToken cancellation);
    ValueTask<byte[]> UnprotectAsync(ReadOnlyMemory<byte> ciphertext, CancellationToken cancellation);
}
