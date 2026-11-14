using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Host.Confirmation.Data;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace PluginSidecar.Presentation;

/// <summary>Guest policy stays local; the Host receives only bounded literal decision text.</summary>
public sealed class SidecarActionConfirmation : IActionConfirmation
{
    private readonly SidecarConfirmationClient _client;
    private SidecarActionConfirmation(SidecarHostRequestClient requests, IXsrDispatchObserver observer) => _client = new(requests, observer);

    public static async ValueTask<SidecarActionConfirmation> CreateAsync(SidecarHostRequestClient requests,
        IXsrDispatchObserver observer, CancellationToken token = default)
    {
        var confirmation = new SidecarActionConfirmation(requests, observer);
        var available = await confirmation._client.DiscoverAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!available.IsSuccess) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        return confirmation;
    }

    public async ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string details = PluginConfirmationText.Format(request);
        if (!_client.Queries.TryResolve(SidecarConfirmationRoutes.Ask, out var route)) throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        var result = await _client.Queries.QueryAsync<HostConfirmationRequest, HostConfirmationDecision>(route,
            new("Confirm plugin action", new UTF8Encoding(false, true).GetBytes(details)), cancellationToken: cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (!result.IsSuccess) throw new AbiHostException(result.Error?.Kind == XsrErrorKind.Cancelled ? AbiErrors.CANCELLED : AbiErrors.CAPABILITY_UNAVAILABLE);
        return result.Value.Accepted;
    }
}
