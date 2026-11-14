using System.IO.Compression;
using System.Text.Json;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Presentation;
using PluginSidecar.WasmRuntime;

internal static partial class PresentationTests
{
    private static async Task ManagementControllerChecks()
    {
        string fixture = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Guest fixture required.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(fixture))!;
        string root = Path.Combine(Path.GetTempPath(), "nexa-management-owner-" + Guid.NewGuid().ToString("N"));
        string installed = Path.Combine(root, "installed");
        byte[] desktop = await File.ReadAllBytesAsync(Path.Combine(directory, "desktop.zip"));
        byte[] advanced = await File.ReadAllBytesAsync(Path.Combine(directory, "advanced.zip"));
        using var catalog = new PluginOfflineCatalog(installed, new TestCatalogKeys());
        using var competing = new PluginOfflineCatalog(installed, new TestCatalogKeys());
        var ready = new PluginInstallationPolicy(Guid.NewGuid(), true, true, false);
        var policy = ready;
        string? picked = Path.Combine(directory, "desktop.zip");
        PluginInstallDecision decision = PluginInstallDecision.Declined;
        Func<CancellationToken, ValueTask<string?>> picker = _ => ValueTask.FromResult<string?>(picked);
        Func<PluginInstallConsent, CancellationToken, ValueTask<PluginInstallDecision>> confirmation = (consent, _) =>
        {
            Check(consent.Id == "example.desktop" && consent.Version == "1.0.0" && !consent.Permissions.IsEmpty);
            return ValueTask.FromResult(decision);
        };
        int machines = 0;
        try
        {
            await using var app = new PluginApplication(Path.Combine(root, "storage"), (wasm, _) =>
            {
                machines++;
                return ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span));
            }, [], new Allow(), (_, _) => { }, (_, _) => { }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            await using var controller = new PluginManagementController(app, catalog, () => policy,
                token => picker(token), (consent, token) => confirmation(consent, token));
            await controller.ImportDeveloperPackageAsync(default);
            Check(app.Runtime.Active.IsEmpty && catalog.Records().IsEmpty && machines == 0);
            decision = PluginInstallDecision.SessionOnly;
            await controller.ImportDeveloperPackageAsync(default);
            var transient = app.Runtime.Active.Single();
            Check(catalog.Records().IsEmpty && !controller.IsBusy);
            await controller.DisableAsync(transient, default);
            await Expect<AbiHostException>(() => controller.DisableAsync(transient, default).AsTask());

            decision = PluginInstallDecision.Remember;
            await controller.ImportDeveloperPackageAsync(default);
            var original = app.Runtime.Active.Single();
            string? revision = await catalog.ReadRevisionAsync(original.PluginId, default);
            await Expect<IOException>(() => controller.ImportDeveloperPackageAsync(default).AsTask());
            Check(app.Runtime.Active.Contains(original) && await catalog.ReadRevisionAsync(original.PluginId, default) == revision);
            policy = ready with { DeveloperMode = false };
            await controller.SynchronizePolicyAsync();
            Check(app.Runtime.Active.IsEmpty && catalog.Records().Length == 1);
            policy = ready;
            Check((await controller.RestoreOfflineAsync()).IsEmpty);
            var restored = app.Runtime.Active.Single();
            Check(restored != original);
            int once = machines;
            Check((await controller.RestoreOfflineAsync()).IsEmpty && machines == once);
            ready = ready with { Session = Guid.NewGuid() }; policy = ready;
            await controller.SynchronizePolicyAsync();
            Check(app.Runtime.Active.IsEmpty);
            Check((await controller.RestoreOfflineAsync()).IsEmpty);
            Check(app.Runtime.Active.Single() != restored);

            // Revocation cancels a pending native interaction, then retires existing loads without forgetting them.
            var consentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var neverApprove = new TaskCompletionSource<PluginInstallDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            confirmation = async (_, token) => { consentEntered.TrySetResult(); return await neverApprove.Task.WaitAsync(token); };
            Task pending = controller.ImportDeveloperPackageAsync(default).AsTask();
            await consentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(controller.IsBusy);
            policy = ready with { Ready = false };
            await controller.SynchronizePolicyAsync();
            await Expect<OperationCanceledException>(() => pending);
            Check(!controller.IsBusy && app.Runtime.Active.IsEmpty && catalog.Records().Length == 1);
            policy = ready;
            Check((await controller.RestoreOfflineAsync()).IsEmpty);
            await Expect<AbiHostException>(() => controller.DisableAsync(original, default).AsTask());
            Check(catalog.Records().Length == 1);
            await controller.DisableAsync(app.Runtime.Active.Single(), default);
            Check(catalog.Records().IsEmpty);

