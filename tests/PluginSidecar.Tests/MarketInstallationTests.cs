using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.MarketDelivery;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Protection;
using PluginSidecar.WasmRuntime;
using System.Security.Cryptography;

internal static class MarketInstallationTests
{
    internal static async Task Run(byte[] bytes)
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-market-install-" + Guid.NewGuid().ToString("N"));
        using var publisher = RSA.Create(2048); using var device = RSA.Create(3072);
        using var secrets = new KeyProtectionTests.FakeSecrets(); using var protector = new PlatformPluginKeyProtector(secrets);
        var clock = new Clock();
        using var package = VerifiedPluginPackage.Parse(bytes, "example.desktop", "1.0.0");
        var manifest = package.Manifest;
        var entitlement = new PluginEntitlement(manifest.PluginId, manifest.PluginVersion, "account", clock.Now.AddMinutes(-1), null);
        var delivery = MarketDeliveryIssuer.Issue(bytes, entitlement, device, publisher);
        using var catalog = new PluginMarketCatalog(Path.Combine(root, "installed"), protector, new(publisher, device, clock));
        var ready = new PluginInstallationPolicy(Guid.NewGuid(), false, true, false);
        var policy = ready;
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<IWasmPluginMachine>> machine = Fail;
        try
        {
            await using var app = new PluginApplication(Path.Combine(root, "storage"), (wasm, token) => machine(wasm, token),
                [], new Deny(), (_, _) => { }, (_, _) => { }, _ => { });
            var installer = new PluginMarketInstaller(app, catalog, () => policy);
            Task<PluginLoadReport> Install(bool remember = true, CancellationToken token = default)
                => installer.InstallAsync(delivery, manifest.PluginId, manifest.PluginVersion, entitlement.Subject,
                    manifest.RequestedPermissions, remember, ready.Session, token);
            Check((await Install()).State != LoadState.Active && catalog.Records().IsEmpty);

            // A failed update restores the original protected bytes even if the old entitlement has expired.
            var expiring = MarketDeliveryIssuer.Issue(bytes, entitlement with { Expires = clock.Now.AddSeconds(1) }, device, publisher);
            string previous = await catalog.SaveIfUnchangedAsync(expiring, manifest.PluginId, manifest.PluginVersion, entitlement.Subject, [], null, default);
            clock.Now += TimeSpan.FromSeconds(2);
            Check((await Install()).State != LoadState.Active);
            Check(await catalog.ReadRevisionAsync(manifest.PluginId, default) == previous);
            await Reject<UnauthorizedAccessException>(() => catalog.OpenInstalledAsync(manifest.PluginId, default));
            previous = await catalog.SaveIfUnchangedAsync(delivery, manifest.PluginId, manifest.PluginVersion, entitlement.Subject, [], previous, default);

            machine = (wasm, _) =>
            {
                policy = ready with { Ready = false };
                return ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span));
            };
            await Reject<InvalidOperationException>(() => Install());
            Check(app.Runtime.Active.IsEmpty && app.Snapshot().Pages.IsEmpty);
            Check(await catalog.ReadRevisionAsync(manifest.PluginId, default) == previous);
            policy = ready;

            string? newer = null;
            machine = async (_, _) =>
            {
                string current = (await catalog.ReadRevisionAsync(manifest.PluginId, default))!;
                newer = await catalog.SaveIfUnchangedAsync(delivery, manifest.PluginId, manifest.PluginVersion, entitlement.Subject, [], current, default);
                throw new IOException("Activation failed after a newer installation.");
            };
            Check((await Install()).State != LoadState.Active);
            Check(await catalog.ReadRevisionAsync(manifest.PluginId, default) == newer);

            using (var stop = new CancellationTokenSource())
            {
                machine = (_, token) => { stop.Cancel(); token.ThrowIfCancellationRequested(); return Fail(default, token); };
                await Reject<OperationCanceledException>(() => Install(token: stop.Token));
                Check(app.Runtime.Active.IsEmpty && await catalog.ReadRevisionAsync(manifest.PluginId, default) == newer);
            }
            int created = 0;
            machine = (wasm, _) => { created++; return ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)); };
            foreach (var denied in new[] { ready with { SafeMode = true }, ready with { Ready = false },
                ready with { Session = Guid.NewGuid() }, ready with { Session = Guid.Empty } })
            { policy = denied; await Reject<InvalidOperationException>(() => Install()); }
            policy = ready;
            await Reject<IOException>(() => installer.InstallAsync(delivery, manifest.PluginId, manifest.PluginVersion,
                entitlement.Subject, ["undeclared.permission"], false, ready.Session, default));
            Check(created == 0);
            var loaded = await Install();
            Check(!policy.DeveloperMode && loaded.State == LoadState.Active && created == 1);
            Check(app.Snapshot().Pages.Any(page => page.Owner == loaded.Identity));
            var remembered = await catalog.OpenInstalledAsync(manifest.PluginId, default);
            await app.StopLoadAsync(loaded.Identity);
            var restored = await installer.InstallAsync(remembered.Delivery, remembered.Id, remembered.Version,
                remembered.Subject, remembered.Grants, false, ready.Session, default);
            Check(restored.State == LoadState.Active && !policy.DeveloperMode);
            Check(await catalog.ReadRevisionAsync(manifest.PluginId, default) == remembered.Revision);
            await app.StopLoadAsync(restored.Identity);
            Console.WriteLine("PASS: signed Market packages activate outside developer mode, restore offline, reject unsafe/stale policy and extra grants, and roll back failures without overwriting newer or expired records.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static ValueTask<IWasmPluginMachine> Fail(ReadOnlyMemory<byte> _, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromException<IWasmPluginMachine>(new IOException("Simulated load failure.")); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Market install assertion failed."); }
    private sealed class Clock : TimeProvider
    { internal DateTimeOffset Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Deny : IActionConfirmation
    { public ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request, CancellationToken cancellation) => ValueTask.FromResult(false); }
}
