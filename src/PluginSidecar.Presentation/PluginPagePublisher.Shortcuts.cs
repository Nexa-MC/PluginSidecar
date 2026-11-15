using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

public sealed partial class PluginPagePublisher
{
    private readonly IPluginShortcutHost? _shortcutHost;
    private PluginShortcutBinding[]? _shortcutBindings;
    private Dictionary<string, ShortcutAction> _shortcutActions = new(StringComparer.Ordinal);
    private sealed record ShortcutAction(PluginPreparedView View, RegistrationRef Shortcut, RegistrationRef Command, QualifiedId Action);

    private async ValueTask SynchronizeShortcutsAsync(CancellationToken token)
    {
        if (_shortcutHost is null || _revision is null) return;
        try
        {
            var ui = _application.AdvancedUi();
            List<PluginShortcutBinding> bindings = [];
            Dictionary<string, ShortcutAction> actions = new(StringComparer.Ordinal);
            HashSet<string> gestures = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ui.Registrations.Where(item => item.Kind == "shortcuts")
                .OrderBy(item => item.Registration.DefinitionId.Value, StringComparer.Ordinal))
            {
                var shortcut = UIShortcut.Read(item.Value);
                var view = _published.SingleOrDefault(view => view.Owner == item.Owner && view.Page == shortcut.Scope && view.DialogReference is null);
                if (view is null || !IsLive(view)) continue;
                var command = ui.Registrations.SingleOrDefault(command => command.Owner == item.Owner && command.Kind == "commands"
                    && command.Registration.DefinitionId == shortcut.Command);
                if (command is null) continue;
                var action = UICommand.Read(command.Value).Action;
                if (!action.Value.StartsWith(item.Owner.PluginId + ":", StringComparison.Ordinal)) throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
                int document = Array.IndexOf(_documents, view);
                string gesture = shortcut.Gesture.Trim();
                if (!gestures.Add(document.ToString(CultureInfo.InvariantCulture) + ":" + gesture)) continue;
                string identity = item.Owner.PluginId + ":" + item.Owner.LoadInstanceId.ToString("N") + ":" + view.Page.Value + ":"
                    + view.PublicationEpoch + ":" + item.Registration.Handle.ToString(CultureInfo.InvariantCulture) + ":" + command.Registration.Handle.ToString(CultureInfo.InvariantCulture);
                string argument = "sc_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
                actions.Add(argument, new(view, item.Registration, command.Registration, action));
                bindings.Add(new(document, gesture, argument));
            }
            if (bindings.Count > 64) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var next = bindings.ToArray();
            if (_shortcutBindings is null || !_shortcutBindings.SequenceEqual(next))
            {
                _shortcutActions.Clear(); _shortcutBindings = null;
                await _shortcutHost.ReplaceAsync(_revision.Value, next, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            _shortcutActions = actions; _shortcutBindings = next;
        }
        catch { _shortcutActions.Clear(); _shortcutBindings = null; throw; }
    }

    private bool TryTranslateShortcut(SidecarFormSubmission form, out PluginIdentity owner, out ActionRequest request)
    {
        owner = null!; request = null!;
        if (!_shortcutActions.TryGetValue(form.Argument, out var shortcut)) return false;
        if (form.Fields.Length != 0) throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
        var view = shortcut.View;
        if (!IsLive(view) || !_published.Any(current => current.Owner == view.Owner && current.Page == view.Page
            && current.DialogReference is null && current.PublicationEpoch == view.PublicationEpoch))
            throw new AbiHostException(AbiErrors.HANDLE_INVALID);
        var registrations = _application.AdvancedUi().Registrations;
        if (!registrations.Any(item => item.Owner == view.Owner && item.Kind == "shortcuts" && item.Registration == shortcut.Shortcut)
            || !registrations.Any(item => item.Owner == view.Owner && item.Kind == "commands" && item.Registration == shortcut.Command))
            throw new AbiHostException(AbiErrors.HANDLE_INVALID);
        owner = view.Owner;
        request = new(shortcut.Action, new(new("nexa:ui"), new(view.Page.Value), "plugin"), Empty.Write(new()), null, Guid.NewGuid().ToString("N"));
        return true;
    }
}
