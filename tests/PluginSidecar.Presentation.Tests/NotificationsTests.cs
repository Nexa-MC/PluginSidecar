using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Presentation;
using PluginSidecar.WasmRuntime;

internal static partial class PresentationTests
{
    private static async Task NotificationPort()
    {
        using var host = new NotificationFixture();
        var notifier = await SidecarNotifications.CreateAsync(host.Requests, new Observer());
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Compiled guest fixture is required.");
        string root = Path.Combine(Path.GetTempPath(), "sidecar-notices-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var package = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "desktop.zip")), "example.desktop", "1.0.0");
            await using var app = new PluginApplication(root, (wasm, _) => ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(wasm.Span)), [], new Allow(),
                notifier.NotifyAsync, (_, _) => { }, PluginViewCompiler.ValidatePage);
            var loaded = await app.LoadAsync(package, package.Manifest.RequestedPermissions, default);
            Check(loaded.State == LoadState.Active);
            var operation = Abi100Catalog.Operations.Single(value => value.Group == "UI" && value.Name == "Notify");
            UINotification notice = new(new("<Button/> {state host.secret}", []), new("中文😀\nBody", []), null);
            Task<AbiReply<AbiValue>> Send(UINotification value, string? key = null) => app.Runtime.InvokeAsync(loaded.Identity,
                operation.ContractId, operation.MethodId, UINotification.Write(value),
                new(Guid.NewGuid(), TimeSpan.FromSeconds(30), default) { IdempotencyKey = key ?? Guid.NewGuid().ToString("N") }).AsTask();
            host.BlockNext();
            var pending = Send(notice, "once");
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!pending.IsCompleted && host.Calls == 1);
            Check(host.Title == "example.desktop: <Button/> {state host.secret}" && host.Body == notice.Body.MessageKey);
            host.Release();
            Check((await pending).Success);
            Check((await Send(notice, "once")).Success && host.Calls == 1); // Runtime idempotency does not publish twice.
            Check((await Send(notice with { Title = new(new string('x', 256), []), Body = new(new string('y', 4096), []) })).Success);
            Check(host.Title.Length == "example.desktop: ".Length + 256 && host.Body.Length == 4096);
            int before = host.Calls;
            Check(!(await Send(notice with { Title = new("localized", [new("argument", new AbiValue.Utf8String("value"))]) })).Success);
            Check(!(await Send(notice with { Action = new("example.desktop:action") })).Success);
            Check(!(await Send(notice with { Title = new(new string('x', 257), []) })).Success);
            Check(!(await Send(notice with { Body = new("bad\0body", []) })).Success);
            Check(host.Calls == before);
            var platform = Abi100Catalog.Operations.Single(value => value.Group == "Platform" && value.Name == "Notify");
            var denied = await app.Runtime.InvokeAsync(loaded.Identity, platform.ContractId, platform.MethodId, UINotification.Write(notice),
                new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default) { IdempotencyKey = "denied" });
            Check(!denied.Success && host.Calls == before); // This guest never received platform notification permission.
            host.Reject = true;
            Check(!(await Send(notice)).Success && host.Calls == ++before);
            host.Reject = false; host.LoseReply = true;
            Check(!(await Send(notice)).Success && host.Calls == ++before);
            host.LoseReply = false;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false;
            try { await notifier.NotifyAsync(loaded.Identity, notice, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && host.Calls == before);
            host.BlockNext(); pending = Send(notice);
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!pending.IsCompleted);
            await app.StopAsync(loaded.Identity.PluginId).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!(await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            await host.DrainAsync();
            Check(host.Calls == ++before && app.Runtime.Active.IsEmpty);
            Check(!(await Send(notice)).Success && host.Calls == before);
        }
        finally { host.Release(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: acknowledged general Host notifications, real guest permissions/idempotency, literal identity, cancellation and no retry after uncertain delivery.");
    }

    private sealed class NotificationFixture : IDisposable
    {
        internal SidecarHostRequestClient Requests { get; }
        internal int Calls { get; private set; }
        internal string Title { get; private set; } = "";
        internal string Body { get; private set; } = "";
        internal bool Reject, LoseReply;
        internal TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _release;
        private int _pending;
        internal NotificationFixture() { Requests = new(Reply); Requests.Activate(SidecarFeatures.All); }
        internal void BlockNext()
        { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); _release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal void Release() => _release?.TrySetResult();
        private async ValueTask Reply(SidecarFrame frame, CancellationToken token)
        {
            if (frame.MessageType == SidecarMessageType.Cancel) { Release(); return; }
            Interlocked.Increment(ref _pending);
            try
            {
                var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
                using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
                void Text(string text) { var bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
                bool uncertain = false;
                if (request.ContractId == 2559)
                {
                    Check(request.Value.Length == 0); writer.Write(1u);
                    Text("4727F28D381FCB96CA31A246E061FB01C7A0006642F63D56584ABBC1598F13C0:0902B722A0DD3A8B8EDEA8BB8AA64C52157437C7159DCB2DD3D7F884B351398A");
                    writer.Write(1); writer.Write(2560u);
                }
                else
                {
                    Check(request.ContractId == 2560); Calls++;
                    using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input);
                    string ReadText() => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                    Check(reader.ReadBoolean() && reader.ReadInt32() == 1);
                    Title = ReadText(); Body = ReadText(); Check(input.Position == input.Length);
                    bool reject = Reject; uncertain = LoseReply;
                    var release = _release;
                    Entered.TrySetResult(); if (release is not null) await release.Task;
                    writer.Write(!reject);
                    if (reject) { writer.Write((int)XsrErrorKind.Rejected); Text("xsr.rejected"); }
                }
                Requests.TryAcceptReply(new(SidecarProtocol.Version,
                    frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
                    SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(!uncertain,
                        new(SidecarWireCodecs.GeneratedDto, uncertain ? [] : output.ToArray()), uncertain ? "xsr.operation_outcome_unknown" : null)));
            }
            finally { Interlocked.Decrement(ref _pending); }
        }
        internal async Task DrainAsync()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref _pending) != 0) await Task.Delay(1, stop.Token);
        }
        public void Dispose() { Release(); Requests.Dispose(); }
    }
}
