using System.Text;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Presentation;

internal static partial class PresentationTests
{
    // Frozen catalog fixture. This exercises the released binary client without loading Host services.
    private static async Task HostPort(PluginPreparedView view)
    {
        long revision = 42;
        bool visible = false, reject = false;
        int models = 0;
        SidecarHostRequestClient? wire = null;
        using var requests = wire = new((frame, _) =>
        {
            var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
            using var input = new MemoryStream(request.Value.ToArray());
            using var reader = new BinaryReader(input, Encoding.UTF8);
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            void Text(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
            string ReadText() => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
            void ReadModels()
            {
                Check(reader.ReadInt32() == 1 && reader.ReadBoolean() && reader.ReadInt32() == 0);
                Check(reader.ReadInt32() == view.Model.Count);
                foreach (var field in view.Model.OrderBy(field => field.Key, StringComparer.Ordinal))
                { Check(reader.ReadBoolean()); Check(ReadText() == field.Key); Check(ReadText() == field.Value); }
                models++;
            }
            if (request.ContractId is 1023 or 1279)
            {
                writer.Write(1u);
                Text(request.ContractId == 1023
                    ? "6D34FF5EBE26D69AED55766EB04A20FCCC1077619CD2BA763323433DEA78FC82:C32BE3528BEE01E22C402B7ECD6A18DCA733060FF3F9734192119615BD45972A"
                    : "46D5C780DCD36136E0F3B0EE1E528E820A7FEBA2F80268C114764FDBA3AE59D4:B8AA95C327BBB6A10CC4B651ECC1A25694F31AD8CE554062803110CE1E9E2505");
                writer.Write(3);
                uint first = request.ContractId == 1023 ? 1024u : 1280u;
                for (uint id = first; id < first + 3; id++) writer.Write(id);
            }
            else
            {
                Check(reader.ReadBoolean());
                writer.Write(!reject);
                if (request.ContractId == 1024)
                { writer.Write(true); writer.Write(revision); }
                else
                {
                    Check(reader.ReadInt64() == revision);
                    switch (request.ContractId)
                    {
                        case 1025:
                            Check(reader.ReadInt32() == 1);
                            var document = SidecarNxmlDocument.Decode(reader.ReadBytes(reader.ReadInt32()));
                            Check(document.Target == view.Document.Target && document.Operation == SidecarNxmlOperation.Page);
                            Check(document.Files[document.EntryPoint] == view.Document.Files[view.Document.EntryPoint]);
                            ReadModels(); revision++; break;
                        case 1026: ReadModels(); revision++; break;
                        case 1281: case 1282:
                            Check(reader.ReadInt32() == 0);
                            if (!reject) visible = request.ContractId == 1281;
                            break;
                        case 1280:
                            writer.Write(true); writer.Write(revision); writer.Write(1);
                            writer.Write(true); writer.Write(0); Text(view.Document.Target); writer.Write(visible);
                            break;
                        default: throw new InvalidOperationException("Unexpected presentation contract.");
                    }
                    if (reject) { writer.Write((int)XsrErrorKind.Rejected); Text("xsr.rejected"); reject = false; }
                }
            }
            Check(input.Position == input.Length);
            Check(wire!.TryAcceptReply(new(SidecarProtocol.Version,
                frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
                SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true,
                    new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null))));
            return ValueTask.CompletedTask;
        });
        requests.Activate(SidecarFeatures.All);
        IPluginPresentationHost host = await SidecarPresentationHost.CreateAsync(requests, new Observer());
        Check(await host.ReadRevisionAsync(default) == 42);
        await host.ReplaceAsync(42, [view], default);
        await host.UpdateModelsAsync(43, [view], default); Check(models == 2);
        await host.SetOpenAsync(44, 0, true, default);
        Check((await host.ReadVisibleAsync(44, default)).SequenceEqual([0]));
        reject = true;
        bool denied = false;
        try { await host.SetOpenAsync(44, 0, false, default); } catch (InvalidOperationException) { denied = true; }
        Check(denied && visible);
        await host.SetOpenAsync(44, 0, false, default);
        Check((await host.ReadVisibleAsync(44, default)).Count == 0);
    }
    private sealed class Observer : IXsrDispatchObserver { public void OnCompleted(XsrDispatchObservation observation) { } }
}
