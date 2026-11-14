using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

public sealed partial class PluginPagePublisher
{
    private readonly IPluginDialogHost? _modalHost;
    private readonly HashSet<(PluginIdentity Owner, UIDialogRef Reference)> _attemptedDialogs = [];

    private PluginPreparedView[] PrepareDialogs(PluginAdvancedUi ui, PluginPage[] pages, string epoch, bool retain)
    {
        var dialogs = ui.Dialogs.Where(dialog => _application.Runtime.Active.Contains(dialog.Owner))
            .OrderBy(dialog => dialog.Reference.Id.Value, StringComparer.Ordinal).ToArray();
        if (dialogs.Length != 0 && _modalHost is null) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        List<PluginPreparedView> prepared = [];
        foreach (var dialog in dialogs)
        {
            var prior = retain ? _published.SingleOrDefault(view => view.Owner == dialog.Owner && view.DialogReference == dialog.Reference) : null;
            PluginPreparedView candidate;
            try { candidate = PluginDialogCompiler.Prepare(dialog, ui, pages, prior?.PublicationEpoch ?? (retain ? Guid.NewGuid().ToString("N") : epoch)); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                CloseCurrentDialog(dialog.Owner, dialog.Reference);
                throw;
            }
            // A changed source retires this dialog. Never reopen from a state read before a user close.
            if (prior is not null && !SameSource(prior, candidate)) { CloseCurrentDialog(dialog.Owner, dialog.Reference); continue; }
            prepared.Add(candidate);
        }
        return prepared.ToArray();
    }

    private void CloseCurrentDialog(PluginIdentity owner, UIDialogRef reference)
    {
        if (!_application.AdvancedUi().Dialogs.Any(dialog => dialog.Owner == owner && dialog.Reference == reference)) return;
        try { _application.ClosePluginDialog(owner, reference.Id); }
        catch (AbiHostException error) when (error.Code == AbiErrors.HANDLE_INVALID) { /* A concurrent exact-reference close already retired it. */ }
    }

    private async ValueTask ObserveDialogsAsync(CancellationToken token)
    {
        var active = _application.AdvancedUi().Dialogs;
        _attemptedDialogs.RemoveWhere(key => !active.Any(dialog => dialog.Owner == key.Owner && dialog.Reference == key.Reference));
        if (_modalHost is null || _revision is null) return;
        var expected = _documents.Select((view, index) => (View: view as PluginPreparedView, Index: index))
            .Where(item => item.View?.DialogReference is not null).ToArray();
        if (expected.Length == 0) return;
        var reply = await _modalHost.ReadAsync(_revision.Value, token).ConfigureAwait(false);
        if (reply is null || reply.Count > 32) throw new InvalidDataException("Invalid Host modal state.");
        var state = reply.ToArray();
        if (state.Length != expected.Length || state.Any(item => item is null)
            || state.Select(item => item.Document).Distinct().Count() != state.Length
            || state.Any(item => !expected.Any(document => document.Index == item.Document && document.View!.Document.Target == item.Target)))
            throw new InvalidDataException("Host modal state does not match the acknowledged source set.");
        foreach (var item in expected)
        {
            var view = item.View!;
            if (_attemptedDialogs.Contains((view.Owner, view.DialogReference!)) && !state.Single(value => value.Document == item.Index).Open)
                CloseCurrentDialog(view.Owner, view.DialogReference!);
        }
    }

    private async ValueTask OpenNewDialogsAsync(CancellationToken token)
    {
        if (_modalHost is null || _revision is null) return;
        for (int index = 0; index < _documents.Length; index++)
        {
            if (_documents[index] is not PluginPreparedView { DialogReference: { } reference } view || !IsLive(view)) continue;
            if (!_attemptedDialogs.Add((view.Owner, reference))) continue;
            // Attempt history survives a lost reply and source recovery. Reconcile actual state on the next call.
            await _modalHost.SetOpenAsync(_revision.Value, index, true, token).ConfigureAwait(false);
        }
    }
}
