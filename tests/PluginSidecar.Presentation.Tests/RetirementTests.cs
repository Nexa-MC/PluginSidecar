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
    private static async Task ExactLoadRetirementChecks()
    {
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Guest fixture required.");
        string root = Path.Combine(Path.GetTempPath(), "sidecar-retirement-" + Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            byte[] desktopBytes = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "desktop.zip"));
            using var package = VerifiedPluginPackage.Parse(desktopBytes, "example.desktop", "1.0.0");
            int machines = 0, retiredResources = 0;
            async ValueTask<IWasmPluginMachine> Machine(ReadOnlyMemory<byte> wasm, CancellationToken token)
            {
                if (Interlocked.Increment(ref machines) == 2)
                { entered.TrySetResult(); await release.Task.WaitAsync(token); }
                return new WasmPluginMachine(wasm.Span);
            }
            await using var app = new PluginApplication(root, Machine, [], new Allow(), (_, _) => { }, (_, _) => { },
                PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            app.ResourcesRetired += _ => retiredResources++;
            var first = await app.LoadAsync(package, package.Manifest.RequestedPermissions, default);
            Check(first.State == LoadState.Active);
            Check((await app.StopLoadAsync(first.Identity)).SequenceEqual([first.Identity]));
            using var reloadPackage = VerifiedPluginPackage.Parse(desktopBytes, "example.desktop", "1.0.0");
            var loading = app.LoadAsync(reloadPackage, reloadPackage.Manifest.RequestedPermissions, default);
            Task<System.Collections.Immutable.ImmutableArray<PluginIdentity>> staleStop;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                staleStop = app.StopLoadAsync(first.Identity);
            }
            finally { release.TrySetResult(); await loading; }
            var current = await loading;
            Check(current.State == LoadState.Active && current.Identity != first.Identity);
            int resourcesBeforeStaleStop = retiredResources;
            await RejectedAsync(() => staleStop);
            Check(retiredResources == resourcesBeforeStaleStop && app.Runtime.Active.Contains(current.Identity));
            Check(app.Snapshot().Pages.Single().Owner == current.Identity);
            // The queued stale request did not retire assets, registrations or the new presentation.
            var host = new TestPageHost();
            await using var publisher = new PluginPagePublisher(app, host);
            await publisher.SynchronizeAsync();
            await publisher.SetOpenAsync(current.Identity, app.Snapshot().Pages.Single().Page.Id, true);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await app.StopLoadAsync(current.Identity, cancelled.Token); throw new InvalidOperationException("Cancelled stop accepted."); }
            catch (OperationCanceledException) { }
            Check(app.Runtime.Active.Contains(current.Identity));
            byte[] advancedBytes = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "advanced.zip"));
            using var advanced = VerifiedPluginPackage.Parse(advancedBytes, "example.advanced", "1.0.0");
            var manifest = advanced.Manifest with { RequiredDependencies = [new(current.Identity.PluginId, new("1.0.0", true, "2.0.0", false))] };
            using var dependentBytes = new MemoryStream();
            using (var source = new ZipArchive(new MemoryStream(advancedBytes), ZipArchiveMode.Read))
            using (var destination = new ZipArchive(dependentBytes, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var entry in source.Entries)
                {
                    using var output = destination.CreateEntry(entry.FullName).Open();
                    if (entry.FullName == "plugin.json") output.Write(JsonSerializer.SerializeToUtf8Bytes(manifest, PackageJsonContext.Default.PluginManifest));
                    else { using var input = entry.Open(); input.CopyTo(output); }
                }
            using var dependentPackage = VerifiedPluginPackage.Parse(dependentBytes.ToArray(), manifest.PluginId, manifest.PluginVersion);
            var dependent = await app.LoadAsync(dependentPackage, manifest.RequestedPermissions, default);
            Check(dependent.State == LoadState.Active, dependent.Error?.Code);
            var retired = await app.StopLoadAsync(current.Identity);
            Check(retired.Length == 2 && retired.Contains(current.Identity) && retired.Contains(dependent.Identity));
            Check(app.Runtime.Active.IsEmpty && app.Snapshot().Pages.IsEmpty);
            await RejectedAsync(() => app.StopLoadAsync(current.Identity));
            Console.WriteLine("PASS: exact-load stop fences stale requests behind reload, preserves current assets, rejects cancellation and reports dependent retirement.");
        }
        finally { release.TrySetResult(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
