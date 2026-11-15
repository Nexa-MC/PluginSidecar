using System.Globalization;
using System.Text;
using System.Xml;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Xsr.Renderer;
using Nexa.Xsr.Renderer.Nxml;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

/// <summary>Composes ordinary-page content inside the Sidecar, before any Host mutation.</summary>
internal static class PluginPageContributions
{
    internal static string Stamp(PluginIdentity owner, PluginAdvancedUi snapshot) => string.Join(',',
        snapshot.Registrations.Where(r => r.Owner == owner && r.Kind is "components" or "contributions" or "replacements" or "commands")
            .Select(r => r.Registration.Handle).Order().Select(handle => handle.ToString(CultureInfo.InvariantCulture)));

    internal static string Stamp(PluginIdentity owner, PluginAdvancedUi snapshot, QualifiedId view)
    {
        var owned = snapshot.Registrations.Where(item => item.Owner == owner).ToArray();
        var contributions = owned.Where(item => item.Kind == "contributions" && UIContribution.Read(item.Value).View == view).ToArray();
        var components = contributions.Select(item => UIContribution.Read(item.Value).Component).ToHashSet();
        return string.Join(',', contributions.Concat(owned.Where(item =>
                item.Kind == "components" && components.Contains(item.Registration.DefinitionId)
                || item.Kind == "replacements" && UIViewReplacement.Read(item.Value).View == view))
            .Select(item => item.Registration.Handle).Order().Select(handle => handle.ToString(CultureInfo.InvariantCulture)));
    }

    internal static PluginPage Compose(PluginPage page, PluginAdvancedUi snapshot, AbiValue? contributionModel = null)
    {
        var owned = snapshot.Registrations.Where(r => r.Owner == page.Owner).ToArray();
        var items = owned.Where(r => r.Kind == "contributions").Select(r => UIContribution.Read(r.Value))
            .Where(c => c.View == page.Page.Id).ToArray();
        if (items.Length == 0) return page;
        if (items.Length > 256) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        if (items.Select(c => c.Id).Distinct().Count() != items.Length) throw new AbiHostException(AbiErrors.CONFLICT);
        var byId = items.ToDictionary(c => c.Id.Value, StringComparer.Ordinal);
        var edges = byId.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var degrees = byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var before in item.Before) Edge(item.Id.Value, before.Value);
            foreach (var after in item.After) Edge(after.Value, item.Id.Value);
        }
        void Edge(string from, string to)
        {
            // Ordering references outside this target do not participate in its graph.
            if (edges.TryGetValue(from, out var outgoing) && degrees.ContainsKey(to) && outgoing.Add(to)) degrees[to]++;
        }
        var ready = new SortedSet<string>(degrees.Where(p => p.Value == 0).Select(p => p.Key), StringComparer.Ordinal);
        var ordered = new List<UIContribution>(items.Length);
        while (ready.Count != 0)
        {
            string id = ready.Min!; ready.Remove(id); ordered.Add(byId[id]);
            foreach (string next in edges[id]) if (--degrees[next] == 0) ready.Add(next);
        }
        if (ordered.Count != items.Length) throw new AbiHostException(AbiErrors.CONFLICT);
        PluginViewCompiler.ValidatePage(page.Source);
        var syntax = NxmlParser.Parse(page.Source).Root;
        if (syntax.Children.Count != 1) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        var root = syntax.Children[0];
        var model = PluginViewCompiler.ReadModel(page.Model);
        var componentModel = contributionModel is null ? model : PluginViewCompiler.ReadModel(contributionModel);
        var compiled = Compile(page.Source);
        PluginViewCompiler.ValidateCompiled(compiled.Root.Root, "ui.plugin.action");
        foreach (var item in ordered)
        {
            if (item.SemanticNode != "content") throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
            if (!owned.Any(r => r.Kind == "components" && r.Registration.DefinitionId == item.Component)
                || !snapshot.Components.TryGetValue(item.Component, out string? source)) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
            PluginViewCompiler.ValidateComponent(source);
            var fragment = NxmlParser.Parse(source).Root;
            if (fragment.Children.Count != 1) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            var node = fragment.Children[0];
            var part = Compile(source, componentModel);
            PluginViewCompiler.ValidateCompiled(part.Root.Root, "ui.plugin.action");
            var mode = item.Mode switch
            {
                UIContributionMode.Append => NxmlContributionMode.Append,
                UIContributionMode.Prepend => NxmlContributionMode.Prepend,
                UIContributionMode.Hide => NxmlContributionMode.Hide,
                UIContributionMode.Replace => NxmlContributionMode.Replace,
                UIContributionMode.Wrap => NxmlContributionMode.Wrap,
                _ => throw new AbiHostException(AbiErrors.SCHEMA_INVALID)
            };
            compiled = compiled.Compose([new(part, mode)]); // Shared semantic, node and depth checks.
            root = new(root.Name, root.Attributes, mode switch
            {
                NxmlContributionMode.Append => [.. root.Children, node],
                NxmlContributionMode.Prepend => [node, .. root.Children],
                NxmlContributionMode.Hide => [],
                NxmlContributionMode.Replace => [node],
                NxmlContributionMode.Wrap when part.Root.Root.Recipe is NxmlRuntimeRecipe.StackLayout or NxmlRuntimeRecipe.Element
                    => [new(node.Name, node.Attributes, [.. node.Children, .. root.Children])],
                _ => throw new AbiHostException(AbiErrors.SCHEMA_INVALID)
            });
        }
        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true }))
            Write(new(syntax.Name, syntax.Attributes, [root]), writer);
        string result = output.ToString();
        PluginViewCompiler.ValidatePage(result); // Includes the aggregate 256 KiB source limit.
        return page with { Source = result };

        NxmlPreparedDocument Compile(string source, IReadOnlyDictionary<string, string>? data = null) => PluginNxmlAssets.Create("page.nxml",
            new Dictionary<string, string> { ["page.nxml"] = source }).Compile(data ?? model);
    }

    private static void Write(NxmlElement node, XmlWriter writer)
    {
        writer.WriteStartElement(node.Name);
        foreach (var property in node.Attributes)
            writer.WriteAttributeString(property.Name, property.Value.Kind == NxmlValueKind.DataBinding
                ? "{data " + property.Value.Text + "}" : property.Value.Text);
        foreach (var child in node.Children) Write(child, writer);
        writer.WriteEndElement();
    }
}
