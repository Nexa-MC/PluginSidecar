using System.Collections.Immutable;
using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using Nexa.Xsr.Renderer.Nxml;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Presentation;
using PluginSidecar.WasmRuntime;

await PresentationTests.Run();

internal static partial class PresentationTests
{
    private static void Check(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? message = null)
    { if (!condition) throw new InvalidOperationException("Presentation boundary: " + message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is AbiHostException or NxmlCompileException or NxmlParseException or NxmlLoadException or InvalidDataException) { return; }
        throw new InvalidOperationException("Guest presentation unexpectedly accepted.");
    }
    private static IEnumerable<NxmlIrNode> Nodes(NxmlIrNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var node in Nodes(child)) yield return node;
    }
    private static NxmlPreparedDocument Compile(PluginPreparedView view) => new NxmlExtensionProgram(view.Document.EntryPoint, view.Document.Files).Compile(view.Model);
    private static NxmlPreparedDocument Compile(IPluginHostView view) => new NxmlExtensionProgram(view.Document.EntryPoint, view.Document.Files).Compile(view.Model);
    private static AbiValue.Map Model(params (string Key, AbiValue Value)[] values) => new(values.ToImmutableDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    private static PluginPage Page(PluginIdentity owner, string source, AbiValue? model = null, string id = "example.test:page") => new(owner,
        new(new(id), new(Guid.NewGuid(), owner.LoadInstanceId, "package"), Abi100Schemas.Empty, []), "Test", source, model ?? new AbiValue.Null(), new("1"));
    internal static async Task Run()
    {
        var identity = new PluginIdentity("example.test", "1.0.0", Guid.NewGuid());
        string source = """<nxml-page><Page Key="Page"><StackPanel><Text Content="{data title}" /><TextInput Key="draft" Label="Draft" /><Button Key="save" Content="Save" Command="ui.plugin.action" Argument="{data action}" /></StackPanel></Page></nxml-page>""";
        var page = Page(identity, source, Model(("title", new AbiValue.Utf8String("<Image Source='private'/> {state host.secret}")),
            ("action", new AbiValue.Utf8String("example.test:save")), ("number", new AbiValue.Int64(42))));
        var view = PluginViewCompiler.Prepare(page);
        var compiled = Compile(view);
        var nodes = Nodes(compiled.Root.Root).ToArray();
        var input = nodes.Single(n => n.Recipe == NxmlRuntimeRecipe.TextInput);
        var button = nodes.Single(n => n.Command is not null);
        Check(view.Document.Target == "#" + compiled.Root.Root.Key);
        Check(input.Key != "draft" && button.Command!.Value.Value == PluginViewCompiler.ActionCommand);
        Check(nodes.All(n => n.Bindings.Count == 0 && n.Recipe != NxmlRuntimeRecipe.Image));
        Check(nodes.Any(n => n.Content == "<Image Source='private'/> {state host.secret}"));
        byte[] Form(string argument, string key = "", string value = "") => SidecarUiForms.Encode(argument,
            key == "" ? new Dictionary<string, string>() : new Dictionary<string, string> { [key] = value });
        byte[] form = Form(button.Argument!, input.Key!, "typed & <value>");
        var action = view.TranslateAction(form, identity);
        Check(action.Action.Value == "example.test:save");
        var values = ((AbiValue.Map)action.Inputs).Value;
        Check(((AbiValue.Utf8String)values["draft"]).Value == "typed & <value>");
        Check(((AbiValue.Int64)values["number"]).Value == 42);
        Check(!values.ContainsKey(input.Key!));
        Reject(() => view.TranslateAction(Form("example.test:save"), identity));
        Reject(() => view.TranslateAction(Form(button.Argument!, "foreign", "secret"), identity));
        Reject(() => view.TranslateAction(form, identity with { LoadInstanceId = Guid.NewGuid() }));
        var other = PluginViewCompiler.Prepare(page with { Page = page.Page with { Id = new("example.test:other") } });
        Reject(() => other.TranslateAction(form, identity));
        Check(view.Document.Target != other.Document.Target);
        var changed = PluginViewCompiler.Prepare(page with { Model = Model(("title", new AbiValue.Utf8String("Updated")), ("action", new AbiValue.Utf8String("example.test:next"))), Revision = new("2") });
        Check(view.Document.Files[view.Document.EntryPoint] == changed.Document.Files[changed.Document.EntryPoint]);
        Check(view.Document.Target == changed.Document.Target);
        Reject(() => changed.TranslateAction(form, identity));
        Check(Nodes(Compile(changed).Root.Root).Any(n => n.Content == "Updated"));
        var foreignAction = PluginViewCompiler.Prepare(page with { Model = Model(("title", new AbiValue.Utf8String("Test")), ("action", new AbiValue.Utf8String("other.plugin:save"))) });
        var foreignButton = Nodes(Compile(foreignAction).Root.Root).Single(n => n.Command is not null);
        Reject(() => foreignAction.TranslateAction(Form(foreignButton.Argument!), identity));
        // Static keys do not inflate the dynamic data budget during namespacing.
        string many = "<nxml-page><Page Key=\"Many\"><StackPanel>" + string.Concat(Enumerable.Range(0, 300).Select(i => "<Text Key=\"k" + i + "\" Content=\"text\"/>")) + "</StackPanel></Page></nxml-page>";
        Check(PluginViewCompiler.Prepare(Page(identity, many)).Model.Count == 0);
        foreach (string forbidden in new[]
        {
            "<Text Content=\"{state host.secret}\"/>", "<Image Source=\"file:///private\"/>",
            "<Node Kind=\"Image\" Recipe=\"Image\" Source=\"private\"/>",
            "<Button Command=\"ui.host.erase\"/>", "<Button Command=\"{data action}\"/>",
            "<StackPanel ScrollCommand=\"ui.plugin.action\"/>", "<StackPanel DismissCommand=\"ui.host.erase\"/>"
        }) Reject(() => PluginViewCompiler.Prepare(Page(identity, "<nxml-page><Page Key=\"Bad\">" + forbidden + "</Page></nxml-page>")));
        PluginViewCompiler.ValidateComponent("<nxml-dynamic><Text Content=\"Component\" /></nxml-dynamic>");
        Reject(() => PluginViewCompiler.ValidateComponent("<nxml-dynamic><Button DismissCommand=\"ui.host.erase\" /></nxml-dynamic>"));
        ContributionChecks(identity);
        ReplacementChecks(identity);
        WorkspaceChecks();
        await HostPort(view);
        await NotificationPort();
        await RealGuest();
        await RealReplacementGuest();
        await ManagementChecks();
        await ManagementControllerChecks();
        await DeveloperInteractionChecks();
        await ExactLoadRetirementChecks();
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is { } name && (name.StartsWith("Nexa.Services.", StringComparison.Ordinal)
            || name.StartsWith("Avalonia", StringComparison.Ordinal) || name == "Nexa.Desktop")));
        Console.WriteLine("PASS: Sidecar compiles guest pages, scopes keys/actions, preserves models, rejects Host escape paths and invokes the real Wasm action without launcher assemblies.");
    }
    private static async Task RealGuest()
    {
        string? sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE");
        if (sample is null) throw new InvalidOperationException("NEXA_WASM_SAMPLE must identify the compiled guest fixture.");
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "desktop.zip"));
        string root = Path.Combine(Path.GetTempPath(), "sidecar-view-" + Guid.NewGuid().ToString("N"));
        var confirmation = new Allow();
        try
        {
            using var package = VerifiedPluginPackage.Parse(bytes, "example.desktop", "1.0.0");
            await using var app = new PluginApplication(root, (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)), [], confirmation,
                (_, _) => { }, (_, _) => { }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            var loaded = await app.LoadAsync(package, package.Manifest.RequestedPermissions, default);
            Check(loaded.State == LoadState.Active);
            var page = app.Snapshot().Pages.Single();
            var host = new TestPageHost { LoseAcknowledgement = true };
            await using var publisher = new PluginPagePublisher(app, host);
            bool uncertain = false;
            try { await publisher.SynchronizeAsync(); } catch (IOException) { uncertain = true; }
            Check(uncertain && host.Revision == 1);
            byte[] Form(PluginPreparedView value) => SidecarUiForms.Encode(Nodes(Compile(value).Root.Root).Single(node => node.Command is not null).Argument!, new Dictionary<string, string>());
            byte[] retiredForm = Form(host.Views.Single());
            await RejectedAsync(() => publisher.InvokeActionAsync(retiredForm).AsTask());
            await publisher.SynchronizeAsync();
            Check(host.Revision == 2 && host.Replacements == 2);
            await RejectedAsync(() => publisher.InvokeActionAsync(retiredForm).AsTask());
            byte[] form = Form(host.Views.Single());
            await publisher.SynchronizeAsync(); Check(host.Revision == 2); // No redundant replacement.
            await publisher.SetOpenAsync(loaded.Identity, page.Page.Id, true);
            Check(host.Visible == 0);
            await RejectedAsync(() => publisher.SetOpenAsync(loaded.Identity with { LoadInstanceId = Guid.NewGuid() }, page.Page.Id, false).AsTask());
            Check(host.Visible == 0);
            await publisher.ReportVisibilityAsync();
            await publisher.SetOpenAsync(loaded.Identity, page.Page.Id, false); Check(host.Visible == -1);
            var reference = await publisher.InvokeActionAsync(form);
            var read = Abi100Catalog.Operations.Single(o => o.Group == "Tasks" && o.Name == "Read");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var progress = await app.Runtime.InvokeAsync(loaded.Identity, read.ContractId, read.MethodId, EntityRequest.Write(new(reference.Id)),
                    new(Guid.NewGuid(), TimeSpan.FromSeconds(5), deadline.Token));
                Check(progress.Success);
                var record = OperationRecord.Read(progress.Value);
                if (record.State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled or OperationState.OutcomeUnknown)
                {
                    Check(record.State == OperationState.Completed, record.Failure?.Code);
                    Check(TextResult.Read(record.Result).Text == "Desktop action completed");
                    break;
                }
                await Task.Delay(5, deadline.Token);
            }
            Check(confirmation.Count == 1);
            using var advanced = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "advanced.zip")), "example.advanced", "1.0.0");
            var advancedLoad = await app.LoadAsync(advanced, advanced.Manifest.RequestedPermissions, default);
            Check(advancedLoad.State == LoadState.Active);
            await publisher.SynchronizeAsync(); Check(host.Views.Count == 2 && host.Revision == 3);
            await RejectedAsync(() => publisher.InvokeActionAsync(form).AsTask()); // Source-set replacement rotates every action epoch.
            var advancedPage = app.Snapshot().Pages.Single(value => value.Owner == advancedLoad.Identity);
            var update = Abi100Catalog.Operations.Single(value => value.Group == "UI" && value.Name == "UpdateModel");
            var modelResult = await app.Runtime.InvokeAsync(advancedLoad.Identity, update.ContractId, update.MethodId,
                UIModelUpdate.Write(new(new(advancedPage.Page.Id.Value), advancedPage.Page.Schema, TextResult.Write(new("Model update")), null)),
                new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default) { IdempotencyKey = Guid.NewGuid().ToString("N") });
            Check(modelResult.Success, modelResult.Error?.Code);
            string[] targets = host.Views.Select(value => value.Document.Target).ToArray();
            await publisher.SynchronizeAsync();
            Check(host.Revision == 4 && host.ModelUpdates == 1 && targets.SequenceEqual(host.Views.Select(value => value.Document.Target)));
            Check(host.Views.Single(value => value.Owner == advancedLoad.Identity).Model["text"] == "Model update");
            await publisher.SynchronizeAsync(); Check(host.Revision == 4);
            var beforeRetirement = Nodes(Compile(host.Views.Single(value => value.Owner == loaded.Identity)).Root.Root).Single(node => node.Command is not null);
            var beforeRetirementForm = SidecarUiForms.Encode(beforeRetirement.Argument!, new Dictionary<string, string>());
            var contribution = app.Runtime.Definitions.Entries.Single(entry => entry.Owner == advancedLoad.Identity && entry.Definition.Registry.Value == "nexa:ui/contributions");
            app.Runtime.Definitions.Remove(new(advancedLoad.Identity, Guid.Empty, "Active", new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default)),
                new(contribution.Definition.Registry, contribution.Definition.Id, contribution.Revision));
            Check(app.Runtime.Active.Contains(advancedLoad.Identity) && app.Snapshot().Pages.Any(value => value.Owner == advancedLoad.Identity));
            await RejectedAsync(() => publisher.SetOpenAsync(advancedLoad.Identity, advancedPage.Page.Id, true).AsTask());
            await publisher.SynchronizeAsync(); Check(host.Revision == 5);
            await RejectedAsync(() => publisher.InvokeActionAsync(beforeRetirementForm).AsTask());
            await publisher.SetOpenAsync(advancedLoad.Identity, advancedPage.Page.Id, true);
            await app.StopAsync(advancedLoad.Identity.PluginId);
            await publisher.SynchronizeAsync(); Check(host.Views.Count == 1 && host.Revision == 6);

            await app.StopAsync(loaded.Identity.PluginId);
            Check(app.Runtime.Active.IsEmpty);
            await RejectedAsync(() => publisher.InvokeActionAsync(form).AsTask());
            await RejectedAsync(() => publisher.SetOpenAsync(loaded.Identity, page.Page.Id, true).AsTask());
            await publisher.SynchronizeAsync(); Check(host.Views.Count == 0 && host.Revision == 7);
            host.BlockVisibility = true;
            var waiting = publisher.ReportVisibilityAsync().AsTask();
            await host.VisibilityEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await publisher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            try { await waiting; } catch (OperationCanceledException) { }
            Check(waiting.IsCanceled);
            bool cancelled = false;
            try { await publisher.SynchronizeAsync(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled);

        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task RejectedAsync(Func<Task> action)
    {
        try { await action(); } catch (AbiHostException) { return; }
        throw new InvalidOperationException("Retired or foreign presentation action admitted.");
    }
    private sealed class TestPageHost : IPluginPresentationHost
    {
        internal long Revision;
        internal int Replacements, ModelUpdates, Visible = -1;
        internal bool LoseAcknowledgement, BlockVisibility;
        internal TaskCompletionSource VisibilityEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyList<IPluginHostView> Documents = [];
        internal IReadOnlyList<PluginPreparedView> Views => Documents.OfType<PluginPreparedView>().ToArray();
        public ValueTask<long> ReadRevisionAsync(CancellationToken token) => ValueTask.FromResult(Revision);
        public ValueTask ReplaceAsync(long revision, IReadOnlyList<IPluginHostView> views, CancellationToken token)
        {
            Check(revision == Revision); Revision++; Replacements++; Documents = views.ToArray(); Visible = -1;
            if (LoseAcknowledgement) { LoseAcknowledgement = false; throw new IOException("Simulated lost reply after Host commit."); }
            return ValueTask.CompletedTask;
        }
        public ValueTask UpdateModelsAsync(long revision, IReadOnlyList<IPluginHostView> views, CancellationToken token)
        { Check(revision == Revision); Revision++; ModelUpdates++; Documents = views.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask SetOpenAsync(long revision, int document, bool open, CancellationToken token)
        { Check(revision == Revision && document >= 0 && document < Documents.Count); Visible = open ? document : -1; return ValueTask.CompletedTask; }
        public async ValueTask<IReadOnlyList<int>> ReadVisibleAsync(long revision, CancellationToken token)
        {
            Check(revision == Revision);
            if (BlockVisibility) { VisibilityEntered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return Visible < 0 ? [] : [Visible];
        }
    }
    private sealed class Allow : IActionConfirmation
    {
        internal int Count;
        public ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request, CancellationToken cancellation) { Count++; return ConfirmThroughHost(request, cancellation); }
    }
}
