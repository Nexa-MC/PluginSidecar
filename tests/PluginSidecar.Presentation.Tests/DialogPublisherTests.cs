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
    private static async Task DialogPublisherChecks()
    {
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Guest fixture required.");
        string fixtures = Path.GetDirectoryName(Path.GetFullPath(sample))!;
        string root = Path.Combine(Path.GetTempPath(), "sidecar-dialogs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PluginApplication(root, (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)), [],
                new Allow(), (_, _) => { }, (_, _) => { }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            using var advanced = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(Path.Combine(fixtures, "modal.zip")), "example.advanced", "1.0.0");
            using var desktop = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(Path.Combine(fixtures, "desktop.zip")), "example.desktop", "1.0.0");
            var load = await app.LoadAsync(advanced, advanced.Manifest.RequestedPermissions, default); Check(load.State == LoadState.Active);
            var other = await app.LoadAsync(desktop, desktop.Manifest.RequestedPermissions, default); Check(other.State == LoadState.Active);
            var page = app.Snapshot().Pages.Single(value => value.Owner == load.Identity);
            var otherPage = app.Snapshot().Pages.Single(value => value.Owner == other.Identity);
            var host = new DialogPageHost();
            await using var publisher = new PluginPagePublisher(app, host, dialogs: host);
            async Task<AbiValue> Ui(PluginIdentity owner, string name, AbiValue input)
            {
                var operation = Abi100Catalog.Operations.Single(value => value.Group == "UI" && value.Name == name);
                var result = await app.Runtime.InvokeAsync(owner, operation.ContractId, operation.MethodId, input,
                    new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default) { IdempotencyKey = Guid.NewGuid().ToString("N") });
                Check(result.Success, result.Error?.Code); return result.Value;
            }
            async Task<UIDialogRef> Show(PluginIdentity owner, PluginPage source, AbiValue model) => UIDialogRef.Read(await Ui(owner, "ShowDialog",
                UIDialog.Write(new(new(owner.PluginId + ":window"), source.Page.Id, model))));
            async Task<SubscriptionRef> Watch(UIDialogRef reference) => SubscriptionRef.Read(await Ui(load.Identity, "WatchViewLifecycle",
                UIViewWatch.Write(new(reference.Id, reference.ViewScope, null))));
            PluginPreparedView Modal(UIDialogRef reference) => host.Documents.OfType<PluginPreparedView>().Single(view => view.DialogReference == reference);
            byte[] Form(PluginPreparedView value) => SidecarUiForms.Encode(Nodes(Compile(value).Root.Root).Single(node => node.Command is not null).Argument!, new Dictionary<string, string>());
            bool Alive(UIDialogRef reference) => app.AdvancedUi().Dialogs.Any(dialog => dialog.Reference == reference);
            string[] Events(SubscriptionRef watch) => app.Runtime.Services.ReadSubscription(load.Identity, watch).Items.Select(item => TextResult.Read(item).Text).ToArray();
            async Task ActionResult(byte[] form, string expected)
            {
                var operation = await publisher.InvokeActionAsync(form);
                var read = Abi100Catalog.Operations.Single(value => value.Group == "Tasks" && value.Name == "Read");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var result = await app.Runtime.InvokeAsync(load.Identity, read.ContractId, read.MethodId, EntityRequest.Write(new(operation.Id)),
                        new(Guid.NewGuid(), TimeSpan.FromSeconds(5), deadline.Token));
                    Check(result.Success); var record = OperationRecord.Read(result.Value);
                    if (record.State == OperationState.Completed) { Check(TextResult.Read(record.Result).Text == expected); return; }
                    Check(record.State is not (OperationState.Failed or OperationState.Cancelled or OperationState.OutcomeUnknown));
                    await Task.Delay(5, deadline.Token);
                }
            }
            await publisher.SynchronizeAsync();
            var first = await Show(load.Identity, page, TextResult.Write(new("dialog own model")));
            var watch = await Watch(first); Check(Events(watch).SequenceEqual(["open"]));
            await publisher.SynchronizeAsync(); Check(host.Opens == 1 && host.IsOpen(first));
            var modal = Modal(first); byte[] firstForm = Form(modal);
            var ordinary = host.Documents.OfType<PluginPreparedView>().Single(value => value.Owner == load.Identity && value.DialogReference is null);
            Reject(() => ordinary.TranslateAction(firstForm, load.Identity));
            await publisher.SetOpenAsync(load.Identity, page.Page.Id, true); Check(host.LastPage == ordinary.Document.Target);
            await publisher.ReportVisibilityAsync();
            await ActionResult(firstForm, "dialog own model");
            await Ui(load.Identity, "UpdateModel", UIModelUpdate.Write(new(first.Id, page.Page.Schema, TextResult.Write(new("updated dialog model")), null)));
            await RejectedAsync(() => publisher.InvokeActionAsync(firstForm).AsTask());
            await publisher.SynchronizeAsync(); Check(host.Opens == 1 && host.IsOpen(first));
            Check(Modal(first).Document.Target == modal.Document.Target);
            await ActionResult(firstForm, "updated dialog model"); Check(Events(watch).Length == 0);

            // The user closes the first window after its state was read while another window is added.
            var second = await Show(other.Identity, otherPage, Empty.Write(new()));
            host.AfterRead = () => host.NativeClose(first);
            await publisher.SynchronizeAsync(); Check(host.Opens == 2 && !host.IsOpen(first) && host.IsOpen(second));
            await publisher.SynchronizeAsync(); Check(!Alive(first) && Alive(second) && host.Opens == 2);
            Check(Events(watch).SequenceEqual(["closed"])); await RejectedAsync(() => publisher.InvokeActionAsync(firstForm).AsTask());
            app.Runtime.Services.ReleaseSubscription(load.Identity, watch);

            var third = await Show(load.Identity, page, TextResult.Write(new("third")));
            host.LoseOpenReply = true;
            await ExpectIo(() => publisher.SynchronizeAsync().AsTask()); Check(host.IsOpen(third) && host.Opens == 3);
            long revision = host.Revision; int replacements = host.Replacements;
            await publisher.SynchronizeAsync(); Check(host.Opens == 3 && host.Revision == revision && host.Replacements == replacements && Alive(third));
            host.MalformedRead = true;
            bool malformed = false;
            try { await publisher.SynchronizeAsync(); } catch (InvalidDataException) { malformed = true; }
            Check(malformed && Alive(third) && Alive(second) && host.Revision == revision);
            host.MalformedRead = false;
            await publisher.SynchronizeAsync();

            // Recovery from a lost source/model mutation seals old action admission and never replays opens.
            byte[] thirdForm = Form(Modal(third));
            await Ui(load.Identity, "UpdateModel", UIModelUpdate.Write(new(third.Id, page.Page.Schema, TextResult.Write(new("uncertain update")), null)));
            host.LoseMutationReply = true;
            await ExpectIo(() => publisher.SynchronizeAsync().AsTask());
            await RejectedAsync(() => publisher.InvokeActionAsync(thirdForm).AsTask());
            await publisher.SynchronizeAsync(); Check(host.Opens == 3 && host.Replacements == replacements + 1);
            await publisher.SynchronizeAsync(); Check(!Alive(third) && !Alive(second) && host.Opens == 3);
            Check(!host.Documents.Any(value => value.Document.Operation == SidecarNxmlOperation.Dialog));

            var fourth = await Show(load.Identity, page, TextResult.Write(new("guest close")));
            await publisher.SynchronizeAsync(); byte[] fourthForm = Form(Modal(fourth));
            await Ui(load.Identity, "CloseDialog", EntityRequest.Write(new(fourth.Id)));
            await RejectedAsync(() => publisher.InvokeActionAsync(fourthForm).AsTask());
            await publisher.SynchronizeAsync(); Check(!host.IsOpen(fourth));

            var fifth = await Show(load.Identity, page, TextResult.Write(new("source retirement")));
            await publisher.SynchronizeAsync(); int opens = host.Opens;
            // Removing an unrelated command does not retire this dialog or its scoped contribution stamp.
            var command = app.Runtime.Definitions.Entries.Single(entry => entry.Owner == load.Identity && entry.Definition.Registry.Value == "nexa:ui/commands");
            var shortcut = app.Runtime.Definitions.Entries.Single(entry => entry.Owner == load.Identity && entry.Definition.Registry.Value == "nexa:ui/shortcuts");
            foreach (var entry in new[] { shortcut, command }) app.Runtime.Definitions.Remove(new(load.Identity, Guid.Empty, "Active", new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default)),
                new(entry.Definition.Registry, entry.Definition.Id, entry.Revision));
            await publisher.SynchronizeAsync(); Check(Alive(fifth) && host.IsOpen(fifth) && host.Opens == opens);
            var contribution = app.Runtime.Definitions.Entries.Single(entry => entry.Owner == load.Identity && entry.Definition.Registry.Value == "nexa:ui/contributions");
            app.Runtime.Definitions.Remove(new(load.Identity, Guid.Empty, "Active", new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default)),
                new(contribution.Definition.Registry, contribution.Definition.Id, contribution.Revision));
            await publisher.SynchronizeAsync(); Check(!Alive(fifth) && !host.IsOpen(fifth) && host.Opens == opens);

            var last = await Show(load.Identity, page, TextResult.Write(new("shutdown")));
            await publisher.SynchronizeAsync();
            host.BlockRead = true; var pending = publisher.SynchronizeAsync().AsTask();
            await host.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.DisposeAsync();
            try { await pending; } catch (OperationCanceledException) { }
            Check(pending.IsCanceled);
            await app.StopAsync(load.Identity.PluginId); Check(!Alive(last));
            Console.WriteLine("PASS: sole dialog publisher scopes real guest models, retains concurrent windows, observes native close, fences lost replies and drains cancellation.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        static async Task ExpectIo(Func<Task> action)
        { try { await action(); } catch (IOException) { return; } throw new InvalidOperationException("Expected lost acknowledgement."); }
    }

    private sealed class DialogPageHost : IPluginRetainedPresentationHost, IPluginDialogHost
    {
        private readonly RetainedPageHost _pages = new();
        private bool[] _open = [];
        internal bool LoseOpenReply, LoseMutationReply, MalformedRead, BlockRead;
        internal Action? AfterRead;
        internal int Opens;
        internal string? LastPage;
        internal TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyList<IPluginHostView> Documents => _pages.Documents;
        internal long Revision => _pages.Revision;
        internal int Replacements => _pages.Replacements;
        internal bool IsOpen(UIDialogRef reference) => Documents.Select((view, index) => (view, index)).Any(item =>
            item.view is PluginPreparedView guest && guest.DialogReference == reference && _open[item.index]);
        internal void NativeClose(UIDialogRef reference)
        { for (int index = 0; index < Documents.Count; index++) if (Documents[index] is PluginPreparedView guest && guest.DialogReference == reference) _open[index] = false; }
        public ValueTask<long> ReadRevisionAsync(CancellationToken token) => _pages.ReadRevisionAsync(token);
        public async ValueTask ReplaceAsync(long revision, IReadOnlyList<IPluginHostView> views, CancellationToken token)
        { await _pages.ReplaceAsync(revision, views, token); _open = new bool[views.Count]; MaybeLoseMutation(); }
        public async ValueTask ReconcileAsync(long revision, IReadOnlyList<IPluginHostView> views, IReadOnlyList<int> previousDocuments, CancellationToken token)
        {
            var before = _open; await _pages.ReconcileAsync(revision, views, previousDocuments, token);
            _open = previousDocuments.Select(index => index >= 0 && before[index]).ToArray(); MaybeLoseMutation();
        }
        public async ValueTask UpdateModelsAsync(long revision, IReadOnlyList<IPluginHostView> views, CancellationToken token)
        { await _pages.UpdateModelsAsync(revision, views, token); MaybeLoseMutation(); }
        private void MaybeLoseMutation()
        { if (LoseMutationReply) { LoseMutationReply = false; throw new IOException("Committed mutation lost reply."); } }
        public ValueTask SetOpenAsync(long revision, int document, bool open, CancellationToken token)
        { Check(Documents[document].Document.Operation == SidecarNxmlOperation.Page); LastPage = Documents[document].Document.Target; return _pages.SetOpenAsync(revision, document, open, token); }
        public ValueTask<IReadOnlyList<int>> ReadVisibleAsync(long revision, CancellationToken token) => _pages.ReadVisibleAsync(revision, token);
        ValueTask IPluginDialogHost.SetOpenAsync(long revision, int document, bool open, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Check(revision == Revision && Documents[document].Document.Operation == SidecarNxmlOperation.Dialog);
            if (open) Opens++; _open[document] = open;
            if (LoseOpenReply) { LoseOpenReply = false; throw new IOException("Committed open lost reply."); }
            return ValueTask.CompletedTask;
        }
        public async ValueTask<IReadOnlyList<PluginHostDialogState>> ReadAsync(long revision, CancellationToken token)
        {
            Check(revision == Revision);
            if (BlockRead) { ReadEntered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            var state = MalformedRead ? [] : Documents.Select((view, index) => (view, index))
                .Where(item => item.view.Document.Operation == SidecarNxmlOperation.Dialog)
                .Select(item => new PluginHostDialogState(item.index, item.view.Document.Target, _open[item.index])).ToArray();
            var after = AfterRead; AfterRead = null; after?.Invoke(); return state;
        }
    }
}
