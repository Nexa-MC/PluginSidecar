using System.Text;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Business;

internal sealed class FilePickerFixture : IDisposable
{
    private readonly SidecarHostRequestClient _requests;
    internal SidecarFilePickerClient Client { get; }
    internal string? Selection { set => Bytes = value is null ? null : Encoding.UTF8.GetBytes(value); }
    internal byte[]? Bytes { get; set; }
    internal int Picks { get; private set; }
    internal string? Title { get; private set; }
    internal bool Reject { get; set; }
    internal TaskCompletionSource? Entered { get; set; }
    internal TaskCompletionSource? Release { get; set; }
    internal TaskCompletionSource? Drained { get; set; }
    internal FilePickerFixture(IXsrDispatchObserver observer)
    {
        _requests = new(Reply); _requests.Activate(SidecarFeatures.All);
        Client = new(_requests, observer);
    }
    private async ValueTask Reply(SidecarFrame frame, CancellationToken token)
    {
        if (frame.MessageType == SidecarMessageType.Cancel) return; // Let the fixture deliver a late response explicitly.
        var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        if (request.ContractId == 2815)
        {
            BusinessTests.Check(request.Value.Length == 0);
            writer.Write(1u);
            Text("A5F0FB30E9A153BD17BF5C81312D1BAC0905FA159CFF92F7828BB453D52ED4F5:AABD1C6F8DF7A47D65D2F0C5A0E4E376EAA968FE524452CA06DF23B5FD910D8D");
            writer.Write(1); writer.Write(2816u);
        }
        else
        {
            BusinessTests.Check(request.ContractId == 2816);
            using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input);
            BusinessTests.Check(reader.ReadBoolean()); BusinessTests.Check(reader.ReadInt32() is >= 0 and <= 2);
            Title = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
            int count = reader.ReadInt32(); BusinessTests.Check(count is >= 0 and <= 32);
            for (int i = 0; i < count; i++) BusinessTests.Check(reader.ReadBytes(reader.ReadInt32()).Length <= 32);
            BusinessTests.Check(input.Position == input.Length);
            Picks++; Entered?.TrySetResult(); if (Release is { } release) await release.Task.WaitAsync(token);
            writer.Write(!Reject);
            if (Reject) { writer.Write((int)XsrErrorKind.Rejected); Text("xsr.rejected"); }
            else { writer.Write(true); writer.Write(Bytes?.Length ?? -1); if (Bytes is not null) writer.Write(Bytes); }
        }
        BusinessTests.Check(_requests.TryAcceptReply(new(SidecarProtocol.Version, SidecarMessageType.QueryResult,
            SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true,
                new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null))));
        Drained?.TrySetResult();
    }
    internal static async Task AdapterChecks(IXsrDispatchObserver observer)
    {
        using var fixture = new FilePickerFixture(observer);
        var adapter = new SidecarFilePicker(fixture.Client);
        static async Task Fails(Func<Task<string?>> action)
        {
            try { await action(); throw new InvalidOperationException("Invalid picker request or reply accepted."); }
            catch (Nexa.Plugin.Hosting.AbiHostException) { }
        }
        await Fails(() => adapter.PickAsync("File", 0, [], default)); BusinessTests.Check(fixture.Picks == 0);
        BusinessTests.Check((await fixture.Client.DiscoverAsync()).IsSuccess);
        foreach (var bad in new (string Title, int Kind, string[] Filters)[]
        { (new string('x', 257), 0, []), ("Bad\nTitle", 0, []), ("File", 3, []), ("File", 0, ["*"]), ("File", 0, ["..."]), ("File", 0, Enumerable.Repeat("zip", 33).ToArray()) })
            await Fails(() => adapter.PickAsync(bad.Title, bad.Kind, bad.Filters, default));
        BusinessTests.Check(fixture.Picks == 0);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await adapter.PickAsync("File", 0, [], cancelled.Token); throw new InvalidOperationException("Pre-cancellation ignored."); }
            catch (OperationCanceledException) { }
            BusinessTests.Check(fixture.Picks == 0);
        }
        fixture.Selection = null; BusinessTests.Check(await adapter.PickAsync("File", 0, [], default) is null);
        string unicode = Path.GetPathRoot(Path.GetTempPath()) + new string('中', 12000);
        fixture.Selection = unicode; BusinessTests.Check(await adapter.PickAsync("File", 2, ["zip"], default) == unicode);
        foreach (byte[] invalid in new byte[][] { [0xff], Encoding.UTF8.GetBytes("relative"), Encoding.UTF8.GetBytes("/bad\0path"), new byte[4 * 32768 + 1] })
        { fixture.Bytes = invalid; await Fails(() => adapter.PickAsync("File", 0, [], default)); }
        fixture.Reject = true; int before = fixture.Picks;
        await Fails(() => adapter.PickAsync("File", 0, [], default)); BusinessTests.Check(fixture.Picks == before + 1);
        Console.WriteLine("PASS: native picker adapter validates before IPC, preserves Unicode paths, distinguishes dismissal, rejects invalid replies and never retries.");
    }
    public void Dispose() { Release?.TrySetResult(); _requests.Dispose(); }
}
