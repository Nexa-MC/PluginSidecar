if (args is ["--remote-only"]) { await RemoteExecutionTests.Run(); return; }
await WasmTests.Run();
await EndToEndTests.Run();
await DefinitionTests.Run();
await RuntimeServicesTests.Run();
await ServiceProbeTests.Run();
await ActionConfirmationTests.Run();
await ScheduleWorkflowTests.Run();
await DurableEventTests.Run();
await ContentRuntimeTests.Run();
await ResourceRuntimeTests.Run();
await WasmWorkflowTests.Run();
await RemoteExecutionTests.Run();
await GuestSdkTests.Run();
await GuestConformanceTests.Run();
await CallbackSdkTests.Run();
await GuestCodecTests.Run();
await PluginSidecar.Tests.StorageTests.Run();
Console.WriteLine("PASS: NativeAOT runtime, authenticated offline package, atomic loading and durable storage.");
