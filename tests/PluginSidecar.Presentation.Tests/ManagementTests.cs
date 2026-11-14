using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Presentation;
using PluginSidecar.WasmRuntime;

internal static partial class PresentationTests
{
    private static async Task ManagementChecks(bool retained = false)
    {
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Guest fixture required.");
        string root = Path.Combine(Path.GetTempPath(), "sidecar-manager-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PluginApplication(root, (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)), [],
                new Allow(), (_, _) => { }, (_, _) => { }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            var policy = new PluginManagementPolicy(true, false, true, false, "Ready <literal> {data private}");
            var effects = new ManagementEffects(app);
            TestPageHost host = retained ? new RetainedPageHost() : new TestPageHost();
            host.LoseAcknowledgement = true;
            await using var publisher = new PluginPagePublisher(app, host, new(() => policy, effects));
            byte[] Form(IPluginHostView view, string label) => SidecarUiForms.Encode(Nodes(Compile(view).Root.Root)
                .Single(node => node.Content == label && node.Command is not null).Argument!, new Dictionary<string, string>());
            IPluginHostView Manager() => host.Documents.Single(view => view.Document.EntryPoint == "manager.nxml");
            try { await publisher.SynchronizeAsync(); throw new InvalidOperationException("Lost acknowledgement not simulated."); }
            catch (IOException) { }
            byte[] oldImport = Form(Manager(), "加载开发包");
            await RejectedAsync(() => publisher.InvokeManagementAsync(oldImport).AsTask());
            await publisher.SynchronizeAsync();
            Check(host.Documents.Count == 2 && host.Views.Count == 0);
            Check(host.Documents.Any(view => view.Document.Target == "#NavigationSettings" && view.Document.Operation == SidecarNxmlOperation.Before));
            Check(Nodes(Compile(Manager()).Root.Root).Any(node => node.Content == policy.Status));
            await RejectedAsync(() => publisher.InvokeManagementAsync(oldImport).AsTask());
            await publisher.OpenManagerAsync(); Check(host.Visible == 0);
            await publisher.ReportVisibilityAsync();
            byte[] import = Form(Manager(), "加载开发包");
            await RejectedAsync(() => publisher.InvokeActionAsync(import).AsTask());
            var token = SidecarUiForms.Decode(import).Argument;
            await RejectedAsync(() => publisher.InvokeManagementAsync(SidecarUiForms.Encode(token, new Dictionary<string, string> { ["forged"] = "yes" })).AsTask());
            foreach (var denied in new[] { policy with { DeveloperMode = false }, policy with { SafeMode = true }, policy with { EngineReady = false }, policy with { Busy = true } })
            {
                policy = denied;
                await RejectedAsync(() => publisher.InvokeManagementAsync(import).AsTask());
                Check(effects.Imports == 0);
            }
            policy = new(true, false, true, false, "Ready");
            await publisher.InvokeManagementAsync(import); Check(effects.Imports == 1);
            using var package = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "advanced.zip")),
                "example.advanced", "1.0.0");
            var load = await app.LoadAsync(package, package.Manifest.RequestedPermissions, default);
            Check(load.State == LoadState.Active);
            await publisher.SynchronizeAsync();
            Check(host.Documents.Count == 4 && host.Views.Count == 2);
            await RejectedAsync(() => publisher.InvokeManagementAsync(import).AsTask());
            var page = app.Snapshot().Pages.Single();
            byte[] open = Form(Manager(), page.Title);
            await publisher.InvokeManagementAsync(open); Check(host.Visible == 2);
            await publisher.ReportVisibilityAsync();
            if (retained)
            {
                var watch = Abi100Catalog.Operations.Single(value => value.Group == "UI" && value.Name == "WatchViewLifecycle");
                var subscribed = await app.Runtime.InvokeAsync(load.Identity, watch.ContractId, watch.MethodId,
                    UIViewWatch.Write(new(new(page.Page.Id.Value), load.Identity.LoadInstanceId, null)),
                    new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default));
                Check(subscribed.Success, subscribed.Error?.Code);
                var subscription = SubscriptionRef.Read(subscribed.Value);
                Check(TextResult.Read(app.Runtime.Services.ReadSubscription(load.Identity, subscription).Items.Single()).Text == "open");
                string ordinaryTarget = host.Views.Single(value => !value.IsWorkspace).Document.Target;
                string previousWorkspace = host.Views.Single(value => value.IsWorkspace).Document.Target;
                using var otherPackage = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "desktop.zip")),
                    "example.desktop", "1.0.0");
                var otherLoad = await app.LoadAsync(otherPackage, otherPackage.Manifest.RequestedPermissions, default);
                Check(otherLoad.State == LoadState.Active);
                await publisher.SynchronizeAsync();
                Check(host.Views.Single(value => !value.IsWorkspace && value.Owner == load.Identity).Document.Target == ordinaryTarget);
                Check(host.Views.Single(value => value.IsWorkspace).Document.Target != previousWorkspace);
                Check(host.Views.Single(value => value.IsWorkspace).Document.Target.StartsWith(Manager().Document.Target + "/#", StringComparison.Ordinal));
                Check(app.Runtime.Services.ReadSubscription(load.Identity, subscription).Items.IsEmpty);
                await app.StopAsync(otherLoad.Identity.PluginId); await publisher.SynchronizeAsync();
                Check(app.Runtime.Services.ReadSubscription(load.Identity, subscription).Items.IsEmpty);
                app.Runtime.Services.ReleaseSubscription(load.Identity, subscription);
            }
            await RejectedAsync(() => publisher.SetOpenAsync(load.Identity, PluginApplication.WorkspaceView, true).AsTask());
            var workspace = host.Views.Single(view => view.IsWorkspace);
            Check(workspace.Document.Target.StartsWith(Manager().Document.Target + "/#", StringComparison.Ordinal));
            Check(Nodes(Compile(workspace).Root.Root).Any(node => node.Content == "Advanced plugin contribution"));
            byte[] command = Form(workspace, "Advanced command");
            await RejectedAsync(() => publisher.InvokeManagementAsync(command).AsTask());
            var operation = await publisher.InvokeActionAsync(command);
            var read = Abi100Catalog.Operations.Single(o => o.Group == "Tasks" && o.Name == "Read");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var result = await app.Runtime.InvokeAsync(load.Identity, read.ContractId, read.MethodId, EntityRequest.Write(new(operation.Id)),
                    new(Guid.NewGuid(), TimeSpan.FromSeconds(5), deadline.Token));
                Check(result.Success, result.Error?.Code);
                var record = OperationRecord.Read(result.Value);
                if (record.State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled or OperationState.OutcomeUnknown)
                { Check(record.State == OperationState.Completed, record.Failure?.Code); Check(TextResult.Read(record.Result).Text == "Command completed"); break; }
                await Task.Delay(5, deadline.Token);
            }
            foreach (string registry in new[] { "nexa:ui/shortcuts", "nexa:ui/commands" })
            {
                var entry = app.Runtime.Definitions.Entries.Single(e => e.Owner == load.Identity && e.Definition.Registry.Value == registry);
                app.Runtime.Definitions.Remove(new(load.Identity, Guid.Empty, "Active", new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default)),
                    new(entry.Definition.Registry, entry.Definition.Id, entry.Revision));
            }
            await RejectedAsync(() => publisher.InvokeActionAsync(command).AsTask());
            await publisher.SynchronizeAsync();
            await RejectedAsync(() => publisher.InvokeActionAsync(command).AsTask());
            byte[] disable = Form(Manager(), "停用");
            await publisher.InvokeManagementAsync(disable);
            Check(effects.Disabled == load.Identity && !app.Runtime.Active.Contains(load.Identity));
            await RejectedAsync(() => publisher.InvokeManagementAsync(disable).AsTask());
            await RejectedAsync(() => publisher.InvokeManagementAsync(open).AsTask());
            await publisher.SynchronizeAsync(); Check(host.Documents.Count == 2);
            effects.Block = true;
            import = Form(Manager(), "加载开发包");
            var pending = publisher.InvokeManagementAsync(import).AsTask();
            await effects.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RejectedAsync(() => publisher.InvokeManagementAsync(import).AsTask());
            await publisher.SynchronizeAsync();
            Check(!Nodes(Compile(Manager()).Root.Root).Single(node => node.Content == "加载开发包").IsVisible);
            await publisher.DisposeAsync();
            try { await pending; throw new InvalidOperationException("Management effect did not cancel."); }
            catch (OperationCanceledException) { }
            Check(effects.Imports == 2);
            Console.WriteLine("PASS: Sidecar manager/navigation, current-policy checks, independent guest commands, retirement, uncertain publication and cancellation.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ManagementEffects(PluginApplication application) : IPluginManagementActions
    {
        internal int Imports;
        internal bool Block;
        internal PluginIdentity? Disabled;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask ImportDeveloperPackageAsync(CancellationToken token)
        {
            Imports++;
            if (Block) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        }
        public async ValueTask DisableAsync(PluginIdentity exactLoad, CancellationToken token)
        {
            Check(application.Runtime.Active.Contains(exactLoad)); Disabled = exactLoad;
            var retired = await application.StopLoadAsync(exactLoad, token);
            Check(retired.Contains(exactLoad));
        }
    }
}
