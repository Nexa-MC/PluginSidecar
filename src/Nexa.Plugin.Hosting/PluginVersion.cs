using Nexa.Plugin.Bootstrap.V1;
namespace Nexa.Plugin.Hosting;

internal sealed record PluginVersion(ulong Major, ulong Minor, ulong Patch, string[] Pre) : IComparable<PluginVersion>
{
    internal static PluginVersion Parse(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256) throw new FormatException("Invalid plugin version.");
        var build = value.Split('+'); if (build.Length > 2 || build.Length == 2 && !Identifiers(build[1], false)) throw new FormatException("Invalid build metadata.");
        int dash = build[0].IndexOf('-'); string core = dash < 0 ? build[0] : build[0][..dash]; string pre = dash < 0 ? "" : build[0][(dash + 1)..];
        if (dash >= 0 && !Identifiers(pre, true)) throw new FormatException("Invalid prerelease.");
        var numbers = core.Split('.'); if (numbers.Length != 3) throw new FormatException("Expected SemVer major.minor.patch.");
        ulong Number(string text) => text.Length > 0 && (text.Length == 1 || text[0] != '0') && text.All(char.IsAsciiDigit) && ulong.TryParse(text, out var number) ? number : throw new FormatException("Invalid version component.");
        return new(Number(numbers[0]), Number(numbers[1]), Number(numbers[2]), pre.Length == 0 ? [] : pre.Split('.'));
    }
    private static bool Identifiers(string text, bool noLeadingNumericZero) => text.Split('.').All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && (!noLeadingNumericZero || p.Length == 1 || p[0] != '0' || !p.All(char.IsAsciiDigit)));
    public int CompareTo(PluginVersion? other)
    {
        if (other is null) return 1;
        int comparison = Major.CompareTo(other.Major); if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor); if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch); if (comparison != 0) return comparison;
        if (Pre.Length == 0 || other.Pre.Length == 0) return Pre.Length == other.Pre.Length ? 0 : Pre.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
        {
            bool leftNumeric = Pre[i].All(char.IsAsciiDigit), rightNumeric = other.Pre[i].All(char.IsAsciiDigit);
            comparison = leftNumeric && rightNumeric ? Pre[i].Length.CompareTo(other.Pre[i].Length) : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : 0;
            if (comparison == 0) comparison = string.CompareOrdinal(Pre[i], other.Pre[i]);
            if (comparison != 0) return comparison;
        }
        return Pre.Length.CompareTo(other.Pre.Length);
    }
    internal static bool Matches(string version, VersionRange range)
    {
        var value = Parse(version);
        return (range.Minimum is null || value.CompareTo(Parse(range.Minimum)) is var minimum && (minimum > 0 || minimum == 0 && range.MinimumInclusive)) &&
            (range.Maximum is null || value.CompareTo(Parse(range.Maximum)) is var maximum && (maximum < 0 || maximum == 0 && range.MaximumInclusive));
    }
}
