using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Protection;
using PluginSidecar.WasmRuntime;

internal static class ApprovedInstallationTests
{
    internal static async Task Run(byte[] bytes)
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-approved-install-" + Guid.NewGuid().ToString("N"));
        using var secrets = new KeyProtectionTests.FakeSecrets();
        using var manifest = VerifiedPluginPackage.Parse(bytes, "example.desktop", "1.0.0");
        string id = manifest.Manifest.PluginId, version = manifest.Manifest.PluginVersion;
        var grants = manifest.Manifest.RequestedPermissions;
        using var catalog = new PluginOfflineCatalog(Path.Combine(root, "installed"), new PlatformPluginKeyProtector(secrets));
        using var competing = new PluginOfflineCatalog(Path.Combine(root, "installed"), new PlatformPluginKeyProtector(secrets));
        Guid session = Guid.NewGuid();
        var ready = new PluginInstallationPolicy(session, true, true, false);
        var policy = ready;
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<IWasmPluginMachine>> machine = Fail;
        try
        {
            await using var app = new PluginApplication(Path.Combine(root, "storage"), (wasm, token) => machine(wasm, token),
                [], new Deny(), (_, _) => { }, (_, _) => { }, _ => { });
            var installer = new PluginApprovedInstaller(app, catalog, () => policy);
            Task<PluginLoadReport> Install(bool remember = true, CancellationToken token = default)
                => installer.InstallAsync(bytes, id, version, grants, remember, session, token);

            Check((await Install()).State != LoadState.Active);
            Check(catalog.Records().IsEmpty && app.Runtime.Active.IsEmpty);
            await catalog.SaveAsync(bytes, id, version, [], default);
            string original = (await catalog.ReadRevisionAsync(id, default))!;
            Check((await Install()).State != LoadState.Active);
            using (var restored = await catalog.OpenInstalledAsync(id, default))
                Check(restored.Grants.IsEmpty && restored.Package.AsSpan().SequenceEqual(bytes) && restored.Revision != original);

            string? newer = null;
            machine = async (_, _) =>
            {
                await competing.SaveAsync(bytes, id, version, [], default);
                newer = await competing.ReadRevisionAsync(id, default);
                throw new IOException("Simulated activation failure after a competing installation.");
            };
            Check((await Install()).State != LoadState.Active);
            Check(await catalog.ReadRevisionAsync(id, default) == newer);

            machine = (wasm, _) =>
            {
                policy = ready with { DeveloperMode = false };
                return ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span));
            };
            await Reject<InvalidOperationException>(() => Install());
            Check(app.Runtime.Active.IsEmpty && app.Snapshot().Pages.IsEmpty);
            using (var restored = await catalog.OpenInstalledAsync(id, default)) Check(restored.Grants.IsEmpty);
            policy = ready;

            using (var cancel = new CancellationTokenSource())
            {
                machine = (_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Fail(default, token); };
                await Reject<OperationCanceledException>(() => Install(token: cancel.Token));
                using var restored = await catalog.OpenInstalledAsync(id, default);
                Check(restored.Grants.IsEmpty && app.Runtime.Active.IsEmpty);
            }

            int created = 0;
            machine = (wasm, _) =>
            {
                created++;
                return ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span));
            };
            original = (await catalog.ReadRevisionAsync(id, default))!;
            foreach (var denied in new[] { ready with { SafeMode = true }, ready with { DeveloperMode = false },
                ready with { Ready = false }, ready with { Session = Guid.NewGuid() }, ready with { Session = Guid.Empty } })
            {
                policy = denied;
                await Reject<InvalidOperationException>(() => Install());
            }
            policy = ready;
            await Reject<IOException>(() => installer.InstallAsync(bytes, id, version, ["undeclared.permission"], false, session, default));
            Check(created == 0 && await catalog.ReadRevisionAsync(id, default) == original);

            var transient = await Install(remember: false);
            Check(transient.State == LoadState.Active && await catalog.ReadRevisionAsync(id, default) == original);
            await Reject<IOException>(() => Install());
            Check(created == 1 && app.Runtime.Active.Contains(transient.Identity));
            Check(await catalog.ReadRevisionAsync(id, default) == original);
            await app.StopLoadAsync(transient.Identity);

            // Bytes are captured before waiting behind another attempt, so caller mutation cannot change approval.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int attempts = 0;
            machine = async (wasm, token) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    throw new IOException("First queued attempt fails.");
                }
                return new WasmPluginMachine(wasm.Span);
            };
            Task<PluginLoadReport> first = Install();
            Task<PluginLoadReport>? queued = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                byte[] supplied = bytes.ToArray();
                queued = installer.InstallAsync(supplied, id, version, grants, true, session, default);
                Check(!queued.IsCompleted);
                Array.Clear(supplied);
            }
            finally { release.TrySetResult(); await first; }
            Check((await first).State != LoadState.Active);
            var accepted = await queued!;
            Check(accepted.State == LoadState.Active && attempts == 2);
            using (var installed = await catalog.OpenInstalledAsync(id, default))
                Check(installed.Grants.SequenceEqual(grants) && installed.Package.AsSpan().SequenceEqual(bytes));
            await app.StopLoadAsync(accepted.Identity);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: approved installation rollback, competing-record preservation, post-activation policy retirement, cancellation, session-only installs and queued byte snapshots.");
    }

    private static ValueTask<IWasmPluginMachine> Fail(ReadOnlyMemory<byte> _, CancellationToken token)
        => ValueTask.FromException<IWasmPluginMachine>(new IOException("Simulated machine creation failure."));
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Check(bool value)
    { if (!value) throw new InvalidOperationException("Approved installation assertion failed."); }
    private sealed class Deny : IActionConfirmation
    {
        public ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request, CancellationToken cancellation) => ValueTask.FromResult(false);
    }
}
