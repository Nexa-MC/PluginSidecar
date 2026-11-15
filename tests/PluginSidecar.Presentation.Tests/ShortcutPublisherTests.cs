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
    private static async Task ShortcutPublisherChecks()
    {
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Guest fixture required.");
        string root = Path.Combine(Path.GetTempPath(), "sidecar-shortcuts-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PluginApplication(root, (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)), [],
                new Allow(), (_, _) => { }, (_, _) => { }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            using var package = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(sample)!, "shortcuts.zip")), "example.advanced", "1.0.0");
            var load = await app.LoadAsync(package, package.Manifest.RequestedPermissions, default); Check(load.State == LoadState.Active);
            var page = app.Snapshot().Pages.Single();
            var host = new ShortcutPageHost();
            await using var publisher = new PluginPagePublisher(app, host,
                new(() => new(true, false, true, false, "Ready"), new ManagementEffects(app)), shortcuts: host);
            async Task<AbiValue> Ui(string name, AbiValue input)
            {
                var operation = Abi100Catalog.Operations.Single(value => value.Group == "UI" && value.Name == name);
                var result = await app.Runtime.InvokeAsync(load.Identity, operation.ContractId, operation.MethodId, input,
                    new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default) { IdempotencyKey = Guid.NewGuid().ToString("N") });
                Check(result.Success, result.Error?.Code); return result.Value;
            }
            void Remove(QualifiedId id)
            {
                var entry = app.Runtime.Definitions.Entries.Single(entry => entry.Owner == load.Identity && entry.Definition.Id == id);
                app.Runtime.Definitions.Remove(new(load.Identity, Guid.Empty, "Active", new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default)),
                    new(entry.Definition.Registry, entry.Definition.Id, entry.Revision));
            }
            static byte[] Form(PluginShortcutBinding binding) => SidecarUiForms.Encode(binding.Argument, new Dictionary<string, string>());
            async Task Invoke(PluginShortcutBinding binding)
            {
                var invoked = await publisher.InvokeActionAsync(Form(binding));
                var read = Abi100Catalog.Operations.Single(value => value.Group == "Tasks" && value.Name == "Read");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var result = await app.Runtime.InvokeAsync(load.Identity, read.ContractId, read.MethodId, EntityRequest.Write(new(invoked.Id)),
                        new(Guid.NewGuid(), TimeSpan.FromSeconds(5), deadline.Token));
                    Check(result.Success); var record = OperationRecord.Read(result.Value);
                    if (record.State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled or OperationState.OutcomeUnknown)
                    { Check(record.State == OperationState.Completed, record.Failure?.Code); Check(TextResult.Read(record.Result).Text == "Command completed"); break; }
                    await Task.Delay(1, deadline.Token);
                }
            }
            host.LoseReply = true;
            await Lost(() => publisher.SynchronizeAsync().AsTask()); Check(host.Bindings.Length == 2);
            var initial = host.Bindings.Single(binding => binding.Gesture == "ctrl+alt+p");
            var workspace = host.Bindings.Single(binding => binding.Gesture == "Ctrl+Alt+W");
            Check(initial.Argument.StartsWith("sc_", StringComparison.Ordinal));
            Check(host.Views.Documents[initial.Document] is PluginPreparedView { IsWorkspace: false, DialogReference: null });
            Check(host.Views.Documents[workspace.Document] is PluginPreparedView { IsWorkspace: true } workspaceView && workspaceView.Owner == load.Identity);
            long revision = host.Views.Revision;
            await RejectedAsync(() => publisher.InvokeActionAsync(Form(initial)).AsTask());
            await RejectedAsync(() => publisher.InvokeActionAsync(Form(workspace)).AsTask());
            await publisher.SynchronizeAsync(); Check(host.Views.Revision == revision);
            await Invoke(initial); await Invoke(workspace);
            // The guest command accepts Empty; page models and drafts are never inputs.
            await RejectedAsync(() => publisher.InvokeActionAsync(SidecarUiForms.Encode(initial.Argument, new Dictionary<string, string> { ["draft"] = "secret" })).AsTask());
            int sets = host.Sets;
            await publisher.SynchronizeAsync(); Check(host.Sets == sets && host.Views.Revision == revision);
            await Ui("UpdateModel", UIModelUpdate.Write(new(new(page.Page.Id.Value), page.Page.Schema, TextResult.Write(new("updated model")), null)));
            await publisher.SynchronizeAsync(); Check(host.Bindings.Single(binding => binding.Gesture == "ctrl+alt+p").Argument == initial.Argument);
            await Invoke(initial);
            // All three shortcuts registered at Initialize; runtime registration is intentionally denied.
            var workspaceId = new QualifiedId("example.advanced:workspace-shortcut");
            var winnerId = new QualifiedId("example.advanced:aaa-shortcut");
            Remove(winnerId);
            await RejectedAsync(() => publisher.InvokeActionAsync(Form(initial)).AsTask());
            await publisher.SynchronizeAsync(); var replacement = host.Bindings.Single(binding => binding.Gesture == "Ctrl+Alt+P");
            Check(replacement.Argument != initial.Argument); await Invoke(replacement);
            Remove(new("example.advanced:shortcut"));
            await RejectedAsync(() => publisher.InvokeActionAsync(Form(replacement)).AsTask());
            await publisher.SynchronizeAsync(); Check(host.Bindings.Length == 1); await Invoke(workspace);
            // A lost source/model acknowledgement seals all shortcut tokens and rotates recovery epochs.
            await Ui("UpdateModel", UIModelUpdate.Write(new(new(page.Page.Id.Value), page.Page.Schema, TextResult.Write(new("uncertain model")), null)));
            host.LoseModelReply = true;
            await Lost(() => publisher.SynchronizeAsync().AsTask());
            await RejectedAsync(() => publisher.InvokeActionAsync(Form(workspace)).AsTask());
            await publisher.SynchronizeAsync(); var recovered = host.Bindings.Single();
            Check(recovered.Argument != workspace.Argument); await Invoke(recovered);
            Remove(workspaceId); Remove(new("example.advanced:command"));
            await RejectedAsync(() => publisher.InvokeActionAsync(Form(recovered)).AsTask());
            // Stopping the owner cancels a pending native binding clear before disposal drains the gate.
            host.Block = true;
            var pending = publisher.SynchronizeAsync().AsTask(); await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.DisposeAsync();
            try { await pending; throw new InvalidOperationException("Shortcut publication did not cancel."); } catch (OperationCanceledException) { }
            try { await publisher.InvokeActionAsync(Form(recovered)); throw new InvalidOperationException("Disposed publisher admitted input."); }
            catch (OperationCanceledException) { }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: real guest shortcut scopes, Empty action inputs, duplicate selection, immediate registration retirement, lost replies, fresh recovery epochs and cancellation.");
        static async Task Lost(Func<Task> action)
        {
            try { await action(); } catch (IOException) { return; }
            throw new InvalidOperationException("Expected a lost Host reply.");
        }
    }

    private sealed class ShortcutPageHost : IPluginRetainedPresentationHost, IPluginShortcutHost
    {
        internal readonly RetainedPageHost Views = new();
        internal PluginShortcutBinding[] Bindings = [];
        internal bool LoseReply, LoseModelReply, Block;
        internal int Sets;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<long> ReadRevisionAsync(CancellationToken token) => Views.ReadRevisionAsync(token);
        public ValueTask ReplaceAsync(long revision, IReadOnlyList<IPluginHostView> views, CancellationToken token) => Views.ReplaceAsync(revision, views, token);
        public ValueTask ReconcileAsync(long revision, IReadOnlyList<IPluginHostView> views, IReadOnlyList<int> previousDocuments, CancellationToken token)
            => Views.ReconcileAsync(revision, views, previousDocuments, token);
        public async ValueTask UpdateModelsAsync(long revision, IReadOnlyList<IPluginHostView> views, CancellationToken token)
        {
            await Views.UpdateModelsAsync(revision, views, token);
            if (LoseModelReply) { LoseModelReply = false; throw new IOException("Lost model reply."); }
        }
        public ValueTask SetOpenAsync(long revision, int document, bool open, CancellationToken token) => Views.SetOpenAsync(revision, document, open, token);
        public ValueTask<IReadOnlyList<int>> ReadVisibleAsync(long revision, CancellationToken token) => Views.ReadVisibleAsync(revision, token);
        public async ValueTask ReplaceAsync(long revision, IReadOnlyList<PluginShortcutBinding> bindings, CancellationToken token)
        {
            Check(revision == Views.Revision && bindings.Count <= 64 && bindings.All(binding => binding.Document >= 0 && binding.Document < Views.Documents.Count));
            if (Block) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            Bindings = bindings.ToArray(); Sets++;
            if (LoseReply) { LoseReply = false; throw new IOException("Lost committed shortcut-set reply."); }
        }
    }
}
