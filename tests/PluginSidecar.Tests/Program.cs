using PluginSidecar.Contracts;

IPlugin plugin = new Probe();
using var stop = new CancellationTokenSource();
stop.Cancel();
try
{
    await plugin.StartAsync(stop.Token);
    throw new InvalidOperationException("Canceled startup was admitted.");
}
catch (OperationCanceledException) { }
await plugin.StartAsync(CancellationToken.None);
await plugin.StopAsync(CancellationToken.None);
Console.WriteLine("PASS: lifecycle contract preserves cancellation and stop.");

sealed class Probe : IPlugin
{
    private bool _started;
    public string Id => "example.probe";
    public ValueTask StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _started = true;
        return ValueTask.CompletedTask;
    }
    public ValueTask StopAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_started) throw new InvalidOperationException("Plugin was not started.");
        _started = false;
        return ValueTask.CompletedTask;
    }
}
