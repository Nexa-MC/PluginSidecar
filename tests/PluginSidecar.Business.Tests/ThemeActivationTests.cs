using System.Collections.Concurrent;
using System.Text;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Business;

internal static class ThemeActivationTests
{
    internal static async Task Run(IXsrDispatchObserver observer)
    {
        using var host = new Fixture(observer);
        ConcurrentQueue<XsrError> failures = [];
        var effects = SidecarThemeActivationEffects.Create(host.Client, failures.Enqueue);
        BusinessTests.Check((await host.Client.DiscoverAsync()).IsSuccess);
        var preview = await effects.Activate("source", "revision", 7, true, default);
        BusinessTests.Check(preview.IsSuccess && preview.Value > 0 && preview.Value == host.Active);
        BusinessTests.Check((await effects.Revert(preview.Value, default)).IsSuccess && host.Active == 0);
        var replacement = SidecarThemeActivationEffects.Create(host.Client, failures.Enqueue);
        var next = await replacement.Activate("source", "revision", 7, true, default);
        BusinessTests.Check(next.IsSuccess && next.Value > preview.Value);
        BusinessTests.Check((await replacement.Revert(next.Value, default)).IsSuccess);
        int before = host.Previews;
        host.LoseReply = true;
        var lost = await effects.Activate("source", "revision", 7, true, default);
        BusinessTests.Check(!lost.IsSuccess && lost.Error!.Code.Value == "xsr.operation_outcome_unknown"
            && host.Previews == before + 1 && host.Active == 0 && host.LastEnded == host.LastPreview);
        int ends = host.Ends;
        var commit = await effects.Activate("source", "revision", 7, false, default);
        BusinessTests.Check(!commit.IsSuccess && host.Commits == 1 && host.Ends == ends); // Persistent commit is never undone on an uncertain reply.
        host.LoseReply = false; host.RejectPreview = true;
        BusinessTests.Check(!(await effects.Activate("source", "revision", 7, true, default)).IsSuccess && host.Active == 0);
        host.RejectPreview = false; host.Block = true;
        using var stop = new CancellationTokenSource();
        var pending = effects.Activate("source", "revision", 7, true, stop.Token);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); BusinessTests.Check(!pending.IsCompleted);
        stop.Cancel();
        BusinessTests.Check(!(await pending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess && host.Active == 0
            && host.LastEnded == host.LastPreview && !host.EndTokenCancelled);
        host.Block = false;
        BusinessTests.Check(failures.IsEmpty);
        host.LoseReply = true; host.RejectEnd = true;
        before = host.Previews; ends = host.Ends;
        BusinessTests.Check(!(await effects.Activate("source", "revision", 7, true, default)).IsSuccess);
        BusinessTests.Check(host.Previews == before + 1 && host.Ends == ends + 1 && failures.Count == 1);
        Console.WriteLine("PASS: typed theme activation, monotonic preview identities, lost-reply compensation, independent cancellation budget and no activation retries.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SidecarHostRequestClient _requests;
        internal SidecarThemeActivationClient Client { get; }
        internal long Active { get; private set; }
        internal long LastPreview { get; private set; }
        internal long LastEnded { get; private set; }
        internal int Previews { get; private set; }
        internal int Commits { get; private set; }
        internal int Ends { get; private set; }
        internal bool LoseReply { get; set; }
        internal bool RejectPreview { get; set; }
        internal bool RejectEnd { get; set; }
        internal bool Block { get; set; }
        internal bool EndTokenCancelled { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture(IXsrDispatchObserver observer)
        { _requests = new(Reply); _requests.Activate(SidecarFeatures.All); Client = new(_requests, observer); }
        private async ValueTask Reply(SidecarFrame frame, CancellationToken token)
        {
            if (frame.MessageType == SidecarMessageType.Cancel) { _release.TrySetResult(); return; }
            var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
            void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
            bool uncertain = false;
            if (request.ContractId == 2303)
            {
                BusinessTests.Check(request.Value.Length == 0); writer.Write(1u);
                Text("54363B11A4654091FB9855460A6F1E1BC5E3440584082031CD3259A8F2816BF5:6CAEE55F44448FA0BD4F93FA754610ABA0888C3DC99999DFA116245FF339A0DE");
                writer.Write(3); writer.Write(2304u); writer.Write(2305u); writer.Write(2306u);
            }
            else
            {
                using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input);
                string ReadText() => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                BusinessTests.Check(reader.ReadBoolean());
                bool reject;
                if (request.ContractId == 2306)
                {
                    LastEnded = reader.ReadInt64(); BusinessTests.Check(LastEnded > 0);
                    Ends++; EndTokenCancelled = token.IsCancellationRequested;
                    reject = RejectEnd;
                    if (!reject && Active == LastEnded) Active = 0;
                }
                else
                {
                    if (request.ContractId == 2305) BusinessTests.Check(reader.ReadBoolean());
                    else BusinessTests.Check(request.ContractId == 2304);
                    BusinessTests.Check(ReadText() == "source" && ReadText() == "revision" && reader.ReadInt64() == 7);
                    reject = request.ContractId == 2305 && RejectPreview;
                    if (request.ContractId == 2305)
                    {
                        long id = reader.ReadInt64(); BusinessTests.Check(id > LastPreview); LastPreview = id; Previews++;
                        if (!reject) Active = id;
                        if (Block) { Entered.TrySetResult(); await _release.Task; }
                    }
                    else { Commits++; Active = 0; }
                    uncertain = LoseReply;
                }
                BusinessTests.Check(input.Position == input.Length);
                writer.Write(!reject);
                if (reject) { writer.Write((int)XsrErrorKind.Rejected); Text("xsr.rejected"); }
            }
            _requests.TryAcceptReply(new(SidecarProtocol.Version, frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
                SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(!uncertain,
                    new(SidecarWireCodecs.GeneratedDto, uncertain ? [] : output.ToArray()), uncertain ? "xsr.operation_outcome_unknown" : null)));
        }
        public void Dispose() => _requests.Dispose();
    }
}
