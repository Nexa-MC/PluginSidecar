using System.Text;

namespace PluginSidecar.Business;

public sealed partial class PluginBusinessRoutes
{
    private static bool LoginBusy(AccountLoginSnapshot state) => state.Phase is AccountLoginPhase.Starting or AccountLoginPhase.AwaitingAuthorization
        or AccountLoginPhase.ChoosingProfile or AccountLoginPhase.Saving;
    private static string DisplayName(ResourceProject project) => string.IsNullOrWhiteSpace(project.ChineseName) ? project.Title : project.ChineseName;
    private static string DisplayName(MinecraftLibraryDirectory directory)
    {
        string official = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft")
            : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "minecraft")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft");
        if (Nexa.Core.PathIdentity.Comparer.Equals(Nexa.Core.PathIdentity.Normalize(directory.Path), Nexa.Core.PathIdentity.Normalize(official))) return "官方文件夹";
        if (!string.IsNullOrWhiteSpace(directory.Name)) return directory.Name;
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory.Path));
        return leaf.Length == 0 ? directory.Path : leaf;
    }
    private MinecraftInstanceDescriptor? SelectedLibraryInstance()
    { var library = Library(); return library.Instances.FirstOrDefault(instance => instance.Id == library.SelectedInstanceId); }
    private static IReadOnlyList<string> ArgumentRows(SettingsEffectiveValue value)
    {
        if (value.Key is not ("game.jvm" or "game.arguments")) return [];
        List<string> rows = []; StringBuilder item = new(); bool quoted = false;
        foreach (char character in value.Value.Value ?? "")
        {
            if (character == '"') quoted = !quoted;
            if (char.IsWhiteSpace(character) && !quoted)
            { if (item.Length > 0) { rows.Add(item.ToString()); item.Clear(); } }
            else item.Append(character);
        }
        if (item.Length > 0) rows.Add(item.ToString());
        return rows;
    }
}
