using System.Text;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Business;

internal sealed class ContextFixture : IDisposable
{
    private readonly SidecarHostRequestClient _requests;
    internal SidecarContextClient Client { get; }
    internal int Phase { get; set; } = 1;
    internal bool Native { get; set; } = true;
    internal byte[] Root { get; set; } = Encoding.UTF8.GetBytes(Path.GetTempPath());
    internal int Reads { get; private set; }
    internal Action? OnRead { get; set; }
    internal string Version { get; set; } = "2.0.0.alpha.29";
    internal ContextFixture(IXsrDispatchObserver observer)
    {
        _requests = new(Reply); _requests.Activate(SidecarFeatures.All);
        Client = new(_requests, observer);
    }
    private ValueTask Reply(SidecarFrame frame, CancellationToken token)
    {
        if (frame.MessageType == SidecarMessageType.Cancel) return ValueTask.CompletedTask;
        var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        if (request.ContractId == 3327)
        {
            BusinessTests.Check(request.Value.Length == 0);
            writer.Write(1u);
            Text("9AE621D854383DCE15D632BDDB710A0A90210844FD32DA799C6DB4BC046962AB:7F31A47018303BE1DA45A067122295468E3BBEA4420232812BB0A7642E71E61B");
            writer.Write(1); writer.Write(3328u);
        }
        else
        {
            BusinessTests.Check(request.ContractId == 3328 && request.Value.Span.SequenceEqual(new byte[] { 1 }));
            Reads++; OnRead?.Invoke();
            writer.Write(true); writer.Write(true); Text(Version);
            writer.Write(Root.Length); writer.Write(Root); writer.Write(Phase); writer.Write(Native);
        }
        BusinessTests.Check(_requests.TryAcceptReply(new(SidecarProtocol.Version, SidecarMessageType.QueryResult,
            SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true,
                new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null))));
        return ValueTask.CompletedTask;
    }
    internal static async Task AdapterChecks(IXsrDispatchObserver observer)
    {
        using var fixture = new ContextFixture(observer);
        var adapter = new SidecarRuntimeContext(fixture.Client);
        async Task Fails<T>(Func<Task> run) where T : Exception
        {
            bool failed = false;
            try { await run(); } catch (T) { failed = true; }
            BusinessTests.Check(failed);
        }
        Task<SidecarRuntimeFacts> Wait(bool window = true, CancellationToken token = default) =>
            adapter.WaitReadyAsync(window, TimeSpan.FromSeconds(5), token);
        await Fails<InvalidOperationException>(() => Wait()); BusinessTests.Check(fixture.Reads == 0);
        BusinessTests.Check((await fixture.Client.DiscoverAsync()).IsSuccess);
        string root = Path.GetPathRoot(Path.GetTempPath()) + new string('中', 12000) + "😀";
        fixture.Root = Encoding.UTF8.GetBytes(root);
        BusinessTests.Check((await Wait()).DataRoot == root);
        fixture.Phase = 0; fixture.Native = false;
        int start = fixture.Reads;
        fixture.OnRead = () =>
        {
            int step = fixture.Reads - start;
            fixture.Phase = step >= 2 ? 1 : 0;
            fixture.Native = step >= 3;
        };
        BusinessTests.Check((await Wait()).NativeWindowReady && fixture.Reads == start + 3);
        fixture.OnRead = null; fixture.Native = false;
        BusinessTests.Check(!(await Wait(false)).NativeWindowReady);
        fixture.Phase = 2;
        await Fails<InvalidOperationException>(() => Wait());
        fixture.Phase = 1; fixture.Native = true;
        foreach (byte[] invalid in new byte[][] { [0xff], [], Encoding.UTF8.GetBytes("relative"), Encoding.UTF8.GetBytes("/bad\0path"), new byte[131073] })
        { fixture.Root = invalid; await Fails<InvalidDataException>(() => Wait()); }
        fixture.Root = Encoding.UTF8.GetBytes(Path.GetTempPath());
        fixture.Version = "Bad\nVersion";
        await Fails<InvalidDataException>(() => Wait());
        fixture.Version = "2.0.0";
        fixture.Phase = 0; fixture.Native = true;
        await Fails<InvalidDataException>(() => Wait());
        fixture.Native = false;
        await Fails<TimeoutException>(() => adapter.WaitReadyAsync(true, TimeSpan.FromMilliseconds(40), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel(); int before = fixture.Reads;
        await Fails<OperationCanceledException>(() => Wait(token: cancelled.Token)); BusinessTests.Check(fixture.Reads == before);
        using var during = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Fails<OperationCanceledException>(() => Wait(token: during.Token));
        await Fails<ArgumentOutOfRangeException>(() => adapter.WaitReadyAsync(true, TimeSpan.Zero, default));
        Console.WriteLine("PASS: Host context waits for separate business/native readiness, validates roots and exits on shutdown, deadline and activation cancellation.");
    }
    public void Dispose() => _requests.Dispose();
}
