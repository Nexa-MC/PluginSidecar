using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Xsr.Renderer.Nxml;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

/// <summary>Separates replacement render data from the logical page's contribution and action model.</summary>
internal static class PluginPageReplacements
{
    internal static (PluginPage Rendered, PluginPage? Dependency) Compose(PluginPage page, PluginAdvancedUi snapshot,
        IReadOnlyList<PluginPage> activePages)
    {
        var replacements = snapshot.Registrations.Where(r => r.Owner == page.Owner && r.Kind == "replacements")
            .Select(r => UIViewReplacement.Read(r.Value)).Where(r => r.View == page.Page.Id).ToArray();
        if (replacements.Length == 0) return (page, null);
        if (replacements.Length != 1) throw new AbiHostException(AbiErrors.CONFLICT);
        var replacement = replacements[0];
        var targets = activePages.Where(p => p.Owner == page.Owner && p.Page.Id == replacement.Page).ToArray();
        if (targets.Length != 1) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
        var target = targets[0];
        if (replacement.ViewContract != page.Page.Schema || target.Page.Schema != page.Page.Schema)
            throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
        PluginViewCompiler.ValidatePage(target.Source);
        _ = PluginViewCompiler.ReadModel(page.Model);
        _ = PluginViewCompiler.ReadModel(target.Model);
        var fields = page.Model is AbiValue.Map original ? original.Value.ToBuilder()
            : ImmutableDictionary.CreateBuilder<string, AbiValue>(StringComparer.Ordinal);
        var data = target.Model is AbiValue.Map model ? model.Value : ImmutableDictionary<string, AbiValue>.Empty;
        string prefix = "nxr_" + Hash(page.Page.Id.Value + "\0" + target.Page.Id.Value)[..16] + "_";
        if (fields.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal))) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        string Alias(string name)
        {
            if (!data.TryGetValue(name, out var value)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            string alias = prefix + Hash(name);
            fields[alias] = value;
            return alias;
        }
        string Rewrite(NxmlValue value)
        {
            if (value.Kind == NxmlValueKind.DataBinding) return "{data "
                + (value.Text.StartsWith('!') ? "!" + Alias(value.Text[1..]) : Alias(value.Text)) + "}";
            if (value.Kind != NxmlValueKind.DataText) return value.Text;
            // The canonical parser already validated the placeholder grammar and budgets.
            // Rewrite names only; never parse a model value as markup or a format string.
            var result = new StringBuilder();
            int start = 0;
            while (value.Text.IndexOf("{data ", start, StringComparison.Ordinal) is int open && open >= 0)
            {
                int close = value.Text.IndexOf('}', open);
                result.Append(value.Text.AsSpan(start, open - start)).Append("{data ")
                    .Append(Alias(value.Text[(open + 6)..close])).Append('}');
                start = close + 1;
            }
            return result.Append(value.Text.AsSpan(start)).ToString();
        }
        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            void Write(NxmlElement node)
            {
                writer.WriteStartElement(node.Name);
                foreach (var property in node.Attributes) writer.WriteAttributeString(property.Name, Rewrite(property.Value));
                foreach (var child in node.Children) Write(child);
                writer.WriteEndElement();
            }
            Write(NxmlParser.Parse(target.Source).Root);
        }
        string source = output.ToString();
        PluginViewCompiler.ValidatePage(source);
        return (page with { Source = source, Model = new AbiValue.Map(fields.ToImmutable()) }, target);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
