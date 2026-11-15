using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using Nexa.Xsr.Renderer;
using Nexa.Xsr.Renderer.Nxml;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

/// <summary>Guest policy and identity translation, owned by Sidecar rather than Desktop.</summary>
public static class PluginViewCompiler
{
    public const string ActionCommand = "nexa.plugins.ui.action";
    public const string DrawingInputCommand = "nexa.plugins.ui.drawing_input";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static void ValidatePage(string source) => ValidateSource(source, "nxml-page");
    public static void ValidateComponent(string source) => ValidateSource(source, "nxml-dynamic");

    private static void ValidateSource(string source, string root)
    {
        if (Utf8.GetByteCount(source) > 256 * 1024) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        var document = NxmlParser.Parse(source);
        if (document.Root.Name != root) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        Visit(document.Root);
        static void Visit(NxmlElement node)
        {
            if (node.Name == "Image" || node.Attributes.Any(a => a.Value.Kind == NxmlValueKind.StateBinding
                || a.Name is "ScrollCommand" or "DismissCommand"
                || a.Name == "Command" && (a.Value.Kind != NxmlValueKind.Literal || a.Value.Text != "ui.plugin.action")))
                throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
            foreach (var child in node.Children) Visit(child);
        }
    }

    public static PluginPreparedView Prepare(PluginPage page, PluginAdvancedUi contributions, string publicationEpoch = "")
        => Prepare(page, contributions, [page], publicationEpoch);

    public static PluginPreparedView Prepare(PluginPage page, PluginAdvancedUi contributions,
        IReadOnlyList<PluginPage> activePages, string publicationEpoch = "")
    {
        var (rendered, replacement) = PluginPageReplacements.Compose(page, contributions, activePages);
        var prepared = Prepare(page, PluginPageContributions.Compose(rendered, contributions, page.Model), publicationEpoch);
        prepared.ContributionStamp = PluginPageContributions.Stamp(page.Owner, contributions);
        prepared.ReplacementDependency = replacement;
        return prepared;
    }

    public static PluginPreparedView Prepare(PluginPage page, string publicationEpoch = "")
        => Prepare(page, page, publicationEpoch);

