using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using Nexa.Xsr.Renderer.Nxml;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

/// <summary>Compiles one load's workspace into its own form exposure inside the trusted manager.</summary>
public static class PluginWorkspaceCompiler
{
    public static PluginPreparedView Prepare(PluginIdentity owner, PluginAdvancedUi snapshot, IReadOnlyList<PluginPage> activePages,
        string target, string publicationEpoch)
    {
        NxmlPresentationCoordinator.ValidateTarget(target);
        var page = new PluginPage(owner, new(PluginApplication.WorkspaceView, new(Guid.Empty, owner.LoadInstanceId, "package"), Abi100Schemas.Empty, []),
            owner.PluginId, "<nxml-page><Page Key=\"Workspace\"><StackPanel /></Page></nxml-page>", Empty.Write(new()), new("workspace"));
        var view = PluginViewCompiler.Prepare(page, snapshot, activePages, publicationEpoch);
        var commands = snapshot.Registrations.Where(r => r.Owner == owner && r.Kind == "commands")
            .OrderBy(r => r.Registration.DefinitionId.Value, StringComparer.Ordinal).Select(r => UICommand.Read(r.Value)).ToArray();
        if (commands.Length > 256) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        if (commands.Select(c => c.Id).Distinct().Count() != commands.Length) throw new AbiHostException(AbiErrors.CONFLICT);
        var model = new Dictionary<string, string>(view.Model, StringComparer.Ordinal);
        Dictionary<string, string> actions = new(StringComparer.Ordinal);
        var root = NxmlParser.Parse(view.Document.Files[view.Document.EntryPoint]).Root.Children.Single();
        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("nxml-dynamic"); writer.WriteStartElement("StackPanel");
            // Match AsFragment: a page embedded in a slot has stack layout, no page role, height or weight.
            Write(new("StackPanel", root.Attributes.Where(p => p.Name is not ("Role" or "Recipe" or "Height" or "Weight")).ToArray(), root.Children));
            foreach (var command in commands)
            {
                if (!command.Action.Value.StartsWith(owner.PluginId + ":", StringComparison.Ordinal) || !command.Title.Arguments.IsEmpty)
                    throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
                string hash = Hash(owner.PluginId + ":" + owner.LoadInstanceId.ToString("N") + ":" + command.Id.Value + ":" + publicationEpoch);
                string alias = "nxc_" + hash, token = "c_" + hash;
                if (!model.TryAdd(alias, command.Title.MessageKey)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
                actions.Add(token, command.Action.Value);
                writer.WriteStartElement("Button"); writer.WriteAttributeString("Key", "PluginCommand_" + hash);
                writer.WriteAttributeString("Content", "{data " + alias + "}"); writer.WriteAttributeString("Localize", "false");
                writer.WriteAttributeString("Command", PluginViewCompiler.ActionCommand); writer.WriteAttributeString("Argument", token);
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); writer.WriteEndElement();
            void Write(NxmlElement node)
            {
                writer.WriteStartElement(node.Name);
                foreach (var property in node.Attributes) writer.WriteAttributeString(property.Name,
                    property.Value.Kind == NxmlValueKind.DataBinding ? "{data " + property.Value.Text + "}" : property.Value.Text);
                foreach (var child in node.Children) Write(child);
                writer.WriteEndElement();
            }
        }
        string source = output.ToString();
        if (Encoding.UTF8.GetByteCount(source) > 256 * 1024) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        var document = new SidecarNxmlDocument(target, SidecarNxmlOperation.Append, "workspace.nxml",
            new Dictionary<string, string> { ["workspace.nxml"] = source });
        var compiled = PluginNxmlAssets.Create(document.EntryPoint, document.Files).Compile(model);
        PluginViewCompiler.ValidateCompiled(compiled.Root.Root, PluginViewCompiler.ActionCommand);
        return view.AsWorkspace(document, model, actions);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
