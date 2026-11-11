using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using PluginSidecar.RuntimeAdapter;

static void Assert(bool value) { if (!value) throw new InvalidOperationException("Contract assertion failed."); }
Assert(!AbiVersions.IsStable(0) && !AbiVersions.IsStable(101) && !AbiVersions.IsStable(150));
Assert(AbiVersions.IsStable(100) && AbiVersions.IsStable(200));
await MarketOfflineTests.Run();
await ContextTests.Run();
await SurfaceTests.Run();
await AuthorityTests.Run();
await LoadingTests.Run();
await WasmTests.Run();
await EndToEndTests.Run();
await DefinitionTests.Run();
await RuntimeServicesTests.Run();
await ActionConfirmationTests.Run();
await ScheduleWorkflowTests.Run();
await DurableEventTests.Run();
await ContentRuntimeTests.Run();
await ResourceRuntimeTests.Run();
await WasmWorkflowTests.Run();
await GuestSdkTests.Run();
await GuestConformanceTests.Run();
await CallbackSdkTests.Run();
await GuestCodecTests.Run();
await PluginSidecar.Tests.StorageTests.Run();
var error = new AbiError(AbiErrors.PERMISSION_DENIED, "denied", "PreInitialize", "abi.make", "example.plugin", Guid.NewGuid(), Guid.NewGuid(), false, ImmutableDictionary<string, string>.Empty);
Assert(AbiReply<AbiContextHandle>.Fail(error).Value.Value == 0);
var probe = new Probe(error);
var options = new InvocationOptions(Guid.NewGuid(), TimeSpan.FromSeconds(5), default);
var events = new Events100(probe);
await events.DeclareEvent(new("event", "schema", "digest", 1024, 16), options);
await events.Subscribe(new EventSubscription("provider", "event", "schema", new(9), true, 1, 16), options);
await events.Publish(new(ulong.MaxValue), new AbiValue.Null(), options);
Assert(((AbiValue.UInt64)((AbiValue.Map)probe.Arguments!).Value["handle"]).Value == ulong.MaxValue);
await events.Unsubscribe(new RegistrationHandle(1), options);
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
await plugins.ExportService(new("svc", "contract", 1, "digest", [], new AbiValue.Null()), new(9), options);
await plugins.Probe(new(1), 3, new AbiValue.Null(), options);
var diagnostics = new Diagnostics100(probe);
await diagnostics.Report(new Diagnostic("test", "test", "info"), options);
await diagnostics.ReportFinding(new("test", "test", false), options);
await diagnostics.QueryRegistrations(options);
await diagnostics.QueryBindingStatus(options);
await new Resources100(probe).RegisterCleanup(new CallbackHandle(9), options);
Assert(probe.Calls.SequenceEqual(new[] {1, 2, 4, 5}.Select(i => ("nexa.events/100", (uint)i)).Concat(Enumerable.Range(1, 6).Select(i => ("nexa.ui/100", (uint)i))).Concat(Enumerable.Range(1, 7).Select(i => ("nexa.interplugin/100", (uint)i))).Concat(Enumerable.Range(1, 4).Select(i => ("nexa.diagnostics/100", (uint)i))).Append(("nexa.resources/100", 1u))));
Assert(probe.LastOptions == options);
var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "HelloPlugin.dll"));
using var loaded = MemoryPlugin.LoadTrustedDevelopmentPayloads("HelloPlugin", "HelloPlugin.Plugin", new Dictionary<string, byte[]> { ["HelloPlugin"] = bytes, ["Nexa.Plugin.Sdk.Abi100"] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Nexa.Plugin.Sdk.Abi100.dll")) });
var context = new PreInitContext(new("example.plugin", "1.0.0", Guid.NewGuid()), 1, probe, probe, probe, probe, default, TimeSpan.FromSeconds(15));
var negotiation = await loaded.Entry.OnPreInitialize(context);
Assert(!negotiation.Success && ReferenceEquals(negotiation.Error, error) && probe.MakeCalls == 1);
Assert(loaded.Entry is IPluginBootstrapV1); // Bootstrap type identity is shared across the ALC.
loaded.Dispose();
bool disposed = false; try { _ = loaded.Entry; } catch (ObjectDisposedException) { disposed = true; }
Assert(disposed);
bool missing = false; try { using var bad = MemoryPlugin.LoadTrustedDevelopmentPayloads("Missing", "Plugin", new Dictionary<string, byte[]>()); } catch (FileNotFoundException) { missing = true; }
Assert(missing);
bool entry = false; try { using var bad = MemoryPlugin.LoadTrustedDevelopmentPayloads("HelloPlugin", "Does.Not.Exist", new Dictionary<string, byte[]> { ["HelloPlugin"] = bytes, ["Nexa.Plugin.Sdk.Abi100"] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Nexa.Plugin.Sdk.Abi100.dll")) }); } catch (TypeLoadException) { entry = true; }
Assert(entry);
Console.WriteLine("PASS: stable versions, failure handles, all 22 callable ABI 100 dispatch slots, exact UInt64, Host failure propagation, CoreCLR memory loading and Bootstrap identity.");

sealed class Probe(AbiError error) : EventAbility, UiAbility, InterPluginAbility, IAbiNegotiatorV1, IDiagnosticsV1, IResourcesV1, ICallbacksV1, ABIContext
{
    public AbiContextHandle Context => new(1);
    public AbiContextInfo Info { get; } = new(new(1), Guid.NewGuid(), new("example.plugin", "1.0.0", Guid.NewGuid()), 100, [], [], Guid.NewGuid(), "digest", "test", []);
    public AbiReply<IAbilityV1> GetAbility(string contractId) => AbiReply<IAbilityV1>.Fail(error);
    public AbiReply<IAbilityV1> GetExperiment(Guid expId, string contractId) => AbiReply<IAbilityV1>.Fail(error);
    public List<(string, uint)> Calls { get; } = [];
    public AbiValue? Arguments; public InvocationOptions? LastOptions; public int MakeCalls;
    public ValueTask<AbiReply<AbiValue>> Invoke(string contractId, uint methodId, AbiValue arguments, InvocationOptions options)
    { Calls.Add((contractId, methodId)); Arguments = arguments; LastOptions = options; return ValueTask.FromResult(AbiReply<AbiValue>.Fail(error)); }
    public ValueTask<AbiReply<ABIContext>> GetABIContext(uint version, InvocationOptions options) => ValueTask.FromResult(AbiReply<ABIContext>.Ok(this));
    public ValueTask<AbiReply<ExperimentRef>> GetExpermentABI(Guid id, InvocationOptions options) => ValueTask.FromResult(AbiReply<ExperimentRef>.Fail(error));
    public ValueTask<AbiReply<ABIContext>> MakeABIContext(ABIContext draft, ImmutableArray<ExperimentRef> experiments, MakeOptions options, CancellationToken cancellation)
    { MakeCalls++; return ValueTask.FromResult(AbiReply<ABIContext>.Fail(error)); }
    public ValueTask<AbiReply<Unit>> ReportDiagnostic(Finding finding, InvocationOptions options) => ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    public ValueTask<AbiReply<RegistrationHandle>> RegisterCleanup(CallbackHandle callback, InvocationOptions options) => ValueTask.FromResult(AbiReply<RegistrationHandle>.Fail(error));
    public CallbackHandle Register(string contractId, Func<AbiValue, InvocationOptions, ValueTask<AbiReply<AbiValue>>> callback) => new(9);
    public void Release(CallbackHandle callback) { }
}
