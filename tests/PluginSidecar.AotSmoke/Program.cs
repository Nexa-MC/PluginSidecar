await WasmTests.Run();
await EndToEndTests.Run();
await DefinitionTests.Run();
await GuestSdkTests.Run();
await GuestConformanceTests.Run();
await CallbackSdkTests.Run();
await GuestCodecTests.Run();
await PluginSidecar.Tests.StorageTests.Run();
Console.WriteLine("PASS: NativeAOT runtime, authenticated offline package, atomic loading and durable storage.");
