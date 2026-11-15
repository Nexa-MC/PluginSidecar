using System.Text;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr.Renderer.Nxml;
using PluginSidecar;
using PluginSidecar.Presentation;

internal static partial class StandaloneRuntimeTests
{
    internal static async Task Run()
    {
        string fixture = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE") ?? throw new InvalidOperationException("Real Wasm fixtures are required.");
        string package = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fixture))!, "desktop.zip");
        string root = Path.Combine(Path.GetTempPath(), "nexa-standalone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var host = new NativeHost(root, package);
            var http = new TrackingHttp();
            int networks = 0;
            var runtime = new StandalonePluginRuntime((_, token) =>
            { token.ThrowIfCancellationRequested(); networks++; return ValueTask.FromResult(new HttpClient(http)); }, _ => { });
            try
            {
                Check(runtime.Registrations.Select(item => item.SemanticId).SequenceEqual(new[]
                { PluginViewCompiler.ActionCommand, PluginPagePublisher.ManagementCommand, PluginViewCompiler.DrawingInputCommand }));
                await runtime.ActivateAsync(host.Requests, default);
                await Until(() => host.Button("加载开发包") is not null, runtime);
                Check(networks == 1 && host.Discoveries.Count == 16);
                Check(Directory.Exists(Path.Combine(root, "plugins", "storage")));
                await runtime.ExecuteAsync(2, Form(host.Button("加载开发包")!), default);
                await Until(() => host.Views.Any(view => view.Document.EntryPoint != "manager.nxml"
                    && view.Document.Operation == SidecarNxmlOperation.Page), runtime);
                var guest = host.Views.Single(view => view.Document.EntryPoint != "manager.nxml" && view.Document.Operation == SidecarNxmlOperation.Page);
                var action = Nodes(Compile(guest).Root.Root).Single(node => node.Command?.Value == PluginViewCompiler.ActionCommand);
                byte[] actionForm = SidecarUiForms.Encode(action.Argument!, new Dictionary<string, string>());
                await runtime.ExecuteAsync(1, new(SidecarWireCodecs.GeneratedDto, actionForm), default);
                await Until(() => host.Confirmations >= 3, runtime); // Two import choices, then the real guest action's confirmation.
                await Until(() => host.Button("停用") is not null, runtime);
                await runtime.ExecuteAsync(2, Form(host.Button("停用")!), default);
                await Until(() => host.Views.Count(view => view.Document.Operation == SidecarNxmlOperation.Page) == 1, runtime);
                await Rejected(() => runtime.ExecuteAsync(1, new(SidecarWireCodecs.GeneratedDto, actionForm), default).AsTask());
                await Rejected(() => runtime.ExecuteAsync(99, new(SidecarWireCodecs.GeneratedDto, []), default).AsTask());
                Check(!http.Disposed && host.Picks == 1 && host.Confirmations == 3);
            }
            finally { await Task.WhenAll(runtime.DisposeAsync().AsTask(), runtime.DisposeAsync().AsTask()); }
            Check(http.Disposed && runtime.Completion.IsCompletedSuccessfully);
            await Rejected(() => runtime.ExecuteAsync(2, Form("retired"), default).AsTask());

            // No admission or storage owner is created when native readiness is cancelled.
            using var waitingHost = new NativeHost(root, package) { Ready = false };
            using var cancellation = new CancellationTokenSource();
            await using var waiting = new StandalonePluginRuntime((_, _) => throw new Exception("Network created before readiness."), _ => { });
            var activation = waiting.ActivateAsync(waitingHost.Requests, cancellation.Token).AsTask();
            await waitingHost.ContextRead.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            await Rejected(() => activation);
            Check(waitingHost.Discoveries.SetEquals([3327]));

            using var brokenHost = new NativeHost(root, package);
            await using var broken = new StandalonePluginRuntime((_, _) => throw new IOException("Configured network unavailable."), _ => { });
            await Rejected(() => broken.ActivateAsync(brokenHost.Requests, default).AsTask());
            Check(brokenHost.Views.Length == 0);
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name is { } name
                && (name.StartsWith("Nexa.Services.", StringComparison.Ordinal) || name is "Nexa.Desktop" or "Avalonia")));
            using var cancellationHost = new NativeHost(root, package);
            var cancellationHttp = new TrackingHttp();
            var faultyCancellation = new StandalonePluginRuntime((_, token) =>
            {
                token.Register(() => throw new InvalidOperationException("Cancellation observer failed."));
                return ValueTask.FromResult(new HttpClient(cancellationHttp));
            }, _ => { });
            await faultyCancellation.ActivateAsync(cancellationHost.Requests, default);
            bool cleanupFailed = false;
            try { await faultyCancellation.DisposeAsync(); }
            catch (AggregateException) { cleanupFailed = true; }
            Check(cleanupFailed && cancellationHttp.Disposed && faultyCancellation.Completion.IsCompleted);
            Console.WriteLine("PASS: standalone native catalog composition, real Wasm import/action/disable, exact callback routing, independent HTTP ownership, readiness cancellation, partial activation cleanup and cancellation-callback failure draining.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static SidecarBinaryValue Form(string argument) => new(SidecarWireCodecs.GeneratedDto,
        SidecarUiForms.Encode(argument, new Dictionary<string, string>()));
    private static async Task Until(Func<bool> condition, StandalonePluginRuntime runtime)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (runtime.Completion.IsCompleted) { await runtime.Completion; throw new Exception("Publication ended."); }
            await Task.Delay(20, deadline.Token);
        }
    }
    private static async Task Rejected(Func<Task> work)
    {
        try { await work(); }
        catch (Exception error) when (error is AbiHostException or InvalidDataException or IOException or InvalidOperationException or OperationCanceledException) { return; }
        throw new Exception("Invalid Worker operation succeeded.");
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Standalone Worker assertion failed."); }
    private static IEnumerable<NxmlIrNode> Nodes(NxmlIrNode node)
    { yield return node; foreach (var child in node.Children) foreach (var descendant in Nodes(child)) yield return descendant; }
    private static NxmlPreparedDocument Compile(View view) => new NxmlExtensionProgram(view.Document.EntryPoint, view.Document.Files).Compile(view.Model);
    private sealed record View(SidecarNxmlDocument Document, Dictionary<string, string> Model);
    private sealed class TrackingHttp : HttpMessageHandler
    {
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new Exception("Unexpected network request.");
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    /// <summary>Frozen native binary fixture; never invokes a launcher service or guest policy.</summary>
    private sealed class NativeHost : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _root, _package;
        private View[] _views = [];
        private long _revision;
        internal bool Ready = true;
        internal int Confirmations, Picks;
        internal readonly HashSet<uint> Discoveries = [];
        internal readonly TaskCompletionSource ContextRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SidecarHostRequestClient Requests { get; }
        internal View[] Views { get { lock (_gate) return _views.ToArray(); } }
        internal NativeHost(string root, string package)
        { _root = root; _package = package; Requests = new(Send); Requests.Activate(SidecarFeatures.All); }
        internal string? Button(string label)
        {
            var manager = Views.SingleOrDefault(view => view.Document.EntryPoint == "manager.nxml");
            if (manager is null) return null;
            return Nodes(Compile(manager).Root.Root).SingleOrDefault(node => node.Content == label
                && node.Command?.Value == PluginPagePublisher.ManagementCommand && node.IsVisible && node.Enabled)?.Argument;
        }
        private ValueTask Send(SidecarFrame frame, CancellationToken token)
        {
            if (frame.MessageType == SidecarMessageType.Cancel) return ValueTask.CompletedTask;
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
                using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
                using var input = new MemoryStream(request.Value.ToArray()); using var reader = new BinaryReader(input, Encoding.UTF8);
                void Text(string? value) { if (value is null) writer.Write(-1); else { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); } }
                byte[] Bytes() => reader.ReadBytes(reader.ReadInt32());
                Dictionary<string, string> Model()
                {
                    int count = reader.ReadInt32(); Dictionary<string, string> result = new(StringComparer.Ordinal);
                    for (int i = 0; i < count; i++) { Check(reader.ReadBoolean()); result.Add(Encoding.UTF8.GetString(Bytes()), Encoding.UTF8.GetString(Bytes())); }
                    return result;
                }
                if (Catalogs.TryGetValue(request.ContractId, out var catalog))
                {
                    Discoveries.Add(request.ContractId); writer.Write(1u); Text(catalog.Identity);
                    writer.Write(catalog.Operations.Length); foreach (uint id in catalog.Operations) writer.Write(id);
                }
                else if (request.ContractId == 254)
                {
                    writer.Write(true);
                    writer.Write(1L); writer.Write(false); // library unavailable
                    writer.Write(1L); writer.Write(0); // accounts empty
                    writer.Write(1L); writer.Write(0); // processes empty
                    writer.Write(1L); writer.Write(false); // installation unavailable
                    writer.Write(1L); writer.Write(false); // login unavailable
                    writer.Write(1L); writer.Write(0); // characters empty
                    writer.Write(1L); // settings revision
                }
                else
                {
                    Check(reader.ReadBoolean()); writer.Write(true); // request presence / successful outcome
                    switch (request.ContractId)
                    {
                        case 3328:
                            writer.Write(true); Text("2.0.0.alpha.test"); Text(_root); writer.Write(Ready ? 1 : 0); writer.Write(Ready);
                            ContextRead.TrySetResult(); break;
                        case 340:
                            for (int i = 0; i < 3; i++) Check(reader.ReadInt32() == -1);
                            writer.Write(true); writer.Write(1L); writer.Write(1);
                            writer.Write(true); Text("developer.enabled"); writer.Write(true); writer.Write(2); Text("true");
                            writer.Write(0); writer.Write(0); Text(null);
                            writer.Write(true); writer.Write(false); for (int i = 0; i < 4; i++) Text(null);
                            break;
                        case 1024: writer.Write(true); writer.Write(_revision); break;
                        case 1025:
                            Check(reader.ReadInt64() == _revision); var documents = new SidecarNxmlDocument[reader.ReadInt32()];
                            for (int i = 0; i < documents.Length; i++) documents[i] = SidecarNxmlDocument.Decode(Bytes());
                            Check(reader.ReadInt32() == documents.Length); View[] replacements = new View[documents.Length];
                            for (int i = 0; i < documents.Length; i++) { Check(reader.ReadBoolean() && reader.ReadInt32() == i); replacements[i] = new(documents[i], Model()); }
                            _views = replacements; _revision++; break;
                        case 1026:
                            Check(reader.ReadInt64() == _revision && reader.ReadInt32() == _views.Length);
                            var updated = _views.ToArray();
                            for (int i = 0; i < updated.Length; i++) { Check(reader.ReadBoolean() && reader.ReadInt32() == i); updated[i] = updated[i] with { Model = Model() }; }
                            _views = updated; _revision++; break;
                        case 3840:
                            Check(reader.ReadInt64() == _revision); var reconciled = new View[reader.ReadInt32()];
                            for (int i = 0; i < reconciled.Length; i++)
                            {
                                Check(reader.ReadBoolean()); int previous = reader.ReadInt32(); byte[] source = Bytes();
                                var document = previous >= 0 ? _views[previous].Document : SidecarNxmlDocument.Decode(source);
                                reconciled[i] = new(document, Model());
                            }
                            _views = reconciled; _revision++; break;
                        case 1280:
                        case 3584:
                            Check(reader.ReadInt64() == _revision); writer.Write(true); writer.Write(_revision); writer.Write(0); break;
                        case 4096:
                        case 4355:
                            Check(reader.ReadInt64() == _revision && reader.ReadInt32() == 0); break;
                        case 2816:
                            Check(reader.ReadInt32() == 0); _ = Bytes(); int extensions = reader.ReadInt32();
                            for (int i = 0; i < extensions; i++) _ = Bytes();
                            Picks++; writer.Write(true); Text(_package); break;
                        case 1536:
                            _ = Bytes(); _ = Bytes(); Confirmations++; writer.Write(true); writer.Write(Confirmations != 2); break;
                        default: throw new InvalidOperationException("Unexpected standalone Host operation: " + request.ContractId);
                    }
                }
                Check(input.Position == input.Length);
                Check(Requests.TryAcceptReply(new(SidecarProtocol.Version,
                    frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult,
                    SidecarFrameTraits.None, frame.CorrelationId, SidecarDataMessages.EncodeBinaryResult(true,
                        new(SidecarWireCodecs.GeneratedDto, output.ToArray()), null))));
            }
            return ValueTask.CompletedTask;
        }
        public void Dispose() => Requests.Dispose();
    }
}
