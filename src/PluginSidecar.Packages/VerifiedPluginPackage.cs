using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.MarketDelivery;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Plugin.Sdk.Abi100;
using PluginSidecar.WasmRuntime;
namespace PluginSidecar.Packages;

/// <summary>Verified plaintext is scoped to a load source, cleared after compilation and never extracted to disk.</summary>
public sealed class VerifiedPluginPackage : IDisposable
{
    private Dictionary<string, byte[]>? _payloads;
    public PluginManifest Manifest { get; }
    private VerifiedPluginPackage(PluginManifest manifest, Dictionary<string, byte[]> payloads) { Manifest = manifest; _payloads = payloads; }
    public static VerifiedPluginPackage OpenOffline(OfflinePluginStore store, string pluginId, string version, string subject)
    {
        byte[] plaintext = store.OpenOffline(pluginId, version, subject);
        try { return Parse(plaintext, pluginId, version); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    /// <summary>Parse only bytes already authenticated by Market or an explicit trusted developer workflow.</summary>
    public static VerifiedPluginPackage Parse(ReadOnlySpan<byte> verifiedPlaintext, string expectedPluginId, string expectedVersion)
    {
        if (verifiedPlaintext.Length is < 1 or > 25 * 1024 * 1024) throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
        using var buffer = new SensitiveBuffer(verifiedPlaintext.ToArray());
        using var stream = new MemoryStream(buffer.Bytes, false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 2 or > 128) throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            string path = entry.FullName;
            if (!SafePath(path) || !entries.TryAdd(path, entry) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 ||
                entry.Length < 0 || (total += entry.Length) > 25 * 1024 * 1024 || entry.Length > 1024 * 1024 && entry.Length > Math.Max(1, entry.CompressedLength) * 100)
                throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
        }
        if (!entries.TryGetValue("plugin.json", out var manifestEntry) || manifestEntry.Length > 64 * 1024) throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
        PluginManifest manifest;
        using (var content = manifestEntry.Open()) manifest = JsonSerializer.Deserialize(content, PackageJsonContext.Default.PluginManifest) ?? throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
        if (manifest.PluginId != expectedPluginId || manifest.PluginVersion != expectedVersion || manifest.ManifestVersion != 1 || manifest.BootstrapVersion != 1 || manifest.Target is null || manifest.Target.Runtime is not ("nexa-wasm-1" or "nexa-content-1") ||
            manifest.Payloads.IsDefault || manifest.Payloads.Length != entries.Count - 1 ||
            manifest.Target.Runtime == "nexa-wasm-1" && (manifest.WebAssemblyEntryPath is null || !SafePath(manifest.WebAssemblyEntryPath)) ||
            manifest.Target.Runtime == "nexa-content-1" && (manifest.WebAssemblyEntryPath is not null || manifest.DeclarativeEntryPath is null) ||
            manifest.DeclarativeEntryPath is { } contentPath && !SafePath(contentPath))
            throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
        if (manifest.Target.OperatingSystems.IsDefault || manifest.Target.Architectures.IsDefault ||
            manifest.RequiredCapabilities.IsDefault || manifest.OptionalCapabilities.IsDefault ||
            manifest.RequiredCapabilities.Concat(manifest.OptionalCapabilities).Any(c => c is null || string.IsNullOrWhiteSpace(c.ContractId) || c.MinimumVersion == 0))
            throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
        string operatingSystem = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unsupported";
        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        if (!manifest.Target.OperatingSystems.IsEmpty && !manifest.Target.OperatingSystems.Contains(operatingSystem) ||
            !manifest.Target.Architectures.IsEmpty && !manifest.Target.Architectures.Contains(architecture)) throw new InvalidDataException(AbiErrors.PAYLOAD_KIND_UNSUPPORTED);
        var payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var descriptor in manifest.Payloads)
            {
                if (descriptor is null || !SafePath(descriptor.Path) || descriptor.Path.Equals("plugin.json", StringComparison.OrdinalIgnoreCase) || payloads.ContainsKey(descriptor.Path) || !entries.TryGetValue(descriptor.Path, out var entry) || descriptor.Size != entry.Length || descriptor.Sha256 is null || descriptor.Sha256.Length != 64)
                    throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
                using var content = entry.Open(); byte[] bytes = new byte[checked((int)descriptor.Size)]; payloads.Add(descriptor.Path, bytes);
                content.ReadExactly(bytes);
                if (content.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(descriptor.Sha256))) throw new CryptographicException(AbiErrors.HASH_MISMATCH);
            }
            if (manifest.WebAssemblyEntryPath is { } wasmPath && !payloads.ContainsKey(wasmPath) || manifest.DeclarativeEntryPath is { } declarativePath && !payloads.ContainsKey(declarativePath)) throw new InvalidDataException(AbiErrors.ENTRY_INVALID);
            if (manifest.DeclarativeEntryPath is { } path)
            {
                if (manifest.RequiredCapabilities.IsDefault || !manifest.RequiredCapabilities.Any(c => c.ContractId is "nexa.definitions" or "nexa.definitions/100" && c.MinimumVersion == 1) ||
                    manifest.RequestedPermissions.IsDefault || !manifest.RequestedPermissions.Contains("nexa.definitions.write")) throw new InvalidDataException(AbiErrors.CAPABILITY_DENIED);
                var content = DeclarativeContentPack.Read(AbiValueBinary.Decode(payloads[path]));
                if (content.Schema != Abi100Schemas.DeclarativeContentPack || !content.Id.Value.StartsWith(manifest.PluginId + ":", StringComparison.Ordinal) || content.Definitions.Length > 4096) throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
            }
            return new(manifest, payloads);
        }
        catch { foreach (var bytes in payloads.Values) CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    /// <summary>Trusted Host asset copy. Ownership transfers to the caller, which must clear it on retirement.</summary>
    public byte[] CopyPayload(string path) => (_payloads ?? throw new ObjectDisposedException(nameof(VerifiedPluginPackage)))
        .TryGetValue(path, out var bytes) ? bytes.ToArray() : throw new FileNotFoundException("Verified payload missing.");

    public PluginLoadSource CreateLoadSource(ImmutableArray<string> hostGrantedPermissions)
        =>CreateLoadSource(hostGrantedPermissions,(bytes,token)=>new ValueTask<IWasmPluginMachine>(Task.Run<IWasmPluginMachine>(()=>new WasmPluginMachine(bytes.Span),token)));

    /// <summary>The trusted Host may supply an authenticated remote execution factory.
    /// Payload memory is valid only until the awaited factory returns.</summary>
    public PluginLoadSource CreateLoadSource(ImmutableArray<string> hostGrantedPermissions,
        Func<ReadOnlyMemory<byte>,CancellationToken,ValueTask<IWasmPluginMachine>> createMachine)
    {
        ArgumentNullException.ThrowIfNull(createMachine);
        var payloads = _payloads ?? throw new ObjectDisposedException(nameof(VerifiedPluginPackage));
        WasmPluginEntry? entry = null; int consumed = 0;
        return new(Manifest, hostGrantedPermissions, async token =>
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref consumed, 1) != 0) throw new InvalidOperationException("A verified load source may be consumed only once.");
            try
            {
                if (Manifest.WebAssemblyEntryPath is { } wasm) entry = new(Manifest,await createMachine(payloads[wasm],token).ConfigureAwait(false));
                return Manifest.DeclarativeEntryPath is { } content
                    ? new DeclarativePluginEntry(Manifest, DeclarativeContentPack.Read(AbiValueBinary.Decode(payloads[content])), entry)
                    : entry ?? throw new InvalidDataException(AbiErrors.ENTRY_INVALID);
            }
            finally { Dispose(); }
        }, async () => { if (entry is not null) await entry.DisposeAsync().ConfigureAwait(false); Dispose(); });
    }
    private sealed class SensitiveBuffer(byte[] bytes) : IDisposable
    {
        internal byte[] Bytes { get; } = bytes;
        public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
    }
    private static bool SafePath(string path) => !string.IsNullOrEmpty(path) && path.Length <= 240 && !path.Contains('\\') && !path.Contains(':') && !path.StartsWith('/') && !path.Any(char.IsControl) && path.Split('/').All(p => p.Length > 0 && p is not "." and not "..");
    public void Dispose()
    {
        var payloads = Interlocked.Exchange(ref _payloads, null);
        if (payloads is not null) foreach (var bytes in payloads.Values) CryptographicOperations.ZeroMemory(bytes);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginManifest))]
public partial class PackageJsonContext : JsonSerializerContext;