            byte[] dependent = WithDependency(advanced, "example.desktop");
            await Save(desktop); await Save(dependent);
            await NewSession();
            Check((await controller.RestoreOfflineAsync()).IsEmpty && app.Runtime.Active.Length == 2);
            var provider = app.Runtime.Active.Single(owner => owner.PluginId == "example.desktop");
            await controller.DisableAsync(provider, default);
            Check(app.Runtime.Active.IsEmpty && catalog.Records().IsEmpty);

            // A new record appearing during retirement must survive the old button's cleanup.
            await Save(desktop); await NewSession(); await controller.RestoreOfflineAsync();
            bool replaceDuringRetirement = true;
            void ReplaceRecord(string id)
            {
                if (id != "example.desktop" || !replaceDuringRetirement) return;
                replaceDuringRetirement = false;
                // This test callback models an independent catalog writer; production retirement remains async.
                competing.SaveAsync(desktop, id, "1.0.0", [], default).GetAwaiter().GetResult();
            }
            app.ResourcesRetired += ReplaceRecord;
            try { await Expect<IOException>(() => controller.DisableAsync(app.Runtime.Active.Single(), default).AsTask()); }
            finally { app.ResourcesRetired -= ReplaceRecord; }
            Check(app.Runtime.Active.IsEmpty);
            using (var current = await catalog.OpenInstalledAsync("example.desktop", default)) Check(current.Grants.IsEmpty);

            await Save(WithDependency(desktop, "example.advanced")); await Save(dependent);
            await Save(await File.ReadAllBytesAsync(Path.Combine(directory, "replacement.zip")));
            await File.WriteAllBytesAsync(Path.Combine(installed, new string('0', 64) + ".npinstall"), [1, 2, 3]);
            await NewSession();
            var issues = await controller.RestoreOfflineAsync();
            Check(issues.Count(issue => issue.Code == "plugin.restore.dependencies") == 2);
            Check(issues.Count(issue => issue.Code == "plugin.restore.record-invalid") == 1);
            Check(app.Runtime.Active.Length == 1, string.Join(", ", issues.Select(issue => issue.Id + ":" + issue.Code)));
            Check(app.Runtime.Active.Single().PluginId == "example.replacement" && catalog.Records().Length == 4);

            var pickEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var neverPick = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            picker = async token => { pickEntered.TrySetResult(); return await neverPick.Task.WaitAsync(token); };
            pending = controller.ImportDeveloperPackageAsync(default).AsTask();
            await pickEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await controller.DisposeAsync();
            await Expect<OperationCanceledException>(() => pending);
            Check(app.Runtime.Active.IsEmpty && catalog.Records().Length == 4 && !controller.IsBusy);
            await Expect<ObjectDisposedException>(() => controller.RestoreOfflineAsync().AsTask());

            async Task NewSession()
            {
                ready = ready with { Session = Guid.NewGuid() }; policy = ready;
                await controller.SynchronizePolicyAsync();
            }
            async Task Save(byte[] packageBytes)
            {
                using var archive = new ZipArchive(new MemoryStream(packageBytes), ZipArchiveMode.Read);
                using var input = archive.GetEntry("plugin.json")!.Open();
                var manifest = JsonSerializer.Deserialize(input, PackageJsonContext.Default.PluginManifest)!;
                await catalog.SaveAsync(packageBytes, manifest.PluginId, manifest.PluginVersion, manifest.RequestedPermissions, default);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: sole management owner, consent, session policy cancellation, dependency restoration, exact disable, newer-record preservation and shutdown drain.");
    }

    private static byte[] WithDependency(byte[] package, string dependency)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        using (var destination = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in archive.Entries)
            {
                using var input = entry.Open(); using var target = destination.CreateEntry(entry.FullName).Open();
                if (entry.FullName != "plugin.json") { input.CopyTo(target); continue; }
                var manifest = JsonSerializer.Deserialize(input, PackageJsonContext.Default.PluginManifest)!;
                target.Write(JsonSerializer.SerializeToUtf8Bytes(manifest with
                { RequiredDependencies = [new(dependency, new("1.0.0", true, "2.0.0", false))] }, PackageJsonContext.Default.PluginManifest));
            }
        return output.ToArray();
    }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    // Key custody has separate OS-backed tests. This fixture exercises management against an ephemeral catalog.
    private sealed class TestCatalogKeys : IPluginKeyProtector
    {
        public ValueTask<byte[]> ProtectAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(bytes.ToArray()); }
        public ValueTask<byte[]> UnprotectAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(bytes.ToArray()); }
    }
}