    private static PluginPreparedView Prepare(PluginPage logicalPage, PluginPage page, string publicationEpoch)
    {
        ValidatePage(page.Source);
        var model = ReadModel(page.Model);
        var original = PluginNxmlAssets.Create("page.nxml", new Dictionary<string, string> { ["page.nxml"] = page.Source }).Compile(model);
        if (original.Root.Root.Role != XsrUiSemanticRole.Page) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        ValidateCompiled(original.Root.Root, "ui.plugin.action");
        string scope = "Plugin_" + Hash(page.Owner.PluginId + ":" + page.Owner.LoadInstanceId.ToString("N") + ":" + page.Page.Id.Value + ":" + publicationEpoch);
        string aliasPrefix = "nx_" + scope + ".";
        if (model.Keys.Any(key => key.StartsWith(aliasPrefix, StringComparison.Ordinal))) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        Dictionary<string, string> inputs = new(StringComparer.Ordinal), actions = new(StringComparer.Ordinal);
        Dictionary<string, string> keys = new(StringComparer.Ordinal);
        CollectInputs(original.Root.Root);
        string ScopedKey(string key) => key == original.Root.Root.Key ? scope : "PluginKey_" + Hash(scope + "\0" + key);
        void CollectInputs(NxmlIrNode node)
        {
            if (!string.IsNullOrEmpty(node.Key) && !keys.TryAdd(node.Key, ScopedKey(node.Key)))
                throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            if (node.Recipe == NxmlRuntimeRecipe.TextInput)
            {
                if (string.IsNullOrWhiteSpace(node.Key) || node.Key.Length > 256 || node.Key.Any(char.IsControl)
                    || inputs.Count >= 128 || !inputs.TryAdd(ScopedKey(node.Key), node.Key)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            }
            foreach (var child in node.Children) CollectInputs(child);
        }
        string AddAlias(string purpose, NxmlValue originalValue, string value)
        {
            if (originalValue.Kind == NxmlValueKind.Literal) return value;
            string name = aliasPrefix + Hash(purpose + ":" + originalValue.Text)[..32];
            model[name] = value; return "{data " + name + "}";
        }
        string Bound(NxmlValue value) => value.Kind switch
        {
            NxmlValueKind.Literal => value.Text,
            NxmlValueKind.DataBinding => model.TryGetValue(value.Text, out var text) ? text : throw new AbiHostException(AbiErrors.SCHEMA_INVALID),
            _ => throw new AbiHostException(AbiErrors.CAPABILITY_DENIED)
        };
        string Value(NxmlValue value) => value.Kind == NxmlValueKind.DataBinding ? "{data " + value.Text + "}" : value.Text;
        var parsed = NxmlParser.Parse(page.Source);
        var output = new StringBuilder();
        using var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false });
        Write(parsed.Root, false); writer.Flush();
        void Write(NxmlElement node, bool pageRoot)
        {
            writer.WriteStartElement(node.Name);
            bool actionNode = node.FindProperty("Command") is not null;
            string? token = null;
            if (actionNode)
            {
                string action = node.FindProperty("Argument") is { } argument ? Bound(argument) : "";
                token = "a_" + Hash(scope + "\0" + action);
                if (action.StartsWith(page.Owner.PluginId + ":", StringComparison.Ordinal)) actions[token] = action;
            }
            foreach (var property in node.Attributes)
            {
                if (pageRoot && property.Name == "Key") continue;
                string value = property.Name switch
                {
                    "Key" or "AnchorKey" or "SegmentThumbKey" => AddAlias("key", property.Value, ScopedKey(Bound(property.Value))),
                    "Command" => ActionCommand,
                    "Argument" when actionNode => AddAlias("action", property.Value, token!),
                    _ => Value(property.Value)
                };
                writer.WriteAttributeString(property.Name, value);
            }
            if (pageRoot) writer.WriteAttributeString("Key", scope);
            if (actionNode && node.FindProperty("Argument") is null) writer.WriteAttributeString("Argument", token!);
            foreach (var child in node.Children) Write(child, node.Name == "nxml-page");
            writer.WriteEndElement();
        }
        string source = output.ToString();
        var files = new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["page.nxml"] = source });
        var compiled = PluginNxmlAssets.Create("page.nxml", files).Compile(model);
        ValidateCompiled(compiled.Root.Root, ActionCommand);
        if (compiled.Root.Root.Key != scope || compiled.Root.Root.Role != XsrUiSemanticRole.Page) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        var document = new SidecarNxmlDocument("#" + scope, SidecarNxmlOperation.Page, "page.nxml", files);
        _ = SidecarNxmlDocument.Encode(document);
        return new(logicalPage, document, model, inputs, actions, keys: keys) { PublicationEpoch = publicationEpoch };
    }

    internal static Dictionary<string, string> ReadModel(AbiValue value)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (value is AbiValue.Null) return result;
        if (value is not AbiValue.Map map) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        foreach (var field in map.Value) result.Add(field.Key, field.Value switch
        {
            AbiValue.Utf8String text => text.Value,
            AbiValue.Bool flag => flag.Value ? "true" : "false",
            AbiValue.Int64 number => number.Value.ToString(CultureInfo.InvariantCulture),
            AbiValue.UInt64 number => number.Value.ToString(CultureInfo.InvariantCulture),
            AbiValue.Float64 number => number.Value.ToString("R", CultureInfo.InvariantCulture),
            AbiValue.Null => "",
            _ => throw new AbiHostException(AbiErrors.SCHEMA_INVALID)
        });
        return result;
    }

    internal static void ValidateCompiled(NxmlIrNode node, string command)
    {
        if (node.Bindings.Count != 0 || node.Command is { } action && action.Value != command
            || node.ScrollCommand is not null || node.DismissCommand is not null || node.Recipe == NxmlRuntimeRecipe.Image)
            throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
        foreach (var child in node.Children) ValidateCompiled(child, command);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value))).ToLowerInvariant();
}
