using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Host.Notifications.Data;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace PluginSidecar.Presentation;

/// <summary>Translates admitted guest notifications to acknowledged, literal Host feedback.</summary>
public sealed class SidecarNotifications
{
    private readonly SidecarNotificationsClient _client;
    private SidecarNotifications(SidecarHostRequestClient requests, IXsrDispatchObserver observer) => _client = new(requests, observer);
    public static async ValueTask<SidecarNotifications> CreateAsync(SidecarHostRequestClient requests,
        IXsrDispatchObserver observer, CancellationToken token = default)
    {
        var notifications = new SidecarNotifications(requests, observer);
        var available = await notifications._client.DiscoverAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!available.IsSuccess) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        return notifications;
    }
    public async ValueTask NotifyAsync(PluginIdentity owner, UINotification notification, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(owner.PluginId) || owner.PluginId.Any(char.IsControl)
            || !notification.Title.Arguments.IsEmpty || !notification.Body.Arguments.IsEmpty
            || notification.Title.MessageKey.Length > 256 || notification.Body.MessageKey.Length > 4096)
            throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        if (notification.Action is not null) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        string title = owner.PluginId + ": " + notification.Title.MessageKey;
        if (title.Length > 4096) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        static bool InvalidText(string text) => text.Any(ch => char.IsControl(ch) && ch is not '\n' and not '\r' and not '\t');
        if (InvalidText(title) || InvalidText(notification.Body.MessageKey)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        if (!_client.Commands.TryResolve(SidecarNotificationsRoutes.Publish, out var route)) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        var result = await _client.Commands.Dispatch(route, new HostNotification(HostNotificationLevel.Info, title, notification.Body.MessageKey),
            cancellationToken: token).Completion.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess) throw new AbiHostException(result.Error?.Kind == XsrErrorKind.Cancelled ? AbiErrors.CANCELLED : AbiErrors.CAPABILITY_UNAVAILABLE);
    }
}
