using System.Text.Json;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Host;
using Nexa.Xsr;
using Data = Nexa.Sidecar.Host.ThemeOptions.Data;

namespace PluginSidecar.Business;

public sealed record PluginThemeOptionsEffects(
    Func<ThemeOptionsSaveCommand, CancellationToken, Task<XsrResult>> Save,
    Func<ThemeOptionsResetCommand, CancellationToken, Task<XsrResult>> Reset,
    Func<ThemeOptionsImportCommand, CancellationToken, Task<XsrResult>> Import);

/// <summary>Adapts local theme documents to the general Host's typed configuration commands.</summary>
public static class SidecarThemeOptionsEffects
{
    /// <summary>Discover the client before use. Import documents are parsed locally and never sent as JSON commands.</summary>
    public static PluginThemeOptionsEffects Create(SidecarThemeOptionsClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Task<XsrResult> Send<T>(XsrSemanticId name, T input, CancellationToken token) where T : notnull
        {
            token.ThrowIfCancellationRequested();
            return client.Commands.TryResolve(name, out var route)
                ? client.Commands.Dispatch(route, input, cancellationToken: token).Completion
                : throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        }
        return new((input, token) => Send(Data.SidecarThemeOptionsRoutes.Save,
                new Data.ThemeOptionsSaveCommand(input.SourceIdentity, input.Values.ToDictionary(pair => pair.Key,
                    pair => new Data.ThemeOptionValue((Data.ThemeOptionKind)pair.Value.Kind, pair.Value.Value, pair.Value.Values?.ToArray()), StringComparer.Ordinal), input.ExpectedRevision), token),
            (input, token) => Send(Data.SidecarThemeOptionsRoutes.Reset, new Data.ThemeOptionsResetCommand(input.SourceIdentity, input.ExpectedRevision), token),
            (input, token) =>
            {
                token.ThrowIfCancellationRequested();
                var document = Parse(input.Json);
                return Send(Data.SidecarThemeOptionsRoutes.Import, new Data.HostThemeOptionsImport(input.SourceIdentity,
                    input.ExpectedRevision, document.Id, document.Version, document.Values), token);
            });
    }

    private static (string Id, int Version, IReadOnlyDictionary<string, Data.ThemeOptionValue> Values) Parse(string json)
    {
        if (json is null) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        if (json.Length > 1024 * 1024) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            Fields(root, "format", "themeId", "schemaVersion", "values");
            if (Text(root.GetProperty("format")) != "NTP-options/1") throw new InvalidDataException();
            string id = Text(root.GetProperty("themeId"));
            int version = root.GetProperty("schemaVersion").GetInt32();
            var values = root.GetProperty("values");
            if (values.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            Dictionary<string, Data.ThemeOptionValue> result = new(StringComparer.Ordinal);
            foreach (var entry in values.EnumerateObject())
            {
                if (result.Count >= 256 || entry.Name.Length is < 1 or > 128) throw new InvalidDataException();
                Fields(entry.Value, "kind", "value", "files");
                if (!Enum.TryParse<Data.ThemeOptionKind>(Text(entry.Value.GetProperty("kind")), out var kind) || !Enum.IsDefined(kind)) throw new InvalidDataException();
                string value = Text(entry.Value.GetProperty("value"));
                string[]? files = null;
                if (entry.Value.TryGetProperty("files", out var list))
                {
                    if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 256) throw new InvalidDataException();
                    files = list.EnumerateArray().Select(Text).ToArray();
                }
                if (!result.TryAdd(entry.Name, new(kind, value, files))) throw new InvalidDataException();
            }
            return (id, version, result);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new AbiHostException(AbiErrors.SCHEMA_INVALID); }
    }
    private static string Text(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: <= 4096 } value || value.Contains('\0'))
            throw new InvalidDataException();
        return value;
    }
    private static void Fields(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !names.Add(field.Name)) throw new InvalidDataException();
    }
}
