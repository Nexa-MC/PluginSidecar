using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using Nexa.Xsr.Renderer;
using Nexa.Xsr.Renderer.Nxml;
using Nexa.Xsr.State;
using PluginSidecar.Application;
using PluginSidecar.Presentation;

internal static partial class PresentationTests
{
    private static void WorkspaceChecks()
    {
        var owner = new PluginIdentity("example.workspace", "1.0.0", Guid.NewGuid());
        var component = new QualifiedId(owner.PluginId + ":component");
        var contribution = new UIContribution(new(owner.PluginId + ":append"), PluginApplication.WorkspaceView, "content", UIContributionMode.Append, component, [], []);
        var command = new UICommand(new(owner.PluginId + ":command"), new(owner.PluginId + ":echo"), new("Command {data secret}", []));
        PluginUiRegistration Registration(string kind, QualifiedId id, AbiValue value, ulong handle) => new(owner, kind, value,
            new(handle, owner.PluginId, owner.LoadInstanceId, id, RegistrationState.Active));
        var ui = new PluginAdvancedUi([Registration("components", component, new AbiValue.Null(), 1),
            Registration("contributions", contribution.Id, UIContribution.Write(contribution), 2),
            Registration("commands", command.Id, UICommand.Write(command), 3)], [], ImmutableDictionary<QualifiedId, string>.Empty.Add(component,
            "<nxml-dynamic><StackPanel><TextInput Key=\"draft\" Label=\"Draft\"/><Button Content=\"Submit\" Command=\"ui.plugin.action\" Argument=\"example.workspace:echo\"/></StackPanel></nxml-dynamic>"));
        const string target = "#Manager/#Slot";
        var view = PluginWorkspaceCompiler.Prepare(owner, ui, [], target, "first");
        Check(view.IsWorkspace && view.Page == PluginApplication.WorkspaceView && view.Document.Operation == SidecarNxmlOperation.Append);
        Check(view.Document.Target == target && Compile(view).Root.Root.Role != XsrUiSemanticRole.Page);
        var nodes = Nodes(Compile(view).Root.Root).ToArray();
        var submit = nodes.Single(n => n.Content == "Submit");
        var commandButton = nodes.Single(n => n.Content == "Command {data secret}");
        var input = nodes.Single(n => n.Recipe == NxmlRuntimeRecipe.TextInput);
        var draft = new Dictionary<string, string> { [input.Key!] = "private draft" };
        var action = view.TranslateAction(SidecarUiForms.Encode(submit.Argument!, draft), owner);
        Check(((AbiValue.Map)action.Inputs).Value.Count == 1 && ((AbiValue.Utf8String)((AbiValue.Map)action.Inputs).Value["draft"]).Value == "private draft");
        var commandAction = view.TranslateAction(SidecarUiForms.Encode(commandButton.Argument!, draft), owner);
        Check(commandAction.Action == action.Action && ((AbiValue.Map)commandAction.Inputs).Value.IsEmpty);
        Reject(() => view.TranslateAction(SidecarUiForms.Encode(commandButton.Argument!, new Dictionary<string, string> { ["foreign"] = "secret" }), owner));
        // A replacement changes only this load's content; command controls remain outside it.
        var replacementPage = Page(owner, "<nxml-page><Page><Text Content=\"Replacement\"/></Page></nxml-page>", Empty.Write(new()), owner.PluginId + ":replacement");
        var replacement = new UIViewReplacement(new(owner.PluginId + ":replace"), PluginApplication.WorkspaceView, Abi100Schemas.Empty, replacementPage.Page.Id);
        var replacedUi = ui with { Registrations = ui.Registrations.Add(Registration("replacements", replacement.Id, UIViewReplacement.Write(replacement), 4)) };
        var replaced = PluginWorkspaceCompiler.Prepare(owner, replacedUi, [replacementPage], target, "first");
        Check(Nodes(Compile(replaced).Root.Root).Any(n => n.Content == "Replacement") && Nodes(Compile(replaced).Root.Root).Any(n => n.Content == "Command {data secret}"));
        var hiddenUi = ui with { Registrations = ui.Registrations.SetItem(1, Registration("contributions", contribution.Id,
            UIContribution.Write(contribution with { Mode = UIContributionMode.Hide }), 2)) };
        var hidden = PluginWorkspaceCompiler.Prepare(owner, hiddenUi, [], target, "first");
        Check(!Nodes(Compile(hidden).Root.Root).Any(n => n.Content == "Submit") && Nodes(Compile(hidden).Root.Root).Any(n => n.Content == "Command {data secret}"));
        // Each load has a distinct exposure and control namespace, even when its logical view ID is shared.
        var other = owner with { LoadInstanceId = Guid.NewGuid() };
        var otherUi = ui with { Registrations = ui.Registrations.Select(r => r with { Owner = other,
            Registration = r.Registration with { LoadInstanceId = other.LoadInstanceId } }).ToImmutableArray() };
        var second = PluginWorkspaceCompiler.Prepare(other, otherUi, [], target, "first");
        Reject(() => second.TranslateAction(SidecarUiForms.Encode(submit.Argument!, draft), other));
        var tree = new XsrUiTree(); var store = new XsrStateStoreBuilder().Build();
        using var manager = new NxmlDocumentInstance(tree, store, new NxmlExtensionProgram("manager.nxml", new Dictionary<string, string>
        { ["manager.nxml"] = "<nxml-page><Page Key=\"Manager\"><Dynamic Key=\"Slot\"/></Page></nxml-page>" }).Compile(new Dictionary<string, string>()));
        using var coordinator = new NxmlPresentationCoordinator(tree, store);
        Guid firstExposure = Guid.NewGuid(), secondExposure = Guid.NewGuid();
        coordinator.Update([new(firstExposure, target, XsrUiMountOperation.Append, Compile(view)), new(secondExposure, target, XsrUiMountOperation.Append, Compile(second))]);
        var inputs = tree.LiveEntities.Where(node => tree.GetComponent<XsrUiTextInput>(node) is not null).ToArray();
        Check(inputs.Length == 2 && inputs.Select(tree.Name).Distinct().Count() == 2);
        Check(coordinator.TryGetExposure(inputs[0], out var first) && coordinator.TryGetExposure(inputs[1], out var next) && first != next);
        coordinator.Update([new(secondExposure, target, XsrUiMountOperation.Append, Compile(second))]);
        Check(tree.LiveEntities.Count(node => tree.GetComponent<XsrUiTextInput>(node) is not null) == 1);
        Console.WriteLine("PASS: workspace replacement/contribution composition, protected commands with empty inputs and separate mounted form exposures.");
    }
}
