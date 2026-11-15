using System.Diagnostics;
using System.Security.Cryptography;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Presentation;

internal static partial class StandaloneRuntimeTests
{
    internal static async Task RunProcess()
    {
        string worker = Environment.GetEnvironmentVariable("NEXA_PLUGIN_WORKER") ?? throw new InvalidOperationException("A real Worker executable is required.");
        string sample = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Real Wasm fixtures are required.");
        string package = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sample))!, "desktop.zip");
        string root = Path.Combine(Path.GetTempPath(), "nexa-standalone-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var token = timeout.Token;
        using var listener = SidecarIpcListener.Bind("nexa-plugin-test-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (worker.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        { start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet"; start.ArgumentList.Add(Path.GetFullPath(worker)); }
        else start.FileName = Path.GetFullPath(worker);
        start.ArgumentList.Add("--nexa-sidecar"); start.ArgumentList.Add("--endpoint"); start.ArgumentList.Add(listener.Endpoint);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Worker not started.");
        var errors = process.StandardError.ReadToEndAsync(token); var output = process.StandardOutput.ReadToEndAsync(token);
        try
        {
            byte[] secret = RandomNumberGenerator.GetBytes(SidecarBootstrap.ChallengeSize);
            await process.StandardInput.BaseStream.WriteAsync(secret, token); await process.StandardInput.BaseStream.FlushAsync(token);
            await using var stream = await listener.AcceptAsync(token);
            try { await SidecarBootstrap.AuthenticateAsync(stream, secret, token); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            using var native = new NativeHost(root, package);
            HashSet<uint> commands = [1025, 1026, 3840, 4096, 4355];
            uint[] operations = [254, 340, 3328, 1024, 1025, 1026, 3840, 1280, 3584, 4096, 4355, 2816, 1536];
            SidecarHostSession? owner = null;
            var router = new SidecarHostRequestRouter(Catalogs.Keys.Concat(operations).Select(id => new SidecarHostRequestRoute(id, !commands.Contains(id),
                SidecarWireCodecs.GeneratedDto, SidecarWireCodecs.GeneratedDto, TimeSpan.FromSeconds(10),
                caller => caller.PackageName == "Nexa.Plugin.Sidecar" && caller.SessionId == owner?.SessionId,
                async (_, input, stop) =>
                {
                    var reply = await native.Requests.InvokeAsync(id, !commands.Contains(id), input, SidecarWireCodecs.GeneratedDto, TimeSpan.FromSeconds(10), stop);
                    Check(reply.Success); return XsrResult.Success(reply.Value);
                })));
            using var connection = new SidecarConnection(stream);
            using var session = new SidecarHostSession(connection, "Nexa.Plugin.Sidecar") { HostRequests = router }; owner = session;
            await session.HandshakeAsync(token); await session.AcceptRegistrationAsync(token); await session.AcceptStateSnapshotAsync(token); await session.ActivateAsync(token);
            Task receive = session.RunReceiveLoopAsync(token).AsTask();
            await Wait(() => native.Button("加载开发包") is not null);
            Check(native.Discoveries.Count == 17);
            await Command(PluginPagePublisher.ManagementCommand, Form(native.Button("加载开发包")!));
            await Wait(() => native.Views.Any(view => view.Document.EntryPoint != "manager.nxml" && view.Document.Operation == SidecarNxmlOperation.Page));
            var guest = native.Views.Single(view => view.Document.EntryPoint != "manager.nxml" && view.Document.Operation == SidecarNxmlOperation.Page);
            var action = Nodes(Compile(guest).Root.Root).Single(node => node.Command?.Value == PluginViewCompiler.ActionCommand);
            var actionForm = new SidecarBinaryValue(SidecarWireCodecs.GeneratedDto, SidecarUiForms.Encode(action.Argument!, new Dictionary<string, string>()));
            await Command(PluginViewCompiler.ActionCommand, actionForm);
            await Wait(() => native.Confirmations == 3 && native.Button("停用") is not null);
            await Command(PluginPagePublisher.ManagementCommand, Form(native.Button("停用")!));
            await Wait(() => native.Views.Count(view => view.Document.Operation == SidecarNxmlOperation.Page) == 1);
            var stale = await session.SendBinaryCommandAsync(XsrSemanticId.Parse(PluginViewCompiler.ActionCommand), actionForm, TimeSpan.FromSeconds(5), token);
            Check(!stale.IsSuccess && process.Id != Environment.ProcessId && !process.HasExited);
            await session.ShutdownAsync(token); await receive.WaitAsync(token); await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new InvalidOperationException("Standalone process failed: " + await errors);
            Check((await output).Length == 0);
            async Task Command(string semantic, SidecarBinaryValue value)
            {
                var result = await session.SendBinaryCommandAsync(XsrSemanticId.Parse(semantic), value, TimeSpan.FromSeconds(10), token);
                if (!result.IsSuccess) throw new InvalidOperationException("Standalone process command failed: " + result.Error?.Code.Value);
            }
            async Task Wait(Func<bool> ready)
            {
                while (!ready())
                {
                    token.ThrowIfCancellationRequested();
                    if (receive.IsCompleted) { await receive; throw new IOException("Standalone channel closed early."); }
                    if (process.HasExited) throw new IOException("Standalone process exited early: " + await errors);
                    await Task.Delay(20, token);
                }
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            Directory.Delete(root, true);
        }
        Console.WriteLine("PASS: production standalone process authenticates, discovers native catalogs, imports real Wasm, executes a confirmed action, disables, rejects a stale callback and drains shutdown.");
    }
}
