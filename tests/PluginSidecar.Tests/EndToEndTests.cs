using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.MarketDelivery;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.Packages;
using Wasmtime;

internal static class EndToEndTests
{
    internal static async Task Run()
    {
        var definition = new Definition(new("example.offline:entry"), new("nexa:test"), new(new("nexa:test"), 1, "schema"), DataCodec.WriteString("offline definition"), []);
        var request = DataCodec.Map(("kind", DataCodec.WriteString("invoke")), ("contract", DataCodec.WriteString("nexa.definitions/100")), ("method", DataCodec.WriteUInt64(5)),
            ("arguments", DefinitionBatch.Write(new([definition]))), ("idempotencyKey", DataCodec.WriteString("initialize")));
        byte[] requested = AbiValueBinary.Encode(request), completed = AbiValueBinary.Encode(DataCodec.Map(("kind", DataCodec.WriteString("complete")), ("value", DataCodec.Map(("success", DataCodec.WriteBoolean(true))))));
        string Escaped(byte[] bytes) => string.Concat(bytes.Select(b => "\\" + b.ToString("x2")));
        byte[] wasm = Module.ConvertText($$"""
        (module (memory (export "memory") 1 1)
         (global $step (mut i32) (i32.const 0))
         (data (i32.const 0) "{{Escaped(completed)}}")
         (data (i32.const 2048) "{{Escaped(requested)}}")
         (func (export "nexa_alloc") (param i32) (result i32) i32.const 8192)
         (func (export "nexa_resume") (param i32 i32) (result i64)
           global.get $step i32.const 1 i32.add global.set $step
           global.get $step i32.const 2 i32.eq
           if (result i64) i64.const {{(2048UL << 32) | (uint)requested.Length}}
           else i64.const {{completed.Length}} end))
        """);
        var manifest = new PluginManifest(1, "example.offline", "1.0.0", 1, new("", ""), new([], [], "nexa-wasm-1"), [100], [], [], [], ["nexa.definitions.write"],
            [new("plugin.wasm", wasm.Length, Convert.ToHexString(SHA256.HashData(wasm)))])
        { WebAssemblyEntryPath = "plugin.wasm", RequiredCapabilities = [new("nexa.definitions", 1)] };
        byte[] Bundle(PluginManifest value, string wasmPath = "plugin.wasm")
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                using (var file = zip.CreateEntry("plugin.json").Open()) JsonSerializer.Serialize(file, value, PackageJsonContext.Default.PluginManifest);
                using (var file = zip.CreateEntry(wasmPath).Open()) file.Write(wasm);
            }
            return stream.ToArray();
        }
        using var signer = RSA.Create(2048); using var device = RSA.Create(2048);
        string directory = Path.Combine(Path.GetTempPath(), "nexa-plugin-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            var verifier = new OfflinePluginVerifier(signer, device);
            var store = new OfflinePluginStore(directory, verifier);
            var delivery = MarketDeliveryIssuer.Issue(Bundle(manifest), new(manifest.PluginId, manifest.PluginVersion, "owner", DateTimeOffset.UtcNow.AddMinutes(-1), null), device, signer);
            await store.SaveAsync(delivery, manifest.PluginId, manifest.PluginVersion, "owner");
            // Simulated restart: only durable ciphertext/grant and independently retained keys are used.
            store = new OfflinePluginStore(directory, new OfflinePluginVerifier(signer, device));
            using var package = VerifiedPluginPackage.OpenOffline(store, manifest.PluginId, manifest.PluginVersion, "owner");
            await using var runtime = new PluginHostRuntime(Path.Combine(directory, "storage"),
                d => d.Registry == definition.Registry && d.Schema == definition.Schema && d.Body is AbiValue.Utf8String,
                (_, _) => false);
            var report = await runtime.LoadAsync([package.CreateLoadSource(["nexa.definitions.write"])]);
            if (report.Single().State != LoadState.Active) throw new InvalidOperationException("End-to-end load failed: " + report.Single().Error?.Code);
            var registered = runtime.Definitions.Entries.Single();
            Check(registered.Definition.Id == definition.Id && registered.Registration.State == RegistrationState.Active);
            await runtime.StopAsync(manifest.PluginId);
            Check(runtime.Definitions.Entries.IsEmpty && runtime.Active.IsEmpty);
            Reject(() => VerifiedPluginPackage.Parse(Bundle(manifest, "../plugin.wasm"), manifest.PluginId, manifest.PluginVersion));
            Reject(() => VerifiedPluginPackage.Parse(Bundle(manifest with { Target = manifest.Target with { OperatingSystems = ["unsupported-fixture"] } }), manifest.PluginId, manifest.PluginVersion));
            Reject(() => VerifiedPluginPackage.Parse(Bundle(manifest with { Payloads = [manifest.Payloads[0] with { Sha256 = new string('0', 64) }] }), manifest.PluginId, manifest.PluginVersion));
            Reject(() => VerifiedPluginPackage.OpenOffline(store, manifest.PluginId, manifest.PluginVersion, "other-owner"));
            var contentPack = new DeclarativeContentPack(new("example.offline:content"), Abi100Schemas.DeclarativeContentPack, [definition]);
            byte[] contentBytes = AbiValueBinary.Encode(DeclarativeContentPack.Write(contentPack));
            var staticManifest = manifest with
            {
                Target = new([], [], "nexa-content-1"), WebAssemblyEntryPath = null, DeclarativeEntryPath = "content.ncp",
                Payloads = [new("content.ncp", contentBytes.Length, Convert.ToHexString(SHA256.HashData(contentBytes)))]
            };
            byte[] ContentBundle(PluginManifest descriptor)
            {
                using var memory = new MemoryStream();
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    using (var file = zip.CreateEntry("plugin.json").Open()) JsonSerializer.Serialize(file, descriptor, PackageJsonContext.Default.PluginManifest);
                    using (var file = zip.CreateEntry("content.ncp").Open()) file.Write(contentBytes);
                }
                return memory.ToArray();
            }
            var staticDelivery = MarketDeliveryIssuer.Issue(ContentBundle(staticManifest), new(manifest.PluginId, manifest.PluginVersion, "owner", DateTimeOffset.UtcNow.AddMinutes(-1), null), device, signer);
            await store.SaveAsync(staticDelivery, manifest.PluginId, manifest.PluginVersion, "owner");
            using var staticPackage = VerifiedPluginPackage.OpenOffline(store, manifest.PluginId, manifest.PluginVersion, "owner");
            var staticReport = await runtime.LoadAsync([staticPackage.CreateLoadSource(["nexa.definitions.write"])]);
            Check(staticReport.Single().State == LoadState.Active && runtime.Definitions.Entries.Single().Definition.Id == definition.Id);
            await runtime.StopAsync(manifest.PluginId);
            Reject(() => VerifiedPluginPackage.Parse(ContentBundle(staticManifest with { RequiredCapabilities = [] }), manifest.PluginId, manifest.PluginVersion));
            Check(Abi100Schemas.Validate(Abi100Schemas.DeclarativeContentPack, DeclarativeContentPack.Write(contentPack)));
            Check(!Abi100Schemas.Validate(Abi100Schemas.DeclarativeContentPack with { Digest = "wrong" }, DeclarativeContentPack.Write(contentPack)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS: signed encrypted package -> offline restart -> strict manifest/digests -> real Wasm or declarative entry -> Host negotiation -> staged registration -> atomic activation -> stop cleanup; traversal/hash/owner/schema rejection.");
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("End-to-end assertion failed."); }
    private static void Reject(Func<VerifiedPluginPackage> open)
    {
        try { using var package = open(); }
        catch (Exception e) when (e is InvalidDataException or CryptographicException or UnauthorizedAccessException or FileNotFoundException) { return; }
        throw new InvalidOperationException("Invalid package was admitted.");
    }
}
