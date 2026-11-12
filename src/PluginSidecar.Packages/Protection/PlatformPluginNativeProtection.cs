using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PluginSidecar.Protection;

internal static partial class PlatformPluginNativeProtection
{
    private static readonly byte[] Entropy = "NexaCL.PluginSidecar.Catalog.v1"u8.ToArray();
    internal static unsafe byte[] Windows(ReadOnlySpan<byte> data, bool protect, bool legacy)
    {
        fixed (byte* inputBytes = data, entropyBytes = Entropy)
        {
            NativeBlob input = new() { Length = data.Length, Data = (nint)inputBytes };
            NativeBlob entropy = new() { Length = Entropy.Length, Data = (nint)entropyBytes };
            nint entropyPointer = legacy ? 0 : (nint)(&entropy);
            int ok = protect ? CryptProtectData(ref input, 0, entropyPointer, 0, 0, 1, out var output)
                : CryptUnprotectData(ref input, 0, entropyPointer, 0, 0, 1, out output);
            if (ok == 0) throw new CryptographicException("Current-user catalog key protection is unavailable.");
            try
            {
                if (output.Data == 0 || output.Length is < 1 or > 16383) throw new CryptographicException("Invalid protected catalog key.");
                return new ReadOnlySpan<byte>((void*)output.Data, output.Length).ToArray();
            }
            finally
            {
                if (!protect && output.Data != 0 && output.Length is > 0 and <= 16383)
                    CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Length));
                _ = LocalFree(output.Data);
            }
        }
    }

    internal static unsafe byte[] MacRead(Guid id, bool legacy)
    {
        byte[] service = Encoding.UTF8.GetBytes(legacy ? "NexaCL.accounts" : "NexaCL.PluginSidecar.catalog");
        byte[] account = Encoding.UTF8.GetBytes(id.ToString("N"));
        // A locked keychain fails closed; key access must not strand the worker on a modal OS prompt.
        if (SecKeychainSetUserInteractionAllowed(0) != 0) throw new IOException("Catalog keychain is unavailable.");
        fixed (byte* serviceBytes = service, accountBytes = account)
        {
            int status = SecKeychainFindGenericPassword(0, (uint)service.Length, serviceBytes, (uint)account.Length, accountBytes,
                out uint length, out nint data, 0);
            if (status != 0) throw new IOException("Catalog key is missing or the keychain is locked.");
            try
            {
                if (length != 32 || data == 0) throw new CryptographicException("Invalid catalog keychain value.");
                return new ReadOnlySpan<byte>((void*)data, 32).ToArray();
            }
            finally { _ = SecKeychainItemFreeContent(0, data); }
        }
    }

    internal static unsafe void MacStore(Guid id, ReadOnlySpan<byte> key)
    {
        byte[] service = "NexaCL.PluginSidecar.catalog"u8.ToArray(), account = Encoding.UTF8.GetBytes(id.ToString("N"));
        if (SecKeychainSetUserInteractionAllowed(0) != 0) throw new IOException("Catalog keychain is unavailable.");
        fixed (byte* serviceBytes = service, accountBytes = account, secret = key)
            if (SecKeychainAddGenericPassword(0, (uint)service.Length, serviceBytes, (uint)account.Length, accountBytes,
                (uint)key.Length, secret, 0) != 0) throw new IOException("Catalog key cannot be stored in the keychain.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBlob { internal int Length; internal nint Data; }
    [LibraryImport("crypt32.dll", SetLastError = true)]
    private static partial int CryptProtectData(ref NativeBlob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out NativeBlob output);
    [LibraryImport("crypt32.dll", SetLastError = true)]
    private static partial int CryptUnprotectData(ref NativeBlob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out NativeBlob output);
    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [LibraryImport(Security)]
    private static partial int SecKeychainSetUserInteractionAllowed(byte state);
    [LibraryImport(Security)]
    private static unsafe partial int SecKeychainAddGenericPassword(nint keychain, uint serviceLength, byte* service, uint accountLength, byte* account, uint passwordLength, byte* password, nint item);
    [LibraryImport(Security)]
    private static unsafe partial int SecKeychainFindGenericPassword(nint keychain, uint serviceLength, byte* service, uint accountLength, byte* account, out uint passwordLength, out nint password, nint item);
    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(nint attributes, nint data);
}
