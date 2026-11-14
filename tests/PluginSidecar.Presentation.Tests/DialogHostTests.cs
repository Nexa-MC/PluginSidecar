using System.Text;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using PluginSidecar.Presentation;

internal static partial class PresentationTests
{
    private static async Task DialogHostPort()
    {
        bool open = false, reject = false, loseReply = false;
        int opens = 0;
        long stateRevision = 12;
        (int Index, string Target, bool Open)[]? overrideState = null;
        SidecarHostRequestClient? wire = null;
        using var requests = wire = new((frame, token) =>
        {
            token.ThrowIfCancellationRequested();
            var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
            using var input = new MemoryStream(request.Value.ToArray());
            using var reader = new BinaryReader(input, Encoding.UTF8);
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            void Text(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
            if (request.ContractId == 3583)
            {
                writer.Write(1u);
                Text("A71C4011DE44E975B9AC4739B69972D33A92781A74E3E20D9B02BB9112AC2409:1096DA93777680CBF63BA34AE34B273B8438A361C0581B55093234B34313CD31");
                writer.Write(3); writer.Write(3584u); writer.Write(3585u); writer.Write(3586u);
            }
            else
            {
                Check(reader.ReadBoolean()); Check(reader.ReadInt64() == 12);
                writer.Write(!reject);
                if (request.ContractId == 3584)
                {
                    if (!reject)
                    {
                        writer.Write(true); writer.Write(stateRevision);
                        var dialogs = overrideState ?? [(2, "#ScopedModal", open)];
                        writer.Write(dialogs.Length);
                        foreach (var dialog in dialogs)
                        { writer.Write(true); writer.Write(dialog.Index); Text(dialog.Target); writer.Write(dialog.Open); }
                    }
                }
                else
                {
                    Check(request.ContractId is 3585 or 3586); Check(reader.ReadInt32() == 2);
                    if (request.ContractId == 3585) opens++;
                    if (!reject) open = request.ContractId == 3585;
                }
                if (reject) { writer.Write((int)XsrErrorKind.Rejected); Text("xsr.rejected"); }
            }
            Check(input.Position == input.Length);
            if (loseReply && request.ContractId == 3585) throw new IOException("Committed modal open lost its acknowledgement.");
            Check(wire!.TryAcceptReply(new(SidecarProtocol.Version,
                frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
                SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true,
                    new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null))));
            return ValueTask.CompletedTask;
        });
        requests.Activate(SidecarFeatures.All);
        var host = await SidecarDialogHost.CreateAsync(requests, new Observer());
        Check((await host.ReadAsync(12)).Single() == new PluginHostDialogState(2, "#ScopedModal", false));
        await host.SetOpenAsync(12, 2, true); Check(open && opens == 1);
        Check((await host.ReadAsync(12)).Single().Open);
        reject = true;
        await MustFail(() => host.SetOpenAsync(12, 2, false).AsTask()); Check(open);
        reject = false;
        await host.SetOpenAsync(12, 2, false); Check(!open);
        stateRevision = 13;
        await MustFail(async () => { await host.ReadAsync(12); });
        stateRevision = 12;
        foreach (var invalid in new (int, string, bool)[][]
        {
            [(2, "#ScopedModal", false), (2, "#Another", false)],
            [(2, "#ScopedModal", false), (3, "#ScopedModal", false)],
            [(-1, "#ScopedModal", false)], [(32, "#ScopedModal", false)],
            [(2, "#Parent/#Child", false)], [(2, "missing-hash", false)], [(2, "#bad key", false)],
            Enumerable.Range(0, 33).Select(index => (index, "#Modal" + index, false)).ToArray()
        })
        {
            overrideState = invalid;
            await MustFail(async () => { await host.ReadAsync(12); });
        }
        overrideState = null;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { await host.SetOpenAsync(12, 2, true, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped && opens == 1);
        loseReply = true;
        await MustFail(() => host.SetOpenAsync(12, 2, true).AsTask());
        Check(open && opens == 2); // No implicit retry after a committed command loses its reply.
        loseReply = false;
        Check((await host.ReadAsync(12)).Single().Open);
        await host.SetOpenAsync(12, 2, false); Check(!open);

        static async Task MustFail(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException) { return; }
            throw new Exception("Invalid modal operation was accepted.");
        }
    }
}
