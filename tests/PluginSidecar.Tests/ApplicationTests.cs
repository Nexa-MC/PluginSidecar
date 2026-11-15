using System.Security.Cryptography;
using Nexa.Plugin.Hosting;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Protection;
using PluginSidecar.WasmRuntime;

internal static class ApplicationTests
{
    internal static async Task Run()
    {
        string? sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE");
        if (sample is null) { Console.WriteLine("SKIP: Sidecar application fixture requires the compiled guest samples."); return; }
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "desktop.zip");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        await CatalogRevisionTests.Run(bytes);
        await MarketCatalogTests.Run(bytes);
        await MarketInstallationTests.Run(bytes);
        await ApprovedInstallationTests.Run(bytes);
        string root = Path.Combine(Path.GetTempPath(), "nexa-sidecar-application-" + Guid.NewGuid().ToString("N"));
        using var secrets = new KeyProtectionTests.FakeSecrets();
        try
        {
            using var package = VerifiedPluginPackage.Parse(bytes, "example.desktop", "1.0.0");
            using var catalog = new PluginOfflineCatalog(Path.Combine(root, "installed"), new PlatformPluginKeyProtector(secrets));
            await catalog.SaveAsync(bytes, package.Manifest.PluginId, package.Manifest.PluginVersion, package.Manifest.RequestedPermissions, default);
            using var restarted = new PluginOfflineCatalog(Path.Combine(root, "installed"), new PlatformPluginKeyProtector(secrets));
            using var restored = await restarted.OpenAsync(catalog.Records().Single(), default);
            Check(restored.Package.AsSpan().SequenceEqual(bytes));
            Check(restored.Grants.SequenceEqual(package.Manifest.RequestedPermissions));
            await using (var app = new PluginApplication(Path.Combine(root, "storage"),
                (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)), [], new Deny(),
                (_, _) => { }, (_, _) => { }, _ => { }))
            {
                var report = await app.LoadAsync(package, restored.Grants, default);
                Check(report.State == Nexa.Plugin.Bootstrap.V1.LoadState.Active);
                Check(app.Snapshot().Pages.Single().Owner.PluginId == package.Manifest.PluginId);
                await app.StopAsync(package.Manifest.PluginId);
                Check(app.Snapshot().Pages.IsEmpty && app.Runtime.Active.IsEmpty);
            }
            try { await catalog.SaveAsync(bytes, package.Manifest.PluginId, package.Manifest.PluginVersion, ["undeclared.permission"], default); throw new InvalidOperationException("Undeclared grant accepted."); }
            catch (IOException) { }
            string record = catalog.Records().Single(); byte[] cipher = await File.ReadAllBytesAsync(record);
            cipher[^1] ^= 1; await File.WriteAllBytesAsync(record, cipher);
            try { using var invalid = await catalog.OpenAsync(record, default); throw new InvalidOperationException("Corrupt catalog accepted."); }
            catch (CryptographicException) { }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: Sidecar-owned application loads and retires real UI guest; encrypted offline grants restore and reject tampering without launcher assemblies.");
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Sidecar application assertion failed."); }
    private sealed class Deny : IActionConfirmation
    {
        public ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request, CancellationToken cancellation) => ValueTask.FromResult(false);
    }
}
