using System.Text;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using PluginSidecar.Presentation;

internal static partial class PresentationTests
{
    private static async Task ShortcutHostChecks()
    {
        int calls = 0;
        bool reject = false, loseReply = false;
        PluginShortcutBinding[] expected = [new(2, "Ctrl+Alt+P", "opaque-token")];
        SidecarHostRequestClient? wire = null;
        using var requests = wire = new((frame, token) =>
        {
            token.ThrowIfCancellationRequested();
            var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
            using var input = new MemoryStream(request.Value.ToArray());
            using var reader = new BinaryReader(input, Encoding.UTF8);
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
            string ReadText() => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
            if (request.ContractId == 4095)
            {
                writer.Write(1u);
                Text("86AF04C1731566DA5EC37A5BFDB2AFAFA0616A0BD9F8874031D4916209FAE0A4:C87A5B88A5CFC380BFD1BF26A44784CF700A934FB4593B827DB39A0D9ADB1821");
                writer.Write(1); writer.Write(4096u);
            }
            else
            {
                Check(request.ContractId == 4096); calls++;
                Check(reader.ReadBoolean()); Check(reader.ReadInt64() == 42);
                Check(reader.ReadInt32() == expected.Length);
                foreach (var binding in expected)
                {
                    Check(reader.ReadBoolean()); Check(reader.ReadInt32() == binding.Document);
                    Check(ReadText() == binding.Gesture); Check(ReadText() == PluginViewCompiler.ActionCommand);
                    Check(ReadText() == binding.Argument);
                }
                writer.Write(!reject);
                if (reject) { writer.Write((int)XsrErrorKind.Rejected); Text("xsr.rejected"); }
                if (loseReply) throw new IOException("Shortcut set acknowledgement lost.");
            }
            Check(input.Position == input.Length);
            Check(wire!.TryAcceptReply(new(SidecarProtocol.Version,
                frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
                SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true,
                    new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null))));
            return ValueTask.CompletedTask;
        });
        requests.Activate(SidecarFeatures.All);
        var host = await SidecarShortcutHost.CreateAsync(requests, new Observer());
        await host.ReplaceAsync(42, expected, default); Check(calls == 1);
        reject = true;
        await MustFail(() => host.ReplaceAsync(42, expected, default).AsTask()); Check(calls == 2);
        reject = false; loseReply = true;
        await MustFail(() => host.ReplaceAsync(42, expected, default).AsTask()); Check(calls == 3);
        loseReply = false;
        foreach (var invalid in new PluginShortcutBinding[][]
        {
            [new(-1, "Ctrl+P", "")], [new(32, "Ctrl+P", "")], [new(0, " ", "")],
            [new(0, "Ctrl\nP", "")], [new(0, new string('x', 65), "")],
            [new(0, "Ctrl+P", new string('x', 32769))], Enumerable.Repeat(expected[0], 65).ToArray()
        }) await MustFail(() => host.ReplaceAsync(42, invalid, default).AsTask());
        Check(calls == 3);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { await host.ReplaceAsync(42, expected, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped && calls == 3);
        expected = [];
        await host.ReplaceAsync(42, expected, default); Check(calls == 4);
        Console.WriteLine("PASS native shortcut discovery, binary contract, validation and no replay after lost acknowledgement");

        static async Task MustFail(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException) { return; }
            throw new Exception("Invalid shortcut operation was accepted.");
        }
    }
}
