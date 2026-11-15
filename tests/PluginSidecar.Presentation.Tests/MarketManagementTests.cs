using System.Security.Cryptography;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.MarketDelivery;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Presentation;
using PluginSidecar.WasmRuntime;

internal static partial class PresentationTests
{
    private static async Task MarketManagementChecks()
    {
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Guest fixture required.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(sample))!;
        byte[] desktop = await File.ReadAllBytesAsync(Path.Combine(directory, "desktop.zip"));
        byte[] advanced = await File.ReadAllBytesAsync(Path.Combine(directory, "advanced.zip"));
        using var publisher = RSA.Create(2048); using var device = RSA.Create(3072);
        var grant = new PluginEntitlement("example.desktop", "1.0.0", "account", DateTimeOffset.UtcNow.AddMinutes(-1), null);
        var delivery = MarketDeliveryIssuer.Issue(desktop, grant, device, publisher);
        string root = Path.Combine(Path.GetTempPath(), "nexa-market-management-" + Guid.NewGuid().ToString("N"));
        var policy = new PluginInstallationPolicy(Guid.NewGuid(), false, true, false);
        var keys = new TestCatalogKeys();
        using var developer = new PluginOfflineCatalog(Path.Combine(root, "developer"), keys);
        using var market = new PluginMarketCatalog(Path.Combine(root, "market"), keys, new(publisher, device));
        var decision = PluginInstallDecision.Declined;
        int asks = 0;
        var sources = new List<PluginInstallSource>();
        Func<PluginInstallConsent, CancellationToken, ValueTask<PluginInstallDecision>> confirm = (consent, _) =>
        { asks++; sources.Add(consent.Source); return ValueTask.FromResult(decision); };
        try
        {
            await using var app = new PluginApplication(Path.Combine(root, "storage"), (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)),
                [], new Allow(), (_, _) => { }, (_, _) => { }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            await using var controller = new PluginManagementController(app, developer, () => policy,
                _ => ValueTask.FromResult<string?>(Path.Combine(directory, "advanced.zip")), (consent, token) => confirm(consent, token), market);
            Task<bool> Install() => controller.InstallMarketAsync(delivery, grant.PluginId, grant.Version, grant.Subject, default).AsTask();
            Check(!await Install() && app.Runtime.Active.IsEmpty && market.Records().IsEmpty);
            await Expect<InvalidOperationException>(() => controller.ImportDeveloperPackageAsync(default).AsTask());
            decision = PluginInstallDecision.Remember;
            Check(await Install());
            var owned = app.Runtime.Active.Single();
            Check(sources.All(source => source == PluginInstallSource.Market));
            Check(market.Records().Length == 1 && developer.Records().IsEmpty);
            policy = policy with { DeveloperMode = true };
            await controller.SynchronizePolicyAsync();
            Check(app.Runtime.Active.Contains(owned));
            await controller.ImportDeveloperPackageAsync(default);
            Check(sources[^1] == PluginInstallSource.Developer && app.Runtime.Active.Length == 2);
            policy = policy with { DeveloperMode = false };
            await controller.SynchronizePolicyAsync();
            Check(app.Runtime.Active.SequenceEqual(new[] { owned }));
            int before = asks;
            Check((await controller.RestoreOfflineAsync()).IsEmpty && asks == before);
            policy = policy with { Ready = false }; await controller.SynchronizePolicyAsync();
            Check(app.Runtime.Active.IsEmpty && market.Records().Length == 1 && developer.Records().Length == 1);
            policy = policy with { Ready = true, Session = Guid.NewGuid() };
            Check((await controller.RestoreOfflineAsync()).IsEmpty && asks == before);
            var offline = app.Runtime.Active.Single(); Check(offline.PluginId == owned.PluginId && offline != owned);
            await controller.DisableAsync(offline, default);
            Check(app.Runtime.Active.IsEmpty && market.Records().IsEmpty && developer.Records().Length == 1);

            // Ordinary mode must not repeatedly cancel or block a native Market confirmation.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pendingConsent = new TaskCompletionSource<PluginInstallDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            confirm = async (consent, token) =>
            { Check(consent.Source == PluginInstallSource.Market); entered.TrySetResult(); return await pendingConsent.Task.WaitAsync(token); };
            var pending = Install(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await controller.SynchronizePolicyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Check(!pending.IsCompleted && controller.IsBusy);
            policy = policy with { SafeMode = true };
            await controller.SynchronizePolicyAsync();
            await Expect<OperationCanceledException>(() => pending);
            Check(app.Runtime.Active.IsEmpty && market.Records().IsEmpty);
            policy = policy with { SafeMode = false };
            using var verified = VerifiedPluginPackage.Parse(desktop, grant.PluginId, grant.Version);
            await developer.SaveAsync(desktop, grant.PluginId, grant.Version, verified.Manifest.RequestedPermissions, default);
            await Expect<System.IO.IOException>(() => Install());
            // Concurrent installations must never make restoration choose an origin by enumeration order.
            await market.SaveIfUnchangedAsync(delivery, grant.PluginId, grant.Version, grant.Subject, verified.Manifest.RequestedPermissions, null, default);
            policy = policy with { Session = Guid.NewGuid() };
            await controller.SynchronizePolicyAsync();
            var issues = await controller.RestoreOfflineAsync();
            Check(issues.Any(issue => issue.Id == grant.PluginId && issue.Code == "plugin.install.origin-conflict"));
            Check(app.Runtime.Active.IsEmpty);
            Console.WriteLine("PASS: normal-mode Market consent/install/restore/disable, independent developer retirement, unchanged-policy pending consent, safe-mode cancellation and conflicting-origin rejection.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
