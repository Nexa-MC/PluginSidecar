using System.Globalization;
using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;

namespace PluginSidecar.Presentation;

/// <summary>Deterministic literal descriptions; values cannot insert field headings or markup.</summary>
public static class PluginConfirmationText
{
    public static string Format(ActionConfirmationRequest request)
    {
        var text = new StringBuilder();
        Add("Caller: "); Quote(request.Caller.PluginId); Add(" @ "); Quote(request.Caller.PluginVersion);
        Add("\nProvider: "); Quote(request.Provider.PluginId); Add(" @ "); Quote(request.Provider.PluginVersion);
        Add("\nAction: "); Quote(request.Action.Id.Value);
        Add("\nTitle: "); Quote(request.Action.Title.MessageKey);
        foreach (var argument in request.Action.Title.Arguments) { Add("\n  "); Quote(argument.Name); Add(": "); Value(argument.Value, 0); }
        Add("\nTarget: "); Quote(request.Request.Target.Kind.Value); Add(" / "); Quote(request.Request.Target.Entity?.Value ?? "");
        Add(" [scope="); Quote(request.Request.Target.Scope); Add("]");
        Add("\nInputs: "); Value(request.Request.Inputs, 0);
        return text.ToString();

        void Add(string value)
        {
            if (value.Length > 65536 - text.Length) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            text.Append(value);
        }
        void Quote(string value)
        {
            Add("\"");
            foreach (char character in value)
                Add(character switch
                {
                    '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                    _ when char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format => "\\u" + ((int)character).ToString("X4", CultureInfo.InvariantCulture),
                    _ => character.ToString()
                });
            Add("\"");
        }
        void Value(AbiValue value, int depth)
        {
            if (depth > 16) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            switch (value)
            {
                case AbiValue.Null: Add("null"); break;
                case AbiValue.Bool item: Add(item.Value ? "true" : "false"); break;
                case AbiValue.Int64 item: Add(item.Value.ToString(CultureInfo.InvariantCulture)); break;
                case AbiValue.UInt64 item: Add(item.Value.ToString(CultureInfo.InvariantCulture)); break;
                case AbiValue.Float64 item: Add(item.Value.ToString("R", CultureInfo.InvariantCulture)); break;
                case AbiValue.Utf8String item: Quote(item.Value); break;
                case AbiValue.Bytes item:
                    if (item.Value.Length > (65536 - text.Length) / 2) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
                    Add("hex:"); Add(Convert.ToHexString(item.Value.AsSpan())); break;
                case AbiValue.Array array:
                    Add("["); bool firstItem = true;
                    foreach (var item in array.Value) { if (!firstItem) Add(", "); firstItem = false; Value(item, depth + 1); }
                    Add("]"); break;
                case AbiValue.Map map:
                    Add("{"); bool firstField = true;
                    foreach (var field in map.Value.OrderBy(field => field.Key, StringComparer.Ordinal))
                    { if (!firstField) Add(", "); firstField = false; Quote(field.Key); Add(": "); Value(field.Value, depth + 1); }
                    Add("}"); break;
                default: throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            }
        }
    }
}
