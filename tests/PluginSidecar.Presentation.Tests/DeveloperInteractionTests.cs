using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using PluginSidecar.Application;
using PluginSidecar.Presentation;
using PluginSidecar.WasmRuntime;

internal static partial class PresentationTests
{
    private static async Task DeveloperInteractionChecks()
    {
        byte[]? selected = null;
        bool approve = false, remember = false;
        int asks = 0, picks = 0;
        int rejectAsk = -1;
        CancellationTokenSource? cancelOnAsk = null;
        List<(string Title, string Details)> dialogs = [];
        SidecarHostRequestClient? wire = null;
        using var requests = wire = new((frame, token) =>
        {
            var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
            void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
            using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input);
            string ReadText() => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
            switch (request.ContractId)
            {
                case 2815:
                    Check(input.Length == 0); writer.Write(1u);
                    Text("A5F0FB30E9A153BD17BF5C81312D1BAC0905FA159CFF92F7828BB453D52ED4F5:AABD1C6F8DF7A47D65D2F0C5A0E4E376EAA968FE524452CA06DF23B5FD910D8D");
                    writer.Write(1); writer.Write(2816u); break;
                case 1535:
                    Check(input.Length == 0); writer.Write(1u);
                    Text("05C1E5C77E48F23A3210C86227090656C5E3F3D2773AB2BD2981FB41132C7E36:FD011BD293F84967DAB5A1DD6889322E3D75C4D3B06FC20A8B4B5A1FE81D484C");
                    writer.Write(1); writer.Write(1536u); break;
                case 2816:
                    picks++;
                    Check(reader.ReadBoolean() && reader.ReadInt32() == 0 && ReadText() == "选择开发者插件包");
                    Check(reader.ReadInt32() == 1 && ReadText() == "zip");
                    writer.Write(true); writer.Write(true); writer.Write(selected?.Length ?? -1);
                    if (selected is not null) writer.Write(selected);
                    break;
                case 1536:
                    asks++;
                    Check(reader.ReadBoolean());
                    string title = ReadText(), details = ReadText();
                    dialogs.Add((title, details));
                    if (asks == rejectAsk) { writer.Write(false); writer.Write((int)XsrErrorKind.TimedOut); Text("xsr.timed_out"); }
                    else
                    {
                        Check(title is "加载开发者插件" or "安装市场插件" or "记住插件加载授权");
                        writer.Write(true); writer.Write(true); writer.Write(title is "加载开发者插件" or "安装市场插件" ? approve : remember);
                    }
                    cancelOnAsk?.Cancel();
                    break;
                default: throw new InvalidOperationException("Unexpected Host operation.");
            }
            Check(input.Position == input.Length);
            bool received = wire!.TryAcceptReply(new(SidecarProtocol.Version, SidecarMessageType.QueryResult, SidecarFrameTraits.None,
                frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true, new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null)));
            Check(received || cancelOnAsk?.IsCancellationRequested == true);
            return ValueTask.CompletedTask;
        });
        requests.Activate(SidecarFeatures.All);
        var interaction = await SidecarDeveloperPackageInteraction.CreateAsync(requests, new Observer());
        Check(await interaction.PickAsync(default) is null);
        string expectedPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "approved plugin.zip"));
        selected = Encoding.UTF8.GetBytes(expectedPath);
        Check(await interaction.PickAsync(default) == expectedPath);
        foreach (var invalid in new byte[][] { Encoding.UTF8.GetBytes("relative.zip"), [0xff], Encoding.UTF8.GetBytes(expectedPath + "\0"), new byte[131073] })
        {
            selected = invalid;
            await Expect<AbiHostException>(() => interaction.PickAsync(default).AsTask());
        }
        var consent = new PluginInstallConsent("example.test\n伪造标题\u202e", "1.0.0", ["nexa.ui.write", "quote\"\\\npermission"]);
        Check(await interaction.ConfirmAsync(consent, default) == PluginInstallDecision.Declined && asks == 1);
        Check(dialogs[0].Details.Contains("\\n", StringComparison.Ordinal) && dialogs[0].Details.Contains("\\u202E", StringComparison.Ordinal));
        Check(!dialogs[0].Details.Contains('\u202e'));
        approve = true;
        Check(await interaction.ConfirmAsync(consent, default) == PluginInstallDecision.SessionOnly && asks == 3);
        Check(dialogs[^1].Details.Contains("取消：仅在本次会话加载", StringComparison.Ordinal));
        remember = true;
        Check(await interaction.ConfirmAsync(consent, default) == PluginInstallDecision.Remember && asks == 5);
        rejectAsk = asks + 2;
        await Expect<AbiHostException>(() => interaction.ConfirmAsync(consent, default).AsTask());
        rejectAsk = -1;
        using (var cancel = new CancellationTokenSource())
        {
            cancelOnAsk = cancel; int before = asks;
            await Expect<OperationCanceledException>(() => interaction.ConfirmAsync(consent, cancel.Token).AsTask());
            Check(asks == before + 1);
            before = picks;
            await Expect<OperationCanceledException>(() => interaction.PickAsync(cancel.Token).AsTask());
            Check(picks == before);
            cancelOnAsk = null;
        }
        int unchanged = asks;
        await Expect<AbiHostException>(() => interaction.ConfirmAsync(consent with { Permissions = ["duplicate", "duplicate"] }, default).AsTask());
        await Expect<AbiHostException>(() => interaction.ConfirmAsync(consent with { Id = new string('x', 257) }, default).AsTask());
        Check(asks == unchanged);

        Check(await interaction.ConfirmAsync(consent with { Source = PluginInstallSource.Market }, default) == PluginInstallDecision.Remember);
        Check(dialogs[^2].Title == "安装市场插件");
        Check(dialogs[^2].Details.Contains("市场签名", StringComparison.Ordinal));
        Check(!dialogs[^2].Details.Contains("未通过", StringComparison.Ordinal));
        Check(dialogs[^1].Details.Contains("离线恢复", StringComparison.Ordinal));
        Check(!dialogs[^1].Details.Contains("开发者模式", StringComparison.Ordinal));
        await Expect<AbiHostException>(() => interaction.ConfirmAsync(consent with { Source = (PluginInstallSource)99 }, default).AsTask());

        // Compose the real generic interaction adapter with the sole management owner and a real guest.
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Guest fixture required.");
        selected = Encoding.UTF8.GetBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "desktop.zip"));
        string root = Path.Combine(Path.GetTempPath(), "nexa-native-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var catalog = new PluginOfflineCatalog(Path.Combine(root, "installed"), new TestCatalogKeys());
            await using var app = new PluginApplication(Path.Combine(root, "storage"), (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)),
                [], new Allow(), (_, _) => { }, (_, _) => { }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
            var policy = new PluginInstallationPolicy(Guid.NewGuid(), true, true, false);
            await using var manager = new PluginManagementController(app, catalog, () => policy, interaction.PickAsync, interaction.ConfirmAsync);
            await manager.ImportDeveloperPackageAsync(default);
            Check(app.Runtime.Active.Single().PluginId == "example.desktop" && catalog.Records().Length == 1);
            Check(dialogs[^2].Details.Contains("example.desktop", StringComparison.Ordinal));
            await manager.DisableAsync(app.Runtime.Active.Single(), default);
            Check(app.Runtime.Active.IsEmpty && catalog.Records().IsEmpty);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: generic native picker, quoted two-stage consent, cancellation/error rejection and real management installation through generated binary Host operations.");
    }
}
