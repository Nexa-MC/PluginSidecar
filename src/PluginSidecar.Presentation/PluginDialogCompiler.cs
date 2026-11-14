using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using PluginSidecar.Application;

namespace PluginSidecar.Presentation;

/// <summary>Scopes a guest dialog independently from its ordinary page and other dialog instances.</summary>
public static class PluginDialogCompiler
{
    public static PluginPreparedView Prepare(PluginDialog dialog, PluginAdvancedUi contributions,
        IReadOnlyList<PluginPage> activePages, string publicationEpoch = "")
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (dialog.Page is null || dialog.Owner != dialog.Page.Owner || dialog.Reference is null || dialog.Reference.Id is null
            || string.IsNullOrWhiteSpace(dialog.Reference.Id.Value) || dialog.Reference.Id.Value.Length > 256
            || dialog.Reference.ViewScope == Guid.Empty)
            throw new AbiHostException(AbiErrors.HANDLE_INVALID);
        string scopedEpoch = publicationEpoch + ":dialog:" + dialog.Reference.Id.Value + ":" + dialog.Reference.ViewScope.ToString("N");
        var prepared = PluginViewCompiler.Prepare(dialog.Page, contributions, activePages, scopedEpoch).AsDialog(dialog.Reference, publicationEpoch);
        prepared.ContributionStamp = PluginPageContributions.Stamp(dialog.Owner, contributions, dialog.Page.Page.Id);
        return prepared;
    }
}
