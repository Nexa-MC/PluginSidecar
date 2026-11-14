using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using PluginSidecar.Presentation;

internal static partial class PresentationTests
{
    private static async ValueTask<bool> ConfirmThroughHost(ActionConfirmationRequest action, CancellationToken cancellation)
    {
        bool accepted = false, reject = false;
        int asks = 0;
        string expected = PluginConfirmationText.Format(action);
        Check(expected.Contains(action.Action.Id.Value, StringComparison.Ordinal));
        string escaped = PluginConfirmationText.Format(action with { Request = action.Request with { Inputs = Model(("key\nTitle:", new AbiValue.Utf8String("line\nCaller: forged\u202E")), ("number", new AbiValue.UInt64(ulong.MaxValue))) } });
        Check(escaped.Contains("key\\nTitle:", StringComparison.Ordinal) && escaped.Contains("line\\nCaller: forged\\u202E", StringComparison.Ordinal));
        Check(escaped.Contains("18446744073709551615", StringComparison.Ordinal));
        Reject(() => PluginConfirmationText.Format(action with { Request = action.Request with { Inputs = new AbiValue.Utf8String(new string('x', 65536)) } }));
        SidecarHostRequestClient? wire = null;
        using var requests = wire = new((frame, token) =>
        {
            var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
            if (request.ContractId == 1535)
            {
                Check(request.Value.Length == 0); writer.Write(1u);
                Text("05C1E5C77E48F23A3210C86227090656C5E3F3D2773AB2BD2981FB41132C7E36:FD011BD293F84967DAB5A1DD6889322E3D75C4D3B06FC20A8B4B5A1FE81D484C");
                writer.Write(1); writer.Write(1536u);
            }
            else
            {
                Check(request.ContractId == 1536); asks++;
                using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input);
                Check(reader.ReadBoolean());
                Check(Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())) == "Confirm plugin action");
                Check(Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())) == expected);
                Check(input.Position == input.Length);
                writer.Write(!reject);
                if (reject) { writer.Write((int)XsrErrorKind.Rejected); Text("xsr.rejected"); }
                else { writer.Write(true); writer.Write(accepted); }
            }
            Check(wire!.TryAcceptReply(new(SidecarProtocol.Version, SidecarMessageType.QueryResult, SidecarFrameTraits.None,
                frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true, new(SidecarWireCodecs.GeneratedDto, stream.ToArray()), null))));
            return ValueTask.CompletedTask;
        });
        requests.Activate(SidecarFeatures.All);
        var confirmation = await SidecarActionConfirmation.CreateAsync(requests, new Observer(), cancellation);
        Check(!await confirmation.ConfirmAsync(action, cancellation));
        reject = true;
        await RejectedAsync(() => confirmation.ConfirmAsync(action, cancellation).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool wasCancelled = false;
        try { await confirmation.ConfirmAsync(action, cancelled.Token); } catch (OperationCanceledException) { wasCancelled = true; }
        Check(wasCancelled && asks == 2);
        accepted = true; reject = false;
        return await confirmation.ConfirmAsync(action, cancellation);
    }
}
