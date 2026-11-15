if (args is ["--catalog-hold-lock", var catalogDirectory]) { await CatalogRevisionTests.HoldLock(catalogDirectory); return; }
if (args is ["--key-protection-only"]) { await KeyProtectionTests.Run(); return; }
if (args is ["--standalone-only"]) { await StandaloneRuntimeTests.Run(); return; }
if (args is ["--worker-only"]) { await WorkerPeerTests.Run(); return; }
if (args is ["--process-only"]) { await StandaloneRuntimeTests.RunProcess(); return; }
if (args is ["--remote-only"]) { await RemoteExecutionTests.Run(); return; }
if (args is ["--application-only"]) { await KeyProtectionTests.Run(); await ApplicationTests.Run(); return; }
await KeyProtectionTests.Run(); await ApplicationTests.Run();
await WasmTests.Run();
await EndToEndTests.Run();
await DefinitionTests.Run();
await RuntimeServicesTests.Run();
await ServiceProbeTests.Run();
await BusinessProviderTests.Run();
await ActionConfirmationTests.Run();
await ScheduleWorkflowTests.Run();
await DurableEventTests.Run();
await ContentRuntimeTests.Run();
await ResourceRuntimeTests.Run();
await WasmWorkflowTests.Run();
await WorkerPeerTests.Run();
await StandaloneRuntimeTests.Run();
await StandaloneRuntimeTests.RunProcess();
await RemoteExecutionTests.Run();
await GuestSdkTests.Run();
await GuestConformanceTests.Run();
await CallbackSdkTests.Run();
await GuestCodecTests.Run();
await PluginSidecar.Tests.StorageTests.Run();
Console.WriteLine("PASS: NativeAOT runtime, authenticated offline package, atomic loading and durable storage.");
