using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using Nexa.Xsr.Renderer.Nxml;
using PluginSidecar.Application;
using PluginSidecar.Presentation;

internal static partial class PresentationTests
{
    private static void DialogCompilerChecks()
    {
        var owner = new PluginIdentity("example.test", "1.0.0", Guid.NewGuid());
        var model = Model(("caption", new AbiValue.Utf8String("dialog model")), ("number", new AbiValue.Int64(42)));
        var page = Page(owner,
            """<nxml-page><Page Key="DialogPage"><StackPanel><Text Content="{data caption}" /><TextInput Key="draft" Label="Draft" /><Button Key="save" Content="Save" Command="ui.plugin.action" Argument="example.test:save" /></StackPanel></Page></nxml-page>""", model);
        var reference = new UIDialogRef(new(Guid.NewGuid().ToString("N")), Guid.NewGuid());
        var dialog = new PluginDialog(owner, reference, page);
        var ui = new PluginAdvancedUi([], [dialog], ImmutableDictionary<QualifiedId, string>.Empty);
        var modal = PluginDialogCompiler.Prepare(dialog, ui, [page], "epoch");
        var ordinary = PluginViewCompiler.Prepare(page, ui, [page], "epoch");
        Check(modal.DialogReference == reference && !modal.IsWorkspace && modal.Page == page.Page.Id);
        Check(modal.Document.Operation == SidecarNxmlOperation.Dialog && ordinary.Document.Operation == SidecarNxmlOperation.Page);
        Check(modal.Document.Target != ordinary.Document.Target);
        Check(SidecarNxmlDocument.Decode(SidecarNxmlDocument.Encode(modal.Document)).Operation == SidecarNxmlOperation.Dialog);
        var nodes = Nodes(Compile(modal).Root.Root).ToArray();
        Check(modal.Document.Target == "#" + Compile(modal).Root.Root.Key);
        byte[] form = SidecarUiForms.Encode(nodes.Single(node => node.Command is not null).Argument!,
            new Dictionary<string, string> { [nodes.Single(node => node.Recipe == NxmlRuntimeRecipe.TextInput).Key!] = "modal draft" });
        var action = modal.TranslateAction(form, owner);
        Check(action.Action.Value == "example.test:save");
        var fields = ((AbiValue.Map)action.Inputs).Value;
        Check(((AbiValue.Utf8String)fields["draft"]).Value == "modal draft" && ((AbiValue.Int64)fields["number"]).Value == 42);
        Check(((AbiValue.Utf8String)fields["caption"]).Value == "dialog model");
        Reject(() => ordinary.TranslateAction(form, owner));
        Reject(() => modal.TranslateAction(form, owner with { LoadInstanceId = Guid.NewGuid() }));
        foreach (var other in new[]
        {
            dialog with { Reference = reference with { Id = new(Guid.NewGuid().ToString("N")) } },
            dialog with { Reference = reference with { ViewScope = Guid.NewGuid() } }
        })
        {
            var changed = PluginDialogCompiler.Prepare(other, ui, [page], "epoch");
            Check(modal.Document.Target != changed.Document.Target);
            Reject(() => changed.TranslateAction(form, owner));
        }
        var updated = PluginDialogCompiler.Prepare(dialog with
        {
            Page = page with
            { Model = Model(("caption", new AbiValue.Utf8String("updated dialog")), ("number", new AbiValue.Int64(43))), Revision = new("2") }
        }, ui, [page], "epoch");
        Check(updated.Document.Target == modal.Document.Target && updated.Document.Files[updated.Document.EntryPoint] == modal.Document.Files[modal.Document.EntryPoint]);
        Check(Nodes(Compile(updated).Root.Root).Any(node => node.Content == "updated dialog"));
        Check(((AbiValue.Int64)((AbiValue.Map)updated.TranslateAction(form, owner).Inputs).Value["number"]).Value == 43);
        var renewed = PluginDialogCompiler.Prepare(dialog, ui, [page], "new epoch");
        Reject(() => renewed.TranslateAction(form, owner));
        Reject(() => PluginDialogCompiler.Prepare(dialog with { Owner = owner with { LoadInstanceId = Guid.NewGuid() } }, ui, [page]));
        Reject(() => PluginDialogCompiler.Prepare(dialog with { Reference = reference with { ViewScope = Guid.Empty } }, ui, [page]));
        Reject(() => PluginDialogCompiler.Prepare(dialog with { Reference = reference with { Id = null! } }, ui, [page]));
        Reject(() => PluginDialogCompiler.Prepare(dialog with { Page = page with { Source = "<nxml-page><Page><Button Command=\"ui.host.erase\" /></Page></nxml-page>" } }, ui, [page]));
        var target = Page(owner, page.Source, Model(("caption", new AbiValue.Utf8String("replacement view"))), "example.test:replacement");
        var replacement = new UIViewReplacement(new("example.test:replace"), page.Page.Id, page.Page.Schema, target.Page.Id);
        var component = new QualifiedId("example.test:extra");
        var contribution = new UIContribution(new("example.test:append"), page.Page.Id, "content", UIContributionMode.Append, component, [], []);
        var composedUi = new PluginAdvancedUi(
        [
            new(owner, "replacements", UIViewReplacement.Write(replacement), new(1, owner.PluginId, owner.LoadInstanceId, replacement.Id, RegistrationState.Active)),
            new(owner, "components", new AbiValue.Null(), new(2, owner.PluginId, owner.LoadInstanceId, component, RegistrationState.Active)),
            new(owner, "contributions", UIContribution.Write(contribution), new(3, owner.PluginId, owner.LoadInstanceId, contribution.Id, RegistrationState.Active))
        ], [dialog], ImmutableDictionary<QualifiedId, string>.Empty.Add(component, "<nxml-dynamic><Text Content=\"{data caption}\" /></nxml-dynamic>"));
        var composed = PluginDialogCompiler.Prepare(dialog, composedUi, [page, target], "epoch");
        var rendered = Nodes(Compile(composed).Root.Root).ToArray();
        Check(rendered.Any(node => node.Content == "replacement view") && rendered.Any(node => node.Content == "dialog model"));
        var composedAction = composed.TranslateAction(SidecarUiForms.Encode(rendered.Single(node => node.Command is not null).Argument!, new Dictionary<string, string>()), owner);
        Check(((AbiValue.Utf8String)((AbiValue.Map)composedAction.Inputs).Value["caption"]).Value == "dialog model");
        Check(composed.DialogReference == reference && composed.Document.Operation == SidecarNxmlOperation.Dialog);
        Reject(() => PluginDialogCompiler.Prepare(dialog, composedUi, [page], "epoch"));
        Console.WriteLine("PASS: modal projections isolate exact dialog references, retain typed action models and preserve model-only source identity.");
    }
}
