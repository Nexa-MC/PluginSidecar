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
    private static async Task DrawingPublisherChecks()
    {
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Guest fixture required.");
        string root = Path.Combine(Path.GetTempPath(), "sidecar-drawing-bindings-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = new PluginApplication(root, (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)), [],
                new Allow(), (_, _) => { }, (_, _) => { }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            using var package = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(sample)!, "advanced.zip")), "example.advanced", "1.0.0");
            var load = await app.LoadAsync(package, package.Manifest.RequestedPermissions, default); Check(load.State == LoadState.Active);
            var page = app.Snapshot().Pages.Single();
            var host = new DialogPageHost(); var rasters = new RasterHost(() => host.Revision);
            await using var publisher = new PluginPagePublisher(app, host, dialogs: host, rasters: rasters);
            async Task<AbiValue> Ui(string name, AbiValue input)
            {
                var operation = Abi100Catalog.Operations.Single(value => value.Group == "UI" && value.Name == name);
                var reply = await app.Runtime.InvokeAsync(load.Identity, operation.ContractId, operation.MethodId, input, new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default) { IdempotencyKey = Guid.NewGuid().ToString("N") });
                Check(reply.Success, reply.Error?.Code); return reply.Value;
            }
            async Task<DrawingSurfaceRef> Surface(Guid scope)
            {
                var surface = DrawingSurfaceRef.Read(await Ui("CreateDrawingSurface", DrawingSurfaceRequest.Write(new(new("example.advanced:canvas"), 16, 16, scope))));
                var frame = surface with { Revision = new("frame1") };
                await Ui("SubmitDrawCommands", DrawSubmission.Write(new(frame, [new(DrawCommandKind.Rectangle, [0, 0, 16, 16], "#224466", null, null)], surface.Revision)));
                return frame;
            }
            var ordinarySurface = await Surface(load.Identity.LoadInstanceId);
            var subscription = SubscriptionRef.Read(await Ui("SubscribeInput", InputSubscription.Write(new(ordinarySurface, [InputEventKind.Pointer, InputEventKind.Text], 2))));
            app.Runtime.Services.ReadSubscription(load.Identity, subscription);
            rasters.LoseReply = true;
            await Lost(() => publisher.SynchronizeAsync().AsTask());
            var initial = rasters.Bindings.Single(); long revision = host.Revision;
            Check(initial.Slot != "canvas" && initial.Width == 16 && initial.Image.Width == 16);
            Check(host.Documents[initial.Document] is PluginPreparedView view && view.ScopedKeys["canvas"] == initial.Slot);
            await RejectedAsync(() => publisher.ForwardDrawingInputAsync(Input(initial)).AsTask());
            await publisher.SynchronizeAsync(); Check(host.Revision == revision);
            await publisher.ForwardDrawingInputAsync(Input(initial));
            var received = app.Runtime.Services.ReadSubscription(load.Identity, subscription).Items.OfType<AbiValue.Map>().Single().Value;
            Check(((AbiValue.Utf8String)received["kind"]).Value == "Pointer");
            Check(((AbiValue.Float64)received["x"]).Value == 1.5 && ((AbiValue.Float64)received["y"]).Value == 2.5);
            int sets = rasters.Sets;
            await publisher.SynchronizeAsync(); Check(rasters.Sets == sets);
            await RejectedAsync(() => publisher.InvokeActionAsync(Input(initial)).AsTask());
            foreach (var pair in new[] { ("kind", "0"), ("kind", "Unknown"), ("x", "NaN"), ("y", "Infinity"), ("draft", "private"), ("phase", new string('x', 65)), ("text", new string('x', 4097)) })
                await RejectedAsync(() => publisher.ForwardDrawingInputAsync(Input(initial, pair.Item1, pair.Item2)).AsTask());
            Check(app.Runtime.Services.ReadSubscription(load.Identity, subscription).Items.Length == 0);
            var frame2 = ordinarySurface with { Revision = new("frame2") };
            await Ui("SubmitDrawCommands", DrawSubmission.Write(new(frame2, [new(DrawCommandKind.Rectangle, [0, 0, 16, 16], "#FF0000", null, null)], ordinarySurface.Revision)));
            await publisher.SynchronizeAsync();
            var changed = rasters.Bindings.Single(); Check(changed.InputToken == initial.InputToken && !ReferenceEquals(changed.Image, initial.Image));
            await Ui("UpdateModel", UIModelUpdate.Write(new(new(page.Page.Id.Value), page.Page.Schema, TextResult.Write(new("updated")), null)));
            await publisher.SynchronizeAsync(); Check(rasters.Bindings.Single().InputToken == initial.InputToken);
            Check(ReferenceEquals(rasters.Bindings.Single().Image, changed.Image));
            var dialog = UIDialogRef.Read(await Ui("ShowDialog", UIDialog.Write(new(new("example.advanced:dialog"), page.Page.Id, TextResult.Write(new("modal"))))));
            await Surface(dialog.ViewScope);
            await publisher.SynchronizeAsync(); Check(rasters.Bindings.Length == 2);
            var modal = rasters.Bindings.Single(binding => host.Documents[binding.Document] is PluginPreparedView { DialogReference: not null });
            Check(modal.Slot != initial.Slot && modal.InputToken != initial.InputToken);
            Check(rasters.Bindings.Single(binding => binding.InputToken == initial.InputToken).Slot == initial.Slot);
            app.ClosePluginDialog(load.Identity, dialog.Id);
            await RejectedAsync(() => publisher.ForwardDrawingInputAsync(Input(modal)).AsTask());
            await publisher.SynchronizeAsync(); Check(rasters.Bindings.Length == 1);
            await publisher.ForwardDrawingInputAsync(Input(initial));
            app.Runtime.Services.ReadSubscription(load.Identity, subscription);
            host.LoseMutationReply = true;
            await Ui("UpdateModel", UIModelUpdate.Write(new(new(page.Page.Id.Value), page.Page.Schema, TextResult.Write(new("uncertain")), null)));
            await Lost(() => publisher.SynchronizeAsync().AsTask());
            await RejectedAsync(() => publisher.ForwardDrawingInputAsync(Input(initial)).AsTask());
            await publisher.SynchronizeAsync(); var recovered = rasters.Bindings.Single(); Check(recovered.InputToken != initial.InputToken);
            await RejectedAsync(() => publisher.ForwardDrawingInputAsync(Input(initial)).AsTask());
            await publisher.ForwardDrawingInputAsync(Input(recovered));
            await app.StopAsync(load.Identity.PluginId);
            await RejectedAsync(() => publisher.ForwardDrawingInputAsync(Input(recovered)).AsTask());
            rasters.Block = true;
            var synchronizing = publisher.SynchronizeAsync().AsTask();
            await rasters.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            bool cancelled = false; try { await synchronizing; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled);
            Console.WriteLine("PASS: real guest raster slot isolation, scoped input, image revisions, model retention, exact dialog closure, lost replies, source recovery and disposal cancellation.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        static byte[] Input(PluginRasterBinding binding, string? field = null, string? value = null)
        {
            Dictionary<string, string> fields = new() { ["kind"] = "Pointer", ["phase"] = "move", ["x"] = "1.5", ["y"] = "2.5", ["text"] = "" };
            if (field is not null) fields[field] = value!;
            return SidecarUiForms.Encode(binding.InputToken, fields);
        }
        static async Task Lost(Func<Task> action)
        {
            try { await action(); } catch (IOException) { return; }
            throw new Exception("Expected lost raster or source acknowledgement.");
        }
    }

    private sealed class RasterHost(Func<long> revision) : IPluginRasterHost
    {
        internal PluginRasterBinding[] Bindings = [];
        internal int Sets;
        internal bool LoseReply, Block;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask ReplaceAsync(long expected, IReadOnlyList<PluginRasterBinding> bindings, CancellationToken token)
        {
            Check(expected == revision()); token.ThrowIfCancellationRequested();
            if (Block) { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            Bindings = bindings.ToArray(); Sets++;
            if (LoseReply) { LoseReply = false; throw new IOException("Committed raster set lost reply."); }
        }
    }
}
