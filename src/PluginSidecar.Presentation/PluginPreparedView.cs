using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

/// <summary>A locally validated projection. Publication and active-load admission belong to its owner.</summary>
public sealed class PluginPreparedView : IPluginHostView
{
    private readonly PluginPage _page;
    private readonly IReadOnlyDictionary<string, string> _inputs, _actions;
    private readonly IReadOnlyDictionary<string, string> _commands;
    public bool IsWorkspace { get; private init; }
    public PluginIdentity Owner => _page.Owner;
    public QualifiedId Page => _page.Page.Id;
    public Revision Revision => _page.Revision;
    internal string ContributionStamp { get; set; } = "";
    internal PluginPage? ReplacementDependency { get; set; }
    public SidecarNxmlDocument Document { get; }
    public IReadOnlyDictionary<string, string> Model { get; }

    internal PluginPreparedView(PluginPage page, SidecarNxmlDocument document, Dictionary<string, string> model,
        Dictionary<string, string> inputs, Dictionary<string, string> actions, Dictionary<string, string>? commands = null)
    {
        _page = page; Document = document;
        Model = new ReadOnlyDictionary<string, string>(model);
        _inputs = new ReadOnlyDictionary<string, string>(inputs);
        _actions = new ReadOnlyDictionary<string, string>(actions);
        _commands = new ReadOnlyDictionary<string, string>(commands ?? []);
    }

    internal PluginPreparedView AsWorkspace(SidecarNxmlDocument document, Dictionary<string, string> model, Dictionary<string, string> commands)
        => new(_page, document, model, new(_inputs), new(_actions), commands)
        { IsWorkspace = true, ContributionStamp = ContributionStamp, ReplacementDependency = ReplacementDependency };

    internal bool OwnsAction(string token) => _actions.ContainsKey(token) || _commands.ContainsKey(token);

    /// <summary>Call only after resolving this projection in the current published set and active application.</summary>
    public ActionRequest TranslateAction(ReadOnlyMemory<byte> payload, PluginIdentity activeOwner)
    {
        if (activeOwner != Owner) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
        var form = SidecarUiForms.Decode(payload);
        bool command = _commands.TryGetValue(form.Argument, out string? action);
        if (!command && !_actions.TryGetValue(form.Argument, out action)) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
        var fields = _page.Model is AbiValue.Map map ? map.Value.ToBuilder() : ImmutableDictionary.CreateBuilder<string, AbiValue>(StringComparer.Ordinal);
        foreach (var field in form.Fields)
        {
            if (!_inputs.TryGetValue(field.Key, out string? original)) throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
            if (!fields.ContainsKey(original) && fields.Count >= 128) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            fields[original] = new AbiValue.Utf8String(Encoding.UTF8.GetString(field.Utf8Value));
        }
        return new(new(action!), new(new("nexa:ui"), new(Page.Value), "plugin"), command ? Empty.Write(new()) : new AbiValue.Map(fields.ToImmutable()), null, Guid.NewGuid().ToString("N"));
    }
}
