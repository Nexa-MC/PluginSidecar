using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Host.FilePicker.Data;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace PluginSidecar.Business;

/// <summary>Native selection only. Guest admission and opaque file grants stay with the business owner.</summary>
public sealed class SidecarFilePicker(SidecarFilePickerClient client)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public async Task<string?> PickAsync(string title, int kind, string[] extensions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (title is null || title.Length > 256 || title.Any(char.IsControl) || kind is < 0 or > 2
            || extensions is null || extensions.Length > 32
            || extensions.Any(extension => extension is null || extension.Length is < 1 or > 32
                || extension.All(character => character == '.')
                || extension.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.')))
            throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        if (!client.Queries.TryResolve(SidecarFilePickerRoutes.Pick, out var route))
            throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        var result = await client.Queries.QueryAsync<HostFilePick, HostFileSelection>(route,
            new((HostFilePickKind)kind, title, extensions.ToArray()), cancellationToken: token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess) throw new AbiHostException(result.Error?.Kind switch
        {
            XsrErrorKind.Cancelled => AbiErrors.CANCELLED,
            XsrErrorKind.TimedOut => AbiErrors.DEADLINE_EXCEEDED,
            _ => AbiErrors.CAPABILITY_UNAVAILABLE
        });
        if (result.Value!.Utf8Path is not { } bytes) return null;
        if (bytes.Length > 4 * 32768) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        string path;
        try { path = Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new AbiHostException(AbiErrors.SCHEMA_INVALID); }
        if (path.Length > 32768 || path.Contains('\0') || !Path.IsPathFullyQualified(path))
            throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        return path;
    }
}
