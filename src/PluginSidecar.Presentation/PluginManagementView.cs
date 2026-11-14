using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using Nexa.Xsr.Renderer.Nxml;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

public sealed record PluginManagementPolicy(bool DeveloperMode, bool SafeMode, bool EngineReady, bool Busy, string Status)
{
    public bool CanImport => DeveloperMode && !SafeMode && EngineReady && !Busy;
}

/// <summary>Trusted Worker effects. Implementations own package consent and offline-installation changes.</summary>
public interface IPluginManagementActions
{
    ValueTask ImportDeveloperPackageAsync(CancellationToken token);
    ValueTask DisableAsync(PluginIdentity exactLoad, CancellationToken token);
}

public sealed record PluginManagementLabels(string Title = "插件", string Import = "加载开发包", string Disable = "停用");
public sealed record PluginManagementOptions(Func<PluginManagementPolicy> ReadPolicy, IPluginManagementActions Actions,
    PluginManagementLabels? Labels = null, string NavigationAnchor = "#NavigationSettings");

internal enum PluginManagementActionKind { Import, Disable, OpenPage }
internal sealed record PluginManagementAction(PluginManagementActionKind Kind, PluginIdentity? Owner = null, QualifiedId? Page = null);

/// <summary>Sidecar controls and navigation, outside all guest replacement and contribution targets.</summary>
internal sealed class PluginManagementView : IPluginHostView
{
    internal const string Command = "nexa.plugins.manage";
    private readonly Dictionary<string, PluginManagementAction> _actions = new(StringComparer.Ordinal);
    public SidecarNxmlDocument Document { get; }
    public IReadOnlyDictionary<string, string> Model { get; }
    internal IPluginHostView Navigation { get; }
    internal string WorkspaceTarget { get; }
    internal bool TryResolve(string token, out PluginManagementAction? action) => _actions.TryGetValue(token, out action);

    internal PluginManagementView(PluginPresentation snapshot, IReadOnlyCollection<PluginIdentity> active,
        IReadOnlyList<PluginPreparedView> pages, PluginManagementPolicy policy, PluginManagementOptions options, string epoch)
    {
        NxmlPresentationCoordinator.ValidateTarget(options.NavigationAnchor);
        var labels = options.Labels ?? new();
        string key = "PluginManager_" + epoch, slot = "PluginWorkspace_" + epoch;
        WorkspaceTarget = "#" + key + "/#" + slot;
        Dictionary<string, string> model = new(StringComparer.Ordinal);
        string Data(string value) { string name = "m" + model.Count; model.Add(name, value); return "{data " + name + "}"; }
        string Action(PluginManagementAction action, string identity)
        {
            string token = "m_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(epoch + ":" + identity))).ToLowerInvariant();
            _actions.Add(token, action); return token;
        }
        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("nxml-page"); writer.WriteStartElement("Page");
            writer.WriteAttributeString("Key", key); writer.WriteAttributeString("Label", Data(labels.Title));
            writer.WriteStartElement("StackPanel"); writer.WriteAttributeString("Scroll", "true");
            writer.WriteAttributeString("Spacing", "16"); writer.WriteAttributeString("Padding", "24");
            Text(labels.Title, heading: true); Text(policy.Status);
            Button(labels.Import, Action(new(PluginManagementActionKind.Import), "import"), policy.CanImport, hideDisabled: true);
            writer.WriteStartElement("Dynamic"); writer.WriteAttributeString("Key", slot); writer.WriteEndElement();
            foreach (var plugin in snapshot.Plugins.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                writer.WriteStartElement("StackPanel"); writer.WriteAttributeString("Spacing", "8"); writer.WriteAttributeString("Padding", "12");
                Text(plugin.Id + " " + plugin.Version); Text(plugin.State + (plugin.Error is null ? "" : " · " + plugin.Error));
                var owner = active.SingleOrDefault(p => p.PluginId == plugin.Id);
                if (owner is not null) Button(labels.Disable, Action(new(PluginManagementActionKind.Disable, owner), "disable:" + owner.LoadInstanceId.ToString("N")), !policy.Busy);
                writer.WriteEndElement();
            }
            foreach (var page in pages)
                Button(snapshot.Pages.Single(p => p.Owner == page.Owner && p.Page.Id == page.Page).Title,
                    Action(new(PluginManagementActionKind.OpenPage, page.Owner, page.Page), "page:" + page.Owner.LoadInstanceId.ToString("N") + ":" + page.Page.Value), !policy.Busy);
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
            void Text(string value, bool heading = false)
            {
                writer.WriteStartElement("Text"); writer.WriteAttributeString("Content", Data(value));
                writer.WriteAttributeString("FontSize", heading ? "24" : "13"); writer.WriteAttributeString("WrapText", "true");
                writer.WriteAttributeString("Height", heading ? "36" : "44");
                writer.WriteAttributeString("Localize", "false"); writer.WriteEndElement();
            }
            void Button(string title, string token, bool enabled, bool hideDisabled = false)
            {
                writer.WriteStartElement("Button"); writer.WriteAttributeString("Content", Data(title));
                writer.WriteAttributeString("Height", "40");
                writer.WriteAttributeString("Localize", "false"); writer.WriteAttributeString("Command", Command);
                writer.WriteAttributeString("Argument", token); writer.WriteAttributeString("Enabled", Data(enabled ? "true" : "false"));
                if (hideDisabled) writer.WriteAttributeString("IsVisible", Data(enabled ? "true" : "false"));
                writer.WriteEndElement();
            }
        }
        Document = new("#" + key, SidecarNxmlOperation.Page, "manager.nxml", new Dictionary<string, string> { ["manager.nxml"] = output.ToString() });
        Model = new ReadOnlyDictionary<string, string>(model);
        _ = new NxmlExtensionProgram(Document.EntryPoint, Document.Files).Compile(model);
        var nav = new SidecarNxmlDocument(options.NavigationAnchor, SidecarNxmlOperation.Before, "navigation.nxml", new Dictionary<string, string>
        {
            ["navigation.nxml"] = "<nxml-dynamic><NavigationItem Key=\"PluginNavigation_" + epoch + "\" Label=\"{data title}\" Content=\"{data title}\" Localize=\"false\" Icon=\"lucide/puzzle\" Command=\"ui.sidecar.page.open\" Argument=\"#" + key + "\" /></nxml-dynamic>"
        });
        var navModel = new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["title"] = labels.Title });
        _ = new NxmlExtensionProgram(nav.EntryPoint, nav.Files).Compile(navModel);
        Navigation = new NavigationView(nav, navModel);
    }

    private sealed record NavigationView(SidecarNxmlDocument Document, IReadOnlyDictionary<string, string> Model) : IPluginHostView;
}
