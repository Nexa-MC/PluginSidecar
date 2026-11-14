using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Xsr.Renderer.Nxml;
using PluginSidecar.Application;
using PluginSidecar.Presentation;

internal static partial class PresentationTests
{
    private static void ContributionChecks(PluginIdentity owner)
    {
        var page = Page(owner, "<nxml-page><Page Key=\"Page\"><Text Content=\"Base\" /></Page></nxml-page>",
            Model(("title", new AbiValue.Utf8String("<literal>"))));
        var component = new QualifiedId("example.test:component");
        ulong handle = 1;
        PluginUiRegistration Register(string kind, QualifiedId id, AbiValue value, PluginIdentity? identity = null)
        {
            var who = identity ?? owner;
            return new(who, kind, value, new(handle++, who.PluginId, who.LoadInstanceId, id, RegistrationState.Active));
        }
        var registration = Register("components", component, new AbiValue.Null());
        UIContribution Contribution(string id, UIContributionMode mode, string[]? before = null, string[]? after = null) =>
            new(new(id), page.Page.Id, "content", mode, component, (before ?? []).Select(s => new QualifiedId(s)).ToImmutableArray(),
                (after ?? []).Select(s => new QualifiedId(s)).ToImmutableArray());
        PluginAdvancedUi Snapshot(string source, params UIContribution[] items) => new(
            new[] { registration }.Concat(items.Select(item => Register("contributions", item.Id, UIContribution.Write(item)))).ToImmutableArray(), [],
            ImmutableDictionary<QualifiedId, string>.Empty.Add(component, source));
        string[] Texts(PluginPreparedView view) => Nodes(Compile(view).Root.Root).Where(n => n.Content is not null).Select(n => n.Content!).ToArray();
        const string text = "<nxml-dynamic><Text Content=\"{data title}\" /></nxml-dynamic>";
        foreach (var mode in new[] { UIContributionMode.Append, UIContributionMode.Prepend, UIContributionMode.Hide, UIContributionMode.Replace })
        {
            var view = PluginViewCompiler.Prepare(page, Snapshot(text, Contribution("example.test:add", mode)));
            var expected = mode switch
            {
                UIContributionMode.Append => new[] { "Base", "<literal>" },
                UIContributionMode.Prepend => ["<literal>", "Base"],
                UIContributionMode.Hide => [],
                _ => ["<literal>"]
            };
            Check(Texts(view).SequenceEqual(expected));
        }
        var wrapped = PluginViewCompiler.Prepare(page, Snapshot("<nxml-dynamic><StackPanel><Text Content=\"Prefix\" /></StackPanel></nxml-dynamic>",
            Contribution("example.test:wrap", UIContributionMode.Wrap)));
        Check(Texts(wrapped).SequenceEqual(new[] { "Prefix", "Base" }));
        var hide = Contribution("example.test:z-hide", UIContributionMode.Hide, before: ["example.test:a-add"]);
        var append = Contribution("example.test:a-add", UIContributionMode.Append);
        var ordered = Snapshot(text, append, hide);
        Check(Texts(PluginViewCompiler.Prepare(page, ordered)).SequenceEqual(new[] { "<literal>" }));
        var reordered = ordered with { Registrations = ordered.Registrations.Reverse().ToImmutableArray() };
        var first = PluginViewCompiler.Prepare(page, ordered);
        var second = PluginViewCompiler.Prepare(page, reordered);
        Check(first.Document.Files[first.Document.EntryPoint] == second.Document.Files[second.Document.EntryPoint]);
        Reject(() => PluginViewCompiler.Prepare(page, Snapshot(text, hide, append with { Before = [hide.Id] })));
        Reject(() => PluginViewCompiler.Prepare(page, Snapshot(text, append with { After = [append.Id] })));
        Reject(() => PluginViewCompiler.Prepare(page, Snapshot(text, append with { SemanticNode = "protected" })));
        Reject(() => PluginViewCompiler.Prepare(page, Snapshot(text, append, append)));
        Reject(() => PluginViewCompiler.Prepare(page, Snapshot(text, append with { Mode = UIContributionMode.Wrap })));
        var missing = Snapshot(text, append);
        Reject(() => PluginViewCompiler.Prepare(page, missing with { Registrations = missing.Registrations.Remove(registration) }));
        var foreign = registration with { Owner = owner with { LoadInstanceId = Guid.NewGuid() } };
        Reject(() => PluginViewCompiler.Prepare(page, missing with { Registrations = missing.Registrations.Replace(registration, foreign) }));
        Reject(() => PluginViewCompiler.Prepare(page, Snapshot("<nxml-dynamic><Button Command=\"ui.host.erase\" /></nxml-dynamic>", append)));
        const string action = "<nxml-dynamic><StackPanel><TextInput Key=\"draft\" Label=\"Draft\"/><Button Command=\"ui.plugin.action\" Argument=\"example.test:save\"/></StackPanel></nxml-dynamic>";
        var actionable = PluginViewCompiler.Prepare(page, Snapshot(action, append));
        var nodes = Nodes(Compile(actionable).Root.Root).ToArray();
        var button = nodes.Single(n => n.Command is not null);
        var input = nodes.Single(n => n.Recipe == Nexa.Xsr.Renderer.Nxml.NxmlRuntimeRecipe.TextInput);
        var translated = actionable.TranslateAction(Nexa.Sidecar.Protocol.SidecarUiForms.Encode(button.Argument!,
            new Dictionary<string, string> { [input.Key!] = "typed" }), owner);
        Check(((AbiValue.Utf8String)((AbiValue.Map)translated.Inputs).Value["draft"]).Value == "typed");
        Console.WriteLine("PASS: Sidecar page contributions preserve composition order, models and form actions, and reject cycles, retired/foreign components and Host escapes.");
    }
}
