using Nexa.Sidecar.Transport;
using PluginSidecar;
if (args is ["--version"]) { Console.WriteLine("PluginSidecar 0.2.0-alpha.6"); return 0; }
if (args is not ["--nexa-sidecar", "--endpoint", var endpoint])
{ Console.Error.WriteLine("Usage: PluginSidecar --nexa-sidecar --endpoint <host-endpoint>"); return 2; }
try
{
    using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await using var stream = await SidecarBootstrap.ConnectAsync(endpoint, Console.OpenStandardInput(), connect.Token);
    using var wire = new SidecarFrameTransport(stream);
    await new PluginExecutionPeer(wire, new RemoteExecutionRuntime()).RunAsync(default);
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
{ Console.Error.WriteLine("Plugin execution session failed: " + error.GetType().Name); return 1; }
