using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using PluginSidecar.Application;
using PluginSidecar.Presentation;
using PluginSidecar.Packages;
using PluginSidecar.WasmRuntime;

internal static partial class PresentationTests
{
    private static async Task RealReplacementGuest()
    {
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Guest fixture required.");
        string root = Path.Combine(Path.GetTempPath(), "sidecar-replacement-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var package = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!,
                "replacement.zip")), "example.replacement", "1.0.0");
            await using var app = new PluginApplication(root, (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)), [],
                new Allow(), (_, _) => { }, (_, _) => { }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            var load = await app.LoadAsync(package, package.Manifest.RequestedPermissions, default);
            Check(load.State == LoadState.Active, load.Error?.Code);
            var original = app.Snapshot().Pages.Single(p => p.Page.Id.Value.EndsWith(":original", StringComparison.Ordinal));
            var target = app.Snapshot().Pages.Single(p => p.Page.Id.Value.EndsWith(":target", StringComparison.Ordinal));
            async Task Model(PluginPage page, string text)
            {
                var operation = Abi100Catalog.Operations.Single(o => o.Group == "UI" && o.Name == "UpdateModel");
                var result = await app.Runtime.InvokeAsync(load.Identity, operation.ContractId, operation.MethodId,
                    UIModelUpdate.Write(new(new(page.Page.Id.Value), page.Page.Schema, TextResult.Write(new(text)), null)),
                    new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default) { IdempotencyKey = Guid.NewGuid().ToString("N") });
                Check(result.Success, result.Error?.Code);
            }
            await Model(original, "Original model"); await Model(target, "Replacement model");
            var host = new TestPageHost();
            await using var publisher = new PluginPagePublisher(app, host);
            await publisher.SynchronizeAsync();
            var view = host.Views.Single(p => p.Page == original.Page.Id);
            Check(Nodes(Compile(view).Root.Root).Any(n => n.Content == "Rendered Replacement model"));
            byte[] Form(PluginPreparedView item) => SidecarUiForms.Encode(Nodes(Compile(item).Root.Root).Single(n => n.Command is not null).Argument!, new Dictionary<string, string>());
            byte[] form = Form(view);
            var invocation = await publisher.InvokeActionAsync(form);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var read = Abi100Catalog.Operations.Single(o => o.Group == "Tasks" && o.Name == "Read");
            while (true)
            {
                var result = await app.Runtime.InvokeAsync(load.Identity, read.ContractId, read.MethodId, EntityRequest.Write(new(invocation.Id)),
                    new(Guid.NewGuid(), TimeSpan.FromSeconds(5), deadline.Token));
                Check(result.Success, result.Error?.Code);
                var record = OperationRecord.Read(result.Value);
                if (record.State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled or OperationState.OutcomeUnknown)
                {
                    Check(record.State == OperationState.Completed, record.Failure?.Code);
                    Check(TextResult.Read(record.Result).Text == "Original model"); break;
                }
                await Task.Delay(5, deadline.Token);
            }
            await Model(target, "Updated replacement");
            await RejectedAsync(() => publisher.SetOpenAsync(load.Identity, original.Page.Id, true).AsTask());
            await RejectedAsync(() => publisher.InvokeActionAsync(form).AsTask());
            await publisher.SynchronizeAsync();
            Check(host.Replacements == 1 && host.ModelUpdates == 1);
            var updated = host.Views.Single(p => p.Page == original.Page.Id);
            Check(view.Document.Target == updated.Document.Target && view.Document.Files[view.Document.EntryPoint] == updated.Document.Files[updated.Document.EntryPoint]);
            Check(Nodes(Compile(updated).Root.Root).Any(n => n.Content == "Rendered Updated replacement"));
            await publisher.SetOpenAsync(load.Identity, original.Page.Id, true);
            var declaration = app.Runtime.Definitions.Entries.Single(e => e.Owner == load.Identity && e.Definition.Id == target.Page.Id);
            Reject(() => app.Runtime.Definitions.Remove(new(load.Identity, Guid.Empty, "Active", new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default)),
                new(declaration.Definition.Registry, declaration.Definition.Id, declaration.Revision)));
            var replacement = app.Runtime.Definitions.Entries.Single(e => e.Owner == load.Identity && e.Definition.Registry.Value == "nexa:ui/replacements");
            app.Runtime.Definitions.Remove(new(load.Identity, Guid.Empty, "Active", new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default)),
                new(replacement.Definition.Registry, replacement.Definition.Id, replacement.Revision));
            Check(app.Runtime.Active.Contains(load.Identity));
            await RejectedAsync(() => publisher.SetOpenAsync(load.Identity, original.Page.Id, true).AsTask());
            await RejectedAsync(() => publisher.InvokeActionAsync(form).AsTask());
            app.Runtime.Definitions.Remove(new(load.Identity, Guid.Empty, "Active", new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default)),
                new(declaration.Definition.Registry, declaration.Definition.Id, declaration.Revision));
            await publisher.SynchronizeAsync(); Check(host.Revision == 3 && host.Views.Count == 1);
            Check(Nodes(Compile(host.Views.Single()).Root.Root).Any(n => n.Content == "Rendered Original model"));
            await RejectedAsync(() => publisher.InvokeActionAsync(form).AsTask());
            await publisher.SetOpenAsync(load.Identity, original.Page.Id, true);
            Console.WriteLine("PASS: real Wasm replacement registration, original-model callback, target updates and retired-target admission fences.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void ReplacementChecks(PluginIdentity owner)
    {
        var page = Page(owner, "<nxml-page><Page><Text Content=\"Original\"/></Page></nxml-page>",
            Model(("text", new AbiValue.Utf8String("Original data")), ("number", new AbiValue.Int64(42))));
        var target = Page(owner, """
            <nxml-page><Page Key="Replacement"><StackPanel>
              <Text Content="{data text}"/><Text Content="Prefix {data text} / {data number}"/>
              <Text Content="Hidden" IsVisible="{data !visible}"/>
              <TextInput Key="draft" Label="{data text}" Placeholder="Enter {data text}"/>
              <Button Content="Save" Command="ui.plugin.action" Argument="{data action}"/>
            </StackPanel></Page></nxml-page>
            """, Model(("text", new AbiValue.Utf8String("<literal> {data secret}")), ("number", new AbiValue.Int64(7)),
                ("visible", new AbiValue.Bool(true)), ("action", new AbiValue.Utf8String("example.test:save"))), "example.test:replacement");
        var replacement = new UIViewReplacement(new("example.test:replace"), page.Page.Id, page.Page.Schema, target.Page.Id);
        var registration = new PluginUiRegistration(owner, "replacements", UIViewReplacement.Write(replacement),
            new(1, owner.PluginId, owner.LoadInstanceId, replacement.Id, RegistrationState.Active));
        var componentId = new QualifiedId("example.test:extra");
        var component = new PluginUiRegistration(owner, "components", new AbiValue.Null(),
            new(2, owner.PluginId, owner.LoadInstanceId, componentId, RegistrationState.Active));
        var contribution = new UIContribution(new("example.test:append"), page.Page.Id, "content", UIContributionMode.Append, componentId, [], []);
        var append = new PluginUiRegistration(owner, "contributions", UIContribution.Write(contribution),
            new(3, owner.PluginId, owner.LoadInstanceId, contribution.Id, RegistrationState.Active));
        var ui = new PluginAdvancedUi([registration, component, append], [], ImmutableDictionary<QualifiedId, string>.Empty
            .Add(componentId, "<nxml-dynamic><Text Content=\"{data text}\"/></nxml-dynamic>"));
        var view = PluginViewCompiler.Prepare(page, ui, [page, target]);
        var nodes = Nodes(Compile(view).Root.Root).ToArray();
        Check(view.Page == page.Page.Id && view.Revision == page.Revision);
        Check(nodes.Any(n => n.Content == "<literal> {data secret}"));
        Check(nodes.Any(n => n.Content == "Prefix <literal> {data secret} / 7"));
        Check(nodes.Any(n => n.Content == "Original data"));
        Check(!nodes.Single(n => n.Content == "Hidden").IsVisible);
        Check(!nodes.Any(n => n.Content == "Original"));
        var button = nodes.Single(n => n.Command is not null);
        var input = nodes.Single(n => n.Recipe == Nexa.Xsr.Renderer.Nxml.NxmlRuntimeRecipe.TextInput);
        var request = view.TranslateAction(SidecarUiForms.Encode(button.Argument!, new Dictionary<string, string> { [input.Key!] = "Typed" }), owner);
        Check(request.Action.Value == "example.test:save");
        var fields = ((AbiValue.Map)request.Inputs).Value;
        Check(fields.Count == 3 && ((AbiValue.Int64)fields["number"]).Value == 42);
        Check(((AbiValue.Utf8String)fields["text"]).Value == "Original data" && ((AbiValue.Utf8String)fields["draft"]).Value == "Typed");
        var updated = target with
        {
            Model = Model(("text", new AbiValue.Utf8String("Changed")), ("number", new AbiValue.Int64(8)),
            ("visible", new AbiValue.Bool(false)), ("action", new AbiValue.Utf8String("example.test:other"))),
            Revision = new("2")
        };
        var next = PluginViewCompiler.Prepare(page, ui, [page, updated]);
        Check(view.Document.Files[view.Document.EntryPoint] == next.Document.Files[next.Document.EntryPoint]);
        Check(Compile(next).Root.Root.Key == Compile(view).Root.Root.Key);
        Check(Nodes(Compile(next).Root.Root).Any(n => n.Content == "Changed"));
        Check(Nodes(Compile(next).Root.Root).Single(n => n.Content == "Hidden").IsVisible);
        Reject(() => next.TranslateAction(SidecarUiForms.Encode(button.Argument!, new Dictionary<string, string>()), owner));
        Reject(() => PluginViewCompiler.Prepare(page, ui, [page]));
        Reject(() => PluginViewCompiler.Prepare(page, ui, [page, target with { Owner = owner with { LoadInstanceId = Guid.NewGuid() } }]));
        Reject(() => PluginViewCompiler.Prepare(page, ui, [page, target with { Page = target.Page with { Schema = Abi100Schemas.TextResult } }]));
        Reject(() => PluginViewCompiler.Prepare(page, ui with { Registrations = ui.Registrations.Add(registration) }, [page, target]));
        Reject(() => PluginViewCompiler.Prepare(page, ui, [page, target with { Model = new AbiValue.Null() }]));
        string privateAlias = view.Model.Keys.First(key => key.StartsWith("nxr_", StringComparison.Ordinal));
        Reject(() => PluginViewCompiler.Prepare(page with { Model = Model((privateAlias, new AbiValue.Utf8String("collision"))) }, ui, [page, target]));
        Reject(() => PluginViewCompiler.Prepare(page, ui with
        {
            Components = ui.Components.SetItem(componentId,
            "<nxml-dynamic><Text Content=\"{data " + privateAlias + "}\"/></nxml-dynamic>")
        }, [page, target]));
        Reject(() => PluginViewCompiler.Prepare(page, ui, [page, target with { Source = "<nxml-page><Page><Button Command=\"ui.host.erase\"/></Page></nxml-page>" }]));
        // Replacements are one hop: a target's own replacement does not recurse into this projection.
        var reverse = registration with { Value = UIViewReplacement.Write(replacement with { Id = new("example.test:reverse"), View = target.Page.Id, Page = page.Page.Id }) };
        Check(PluginViewCompiler.Prepare(page, ui with { Registrations = ui.Registrations.Add(reverse) }, [page, target]).Document.Files[view.Document.EntryPoint]
            == view.Document.Files[view.Document.EntryPoint]);
        Console.WriteLine("PASS: Sidecar whole-page replacement separates render/contribution/action models, preserves model-only source identity and rejects invalid targets.");
    }
}
