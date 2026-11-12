using System.Collections.ObjectModel;

namespace PluginSidecar.Business;

public static partial class MinecraftLaunchHooks
{
    public static IReadOnlyDictionary<string, string> ParseEnvironment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 32768 || text.Contains('\0'))
            throw new ArgumentException("Environment settings exceed the bounded format.", nameof(text));
        Dictionary<string, string> values = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int separator = line.IndexOf('=');
            string key = separator < 0 ? "" : line[..separator];
            if (key.Length is 0 or > 128 || !(char.IsAsciiLetter(key[0]) || key[0] == '_')
                || key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')
                || line.Contains('\r') || values.Count == 128 || !values.TryAdd(key, line[(separator + 1)..]))
                throw new ArgumentException("Use unique KEY=VALUE lines with ASCII variable names (at most 128).", nameof(text));
        }
        return new ReadOnlyDictionary<string, string>(values);
    }

}
