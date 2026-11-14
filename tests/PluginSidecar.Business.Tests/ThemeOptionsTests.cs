using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Business;
using Host = Nexa.Sidecar.Host.Data;

internal static class ThemeOptionsTests
{
    private const string Document = """{"format":"NTP-options/1","themeId":"sample.theme","schemaVersion":2,"values":{"enabled":{"kind":"Boolean","value":"true"}}}""";
    internal static async Task Run(IXsrDispatchObserver observer)
    {
        using var fixture = new Fixture(observer);
        var effects = SidecarThemeOptionsEffects.Create(fixture.Client);
        var save = new ThemeOptionsSaveCommand("installed/sample", new Dictionary<string, Host.ThemeOptionValue> { ["enabled"] = new(Host.ThemeOptionKind.Boolean, "true") }, 7);
        BusinessTests.Check(!(await effects.Save(save, default)).IsSuccess && fixture.Calls == 0);
        BusinessTests.Check((await fixture.Client.DiscoverAsync()).IsSuccess);
        BusinessTests.Check((await effects.Save(save, default)).IsSuccess && fixture.LastId == 2048);
        BusinessTests.Check((await effects.Reset(new("installed/sample", 7), default)).IsSuccess && fixture.LastId == 2049);
        fixture.Block = true;
        var pending = effects.Import(new("installed/sample", Document, 7), default);
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); BusinessTests.Check(!pending.IsCompleted);
        fixture.Release.TrySetResult();
        BusinessTests.Check((await pending).IsSuccess && fixture.LastId == 2050 && fixture.Calls == 3);
        fixture.Block = false; fixture.Reject = true;
        BusinessTests.Check((await effects.Import(new("installed/sample", Document, 7), default)).Error!.Code.Value == "themes.revision_conflict" && fixture.Calls == 4);
        foreach (string invalid in new[] { "{}", "null", "{", Document.Replace("NTP-options/1", "NTP-options/2", StringComparison.Ordinal),
            Document.Replace("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2", StringComparison.Ordinal),
            Document.Replace("\"value\":\"true\"", "\"value\":\"true\",\"value\":\"false\"", StringComparison.Ordinal),
            Document.Replace("\"kind\":\"Boolean\"", "\"kind\":\"Bogus\"", StringComparison.Ordinal),
            Document.Replace("\"schemaVersion\":2", "\"schemaVersion\":2.5", StringComparison.Ordinal),
            Document.Replace("\"enabled\":", "\"enabled\":{\"kind\":\"Boolean\",\"value\":\"true\"},\"enabled\":", StringComparison.Ordinal),
            Document.Replace("\"format\":", "\"unknown\":0,\"format\":", StringComparison.Ordinal),
            Document.Replace("\"value\":\"true\"", "\"value\":\"\\u0000\"", StringComparison.Ordinal),
            Document.Replace("\"value\":\"true\"", "\"value\":\"true\",\"files\":null", StringComparison.Ordinal),
            Document.Replace("sample.theme", new string('x', 4097), StringComparison.Ordinal) })
        {
            try { await effects.Import(new("installed/sample", invalid, 7), default); throw new InvalidOperationException("Malformed document accepted"); }
            catch (AbiHostException error) { BusinessTests.Check(error.Code == AbiErrors.SCHEMA_INVALID); }
        }
        try { await effects.Import(new("installed/sample", new string('x', 1024 * 1024 + 1), 7), default); throw new InvalidOperationException("Oversized document accepted"); }
        catch (AbiHostException error) { BusinessTests.Check(error.Code == AbiErrors.MESSAGE_TOO_LARGE); }
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { await effects.Import(new("installed/sample", Document, 7), stop.Token); throw new InvalidOperationException("Cancelled import accepted"); }
        catch (OperationCanceledException) { }
        BusinessTests.Check(fixture.Calls == 4);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SidecarHostRequestClient _requests;
        internal SidecarThemeOptionsClient Client { get; }
        internal int Calls { get; private set; }
        internal uint LastId { get; private set; }
        internal bool Reject { get; set; }
        internal bool Block { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture(IXsrDispatchObserver observer)
        { _requests = new(Reply); _requests.Activate(SidecarFeatures.All); Client = new(_requests, observer); }
        private async ValueTask Reply(SidecarFrame frame, CancellationToken token)
        {
            var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
            void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
            if (request.ContractId == 2047)
            {
                BusinessTests.Check(request.Value.Length == 0); writer.Write(1u);
                Text("42D97D29B57F46A62D65489550190E0D7495C34F45D59CF8D4DD7DDA6A89F74C:A0543680BA3B22B4B73294A43317877DB9D79AFD643631D0F213023D4F6B136A");
                writer.Write(3); writer.Write(2048u); writer.Write(2049u); writer.Write(2050u);
            }
            else
            {
                using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input);
                string ReadText() => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                void Values()
                {
                    BusinessTests.Check(reader.ReadInt32() == 1 && ReadText() == "enabled" && reader.ReadBoolean());
                    BusinessTests.Check(reader.ReadInt32() == 0 && ReadText() == "true" && reader.ReadInt32() == -1);
                }
                BusinessTests.Check(reader.ReadBoolean() && ReadText() == "installed/sample");
                if (request.ContractId == 2048) { Values(); BusinessTests.Check(reader.ReadInt64() == 7); }
                else
                {
                    BusinessTests.Check(reader.ReadInt64() == 7);
                    if (request.ContractId == 2050) { BusinessTests.Check(ReadText() == "sample.theme" && reader.ReadInt32() == 2); Values(); }
                    else BusinessTests.Check(request.ContractId == 2049);
                }
                BusinessTests.Check(input.Position == input.Length);
                Calls++; LastId = request.ContractId;
                if (Block) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
                writer.Write(!Reject);
                if (Reject) { writer.Write((int)XsrErrorKind.Rejected); Text("themes.revision_conflict"); }
            }
            BusinessTests.Check(_requests.TryAcceptReply(new(SidecarProtocol.Version, frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
                SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true, new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null))));
        }
        public void Dispose() => _requests.Dispose();
    }
}
