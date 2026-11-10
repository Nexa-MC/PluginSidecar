using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using PluginSidecar.RuntimeAdapter;

static void Assert(bool value) { if (!value) throw new InvalidOperationException("Contract assertion failed."); }
Assert(!AbiVersions.IsStable(0) && !AbiVersions.IsStable(101) && !AbiVersions.IsStable(150));
Assert(AbiVersions.IsStable(100) && AbiVersions.IsStable(200));
await MarketOfflineTests.Run();
var error = new AbiError(AbiErrors.PERMISSION_DENIED, "denied", "PreInitialize", "abi.make", "example.plugin", Guid.NewGuid(), Guid.NewGuid(), false, ImmutableDictionary<string, string>.Empty);
Assert(AbiReply<AbiContextHandle>.Fail(error).Value.Value == 0);
var probe = new Probe(error);
var options = new InvocationOptions(Guid.NewGuid(), TimeSpan.FromSeconds(5), default);
var events = new Events100(probe);
await events.DeclareEvent(new("event", "schema", "digest", 1024, 16), options);
await events.Subscribe(new("provider", "event", "schema", new(9), true, 1, 16), options);
await events.ExportService(new("svc", "contract", 1, "digest", [], new AbiValue.Null()), new(9), options);
await events.Publish(new(ulong.MaxValue), new AbiValue.Null(), options);
Assert(((AbiValue.UInt64)((AbiValue.Map)probe.Arguments!).Value["handle"]).Value == ulong.MaxValue);
await events.Unsubscribe(new(1), options);
var ui = new Ui100(probe);
await ui.DeclareSurface(new("surface", new AbiValue.Null(), []), options);
await ui.DeclareCommand(new("command", "title"), new(9), options);
await ui.DeclarePlacement(new("placement", "surface", "slot"), options);
await ui.DeclareSettings(new("settings", "schema", "digest", new AbiValue.Null()), options);
await ui.PatchState(new(1), ulong.MaxValue, new AbiValue.Null(), options);
await ui.SetCommandState(new(1), new(true, false), options);
var plugins = new InterPlugin100(probe);
await plugins.Describe(new("provider", "svc"), options);
await plugins.Bind(new("provider", "svc", 1, 2, true), options);
await plugins.Call(new(1), 42, new AbiValue.Null(), options);
await plugins.Release(new(1), options);
var failed = await plugins.RebindOptional(new("provider", "svc", 1, 2, false), options);
Assert(ReferenceEquals(failed.Error, error));
Assert(probe.Calls.SequenceEqual(Enumerable.Range(1, 5).Select(i => ("nexa.events/100", (uint)i)).Concat(Enumerable.Range(1, 6).Select(i => ("nexa.ui/100", (uint)i))).Concat(Enumerable.Range(1, 5).Select(i => ("nexa.interplugin/100", (uint)i)))));
Assert(probe.LastOptions == options);
var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "HelloPlugin.dll"));
using var loaded = MemoryPlugin.LoadVerifiedPayloads("HelloPlugin", "HelloPlugin.Plugin", new Dictionary<string, byte[]> { ["HelloPlugin"] = bytes });
var context = new PreInitContext(new("example.plugin", "1.0.0", Guid.NewGuid()), 1, probe, probe, probe, probe, default, TimeSpan.FromSeconds(15));
var negotiation = await loaded.Entry.OnPreInitialize(context);
Assert(!negotiation.Success && ReferenceEquals(negotiation.Error, error) && probe.MakeCalls == 1);
Assert(loaded.Entry is IPluginBootstrapV1); // Bootstrap type identity is shared across the ALC.
loaded.Dispose();
bool disposed = false; try { _ = loaded.Entry; } catch (ObjectDisposedException) { disposed = true; }
Assert(disposed);
bool missing = false; try { using var bad = MemoryPlugin.LoadVerifiedPayloads("Missing", "Plugin", new Dictionary<string, byte[]>()); } catch (FileNotFoundException) { missing = true; }
Assert(missing);
bool entry = false; try { using var bad = MemoryPlugin.LoadVerifiedPayloads("HelloPlugin", "Does.Not.Exist", new Dictionary<string, byte[]> { ["HelloPlugin"] = bytes }); } catch (TypeLoadException) { entry = true; }
Assert(entry);
Console.WriteLine("PASS: stable versions, failure handles, all 16 ABI 100 dispatch slots, exact UInt64, Host failure propagation, CoreCLR memory loading and Bootstrap identity.");

sealed class Probe(AbiError error) : EventAbility, UiAbility, InterPluginAbility, IAbiNegotiatorV1, IDiagnosticsV1, IResourcesV1, ICallbacksV1
{
    public AbiContextHandle Context => new(1);
    public List<(string, uint)> Calls { get; } = [];
    public AbiValue? Arguments; public InvocationOptions? LastOptions; public int MakeCalls;
    public ValueTask<AbiReply<AbiValue>> Invoke(string contractId, uint methodId, AbiValue arguments, InvocationOptions options)
    { Calls.Add((contractId, methodId)); Arguments = arguments; LastOptions = options; return ValueTask.FromResult(AbiReply<AbiValue>.Fail(error)); }
    public ValueTask<AbiReply<AbiContextHandle>> GetABIContext(uint version, InvocationOptions options) => ValueTask.FromResult(AbiReply<AbiContextHandle>.Ok(new(2)));
    public ValueTask<AbiReply<ExperimentRef>> GetExpermentABI(Guid id, InvocationOptions options) => ValueTask.FromResult(AbiReply<ExperimentRef>.Fail(error));
    public ValueTask<AbiReply<AbiContextHandle>> MakeABIContext(AbiContextHandle draft, ImmutableArray<ExperimentRef> experiments, MakeOptions options, CancellationToken cancellation)
    { MakeCalls++; return ValueTask.FromResult(AbiReply<AbiContextHandle>.Fail(error)); }
    public ValueTask<AbiReply<Unit>> ReportDiagnostic(Finding finding, InvocationOptions options) => ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    public ValueTask<AbiReply<RegistrationHandle>> RegisterCleanup(CallbackHandle callback, InvocationOptions options) => ValueTask.FromResult(AbiReply<RegistrationHandle>.Fail(error));
    public CallbackHandle Register(string contractId, Func<AbiValue, InvocationOptions, ValueTask<AbiReply<AbiValue>>> callback) => new(9);
    public void Release(CallbackHandle callback) { }
}
