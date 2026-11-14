using System.Text;
using System.Text.Json;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Host.Confirmation.Data;
using Nexa.Sidecar.Host.FilePicker.Data;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace PluginSidecar.Presentation;

/// <summary>Native developer management through general Host picker and literal confirmation contracts.</summary>
public sealed class SidecarDeveloperPackageInteraction
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly SidecarFilePickerClient _picker;
    private readonly SidecarConfirmationClient _confirmation;
    private SidecarDeveloperPackageInteraction(SidecarHostRequestClient requests, IXsrDispatchObserver observer)
    { _picker = new(requests, observer); _confirmation = new(requests, observer); }

    public static async ValueTask<SidecarDeveloperPackageInteraction> CreateAsync(SidecarHostRequestClient requests,
        IXsrDispatchObserver observer, CancellationToken token = default)
    {
        var interaction = new SidecarDeveloperPackageInteraction(requests, observer);
        var picker = await interaction._picker.DiscoverAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!picker.IsSuccess) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        var confirmation = await interaction._confirmation.DiscoverAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!confirmation.IsSuccess) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        return interaction;
    }

    public async ValueTask<string?> PickAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_picker.Queries.TryResolve(SidecarFilePickerRoutes.Pick, out var route)) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        var result = await _picker.Queries.QueryAsync<HostFilePick, HostFileSelection>(route,
            new(HostFilePickKind.OpenFile, "选择开发者插件包", ["zip"]), cancellationToken: token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess) throw Failure(result.Error);
        if (result.Value!.Utf8Path is not { } bytes) return null;
        if (bytes.Length > 4 * 32768) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        string path;
        try { path = Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new AbiHostException(AbiErrors.SCHEMA_INVALID); }
        if (path.Length > 32768 || path.Contains('\0') || !Path.IsPathFullyQualified(path)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        return path;
    }

    public async ValueTask<PluginInstallDecision> ConfirmAsync(PluginInstallConsent consent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string details = Describe(consent);
        if (!await AskAsync("加载开发者插件", details, token).ConfigureAwait(false)) return PluginInstallDecision.Declined;
        bool remember = await AskAsync("记住插件加载授权", details +
            "\n\n是否在以后启动时，以这些权限自动恢复此插件？仅在开发者模式开启且宿主就绪时恢复。\n确认：记住授权；取消：仅在本次会话加载。", token).ConfigureAwait(false);
        return remember ? PluginInstallDecision.Remember : PluginInstallDecision.SessionOnly;
    }

    private async ValueTask<bool> AskAsync(string title, string details, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_confirmation.Queries.TryResolve(SidecarConfirmationRoutes.Ask, out var route)) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        var result = await _confirmation.Queries.QueryAsync<HostConfirmationRequest, HostConfirmationDecision>(route,
            new(title, Utf8.GetBytes(details)), cancellationToken: token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess) throw Failure(result.Error);
        return result.Value.Accepted;
    }

    private static string Describe(PluginInstallConsent consent)
    {
        if (consent.Id is null || consent.Version is null || consent.Id.Length is < 1 or > 256 || consent.Version.Length is < 1 or > 128 ||
            consent.Permissions.IsDefault || consent.Permissions.Length > 128 ||
            consent.Permissions.Any(permission => permission is null || permission.Length is < 1 or > 256) ||
            consent.Permissions.Distinct(StringComparer.Ordinal).Count() != consent.Permissions.Length)
            throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        var details = new StringBuilder("此开发包未通过插件市场的发布者验证。只加载你信任的插件。\n\n插件：");
        details.Append(Quote(consent.Id)).Append("\n版本：").Append(Quote(consent.Version)).Append("\n请求的权限：");
        if (consent.Permissions.IsEmpty) details.Append("\n（无）");
        foreach (string permission in consent.Permissions) details.Append("\n  ").Append(Quote(permission));
        if (details.Length > 65536) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        details.Append("\n\n是否允许以这些权限加载此插件？");
        return details.ToString();
    }

    private static string Quote(string value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) { writer.WriteStringValue(value); writer.Flush(); }
        return Utf8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static AbiHostException Failure(XsrError? error) => new(error?.Kind switch
    {
        XsrErrorKind.Cancelled => AbiErrors.CANCELLED,
        XsrErrorKind.TimedOut => AbiErrors.DEADLINE_EXCEEDED,
        _ => AbiErrors.CAPABILITY_UNAVAILABLE
    });
}
