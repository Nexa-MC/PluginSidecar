using System.Text;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

internal sealed class ShellFixture : IDisposable
{
    private readonly SidecarHostRequestClient _requests;
    internal SidecarShellClient Client { get; }
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Opens { get; private set; }
    internal int Reveals { get; private set; }
    internal string? DirectoryPath { get; private set; }
    internal bool Reject { get; set; }
    internal ShellFixture(IXsrDispatchObserver observer)
    {
        _requests = new(Reply);
        _requests.Activate(SidecarFeatures.All);
        Client = new(_requests, observer);
    }
    private async ValueTask Reply(SidecarFrame frame, CancellationToken token)
    {
        var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        if (request.ContractId == 1791)
        {
            BusinessTests.Check(request.Value.Length == 0);
            writer.Write(1u);
            Text("77FD1B1073A399AA6FF482D0D27EACD9DD0C25920EBAA8C028E8CB9DEB24AC4D:E5EDAE5F8A890D26E29C7E4B5FD612DABF69B0180B404C5D52CAB880AE7B59EA");
            writer.Write(3); writer.Write(1792u); writer.Write(1793u); writer.Write(1794u);
        }
        else
        {
            using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input);
            BusinessTests.Check(reader.ReadBoolean());
            if (request.ContractId == 1792)
            {
                BusinessTests.Check(Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())).StartsWith("https://", StringComparison.Ordinal));
                Opens++; Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            else if (request.ContractId == 1793)
            {
                DirectoryPath = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                BusinessTests.Check(Path.IsPathFullyQualified(DirectoryPath)); Reveals++;
            }
            else BusinessTests.Check(request.ContractId == 1794);
            BusinessTests.Check(input.Position == input.Length);
            writer.Write(!Reject);
            if (Reject) { writer.Write((int)XsrErrorKind.Rejected); Text("xsr.rejected"); }
            else if (request.ContractId == 1794)
            {
                writer.Write(true); writer.Write(1); writer.Write(true);
                Text("main"); writer.Write(1920); writer.Write(1080); writer.Write(1.25);
            }
        }
        BusinessTests.Check(_requests.TryAcceptReply(new(SidecarProtocol.Version, frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
            SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true,
                new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null))));
    }
    public void Dispose() => _requests.Dispose();
}
