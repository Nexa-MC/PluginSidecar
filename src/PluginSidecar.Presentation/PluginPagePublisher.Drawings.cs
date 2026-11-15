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
    private readonly IPluginRasterHost? _rasterHost;
    private readonly PluginDrawingRenderer _drawingRenderer;
    private readonly Dictionary<EntityId, (PluginDrawing Drawing, PluginRasterImage Image)> _renderedDrawings = [];
    private readonly Dictionary<string, DrawingInput> _drawingInputs = new(StringComparer.Ordinal);
    private PluginRasterBinding[]? _rasterBindings;
    private sealed record DrawingInput(PluginPreparedView View, DrawingSurfaceRef Surface, string Slot);

    private async ValueTask SynchronizeDrawingsAsync(CancellationToken token)
    {
        if (_revision is null) return;
        var drawings = _application.Drawings();
        if (_rasterHost is null)
        {
            if (drawings.Length != 0) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
            return;
        }
        try
        {
            List<PluginRasterBinding> bindings = [];
            Dictionary<string, DrawingInput> inputs = new(StringComparer.Ordinal);
            HashSet<EntityId> eligible = [];
            foreach (var drawing in drawings.OrderBy(item => item.Surface.Id.Value, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                string local = drawing.Name.Value[(drawing.Name.Value.IndexOf(':') + 1)..];
                var views = _published.Where(view => view.Owner == drawing.Owner && !view.IsWorkspace && IsLive(view)
                    && (view.DialogReference?.ViewScope ?? view.Owner.LoadInstanceId) == drawing.Surface.ViewScope
                    && view.ScopedKeys.ContainsKey(local)).ToArray();
                if (views.Length != 1) continue;
                var view = views[0]; string slot = view.ScopedKeys[local];
                if (!_renderedDrawings.TryGetValue(drawing.Surface.Id, out var cached) || cached.Drawing != drawing)
                {
                    var image = _drawingRenderer.Render(drawing, token);
                    if (!IsLive(view) || !_application.Drawings().Contains(drawing)) continue;
                    cached = (drawing, image); _renderedDrawings[drawing.Surface.Id] = cached;
                }
                eligible.Add(drawing.Surface.Id);
                string identity = drawing.Owner.PluginId + ":" + drawing.Owner.LoadInstanceId.ToString("N") + ":" + drawing.Surface.Id.Value
                    + ":" + drawing.Surface.ViewScope.ToString("N") + ":" + view.Page.Value + ":" + view.PublicationEpoch + ":" + slot;
                string argument = "di_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
                inputs.Add(argument, new(view, drawing.Surface, slot));
                bindings.Add(new(Array.IndexOf(_documents, view), slot, drawing.Width, drawing.Height, cached.Image, argument));
            }
            foreach (var id in _renderedDrawings.Keys.Where(id => !eligible.Contains(id)).ToArray()) _renderedDrawings.Remove(id);
            var next = bindings.ToArray();
            if (_rasterBindings is null || !_rasterBindings.SequenceEqual(next))
            {
                _drawingInputs.Clear(); _rasterBindings = null;
                await _rasterHost.ReplaceAsync(_revision.Value, next, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            _drawingInputs.Clear();
            foreach (var pair in inputs) _drawingInputs.Add(pair.Key, pair.Value);
            _rasterBindings = next;
        }
        catch { _drawingInputs.Clear(); _rasterBindings = null; _renderedDrawings.Clear(); throw; }
    }

    /// <summary>Accepts only native surface observations, never ordinary form drafts or action inputs.</summary>
    public async ValueTask ForwardDrawingInputAsync(ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
        await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            var form = SidecarUiForms.Decode(payload);
            if (!_drawingInputs.TryGetValue(form.Argument, out var input) || !IsLive(input.View)
                || !_published.Any(view => view.Owner == input.View.Owner && view.Page == input.View.Page
                    && view.DialogReference == input.View.DialogReference && view.PublicationEpoch == input.View.PublicationEpoch
                    && view.ScopedKeys.Values.Contains(input.Slot, StringComparer.Ordinal))
                || !_application.Drawings().Any(drawing => drawing.Owner == input.View.Owner && drawing.Surface.Id == input.Surface.Id
                    && drawing.Surface.ViewScope == input.Surface.ViewScope))
                throw new AbiHostException(AbiErrors.HANDLE_INVALID);
            if (form.Fields.Length is not (5 or 6)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            var fields = form.Fields.ToDictionary(field => field.Key, field => new UTF8Encoding(false, true).GetString(field.Utf8Value), StringComparer.Ordinal);
            if (fields.Count == 6 && (!fields.TryGetValue("gap", out var gapText) || gapText is not ("true" or "false")))
                throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            if (!fields.TryGetValue("kind", out var kindText) || !Enum.TryParse<InputEventKind>(kindText, out var kind)
                || !Enum.IsDefined(kind) || kind.ToString() != kindText
                || !fields.TryGetValue("phase", out var phase) || phase.Length > 64
                || !fields.TryGetValue("x", out var xText) || !double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.IsFinite(x)
                || !fields.TryGetValue("y", out var yText) || !double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) || !double.IsFinite(y)
                || !fields.TryGetValue("text", out var text) || text.Length > 4096)
                throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            stop.Token.ThrowIfCancellationRequested();
            _application.DrawingInput(input.Surface.Id, kind, phase, x, y, text, fields.GetValueOrDefault("gap") == "true");
        }
        finally { _serial.Release(); }
    }
}
