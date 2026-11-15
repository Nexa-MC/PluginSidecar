using System.Security.Cryptography;
using PluginSidecar.Protection;

internal static class MarketDeviceKeyTests
{
    internal static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-market-device-" + Guid.NewGuid().ToString("N"));
        using var secrets = new KeyProtectionTests.FakeSecrets();
        try
        {
            using var protector = new PlatformPluginKeyProtector(secrets);
            byte[] identity;
            var created = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => new PluginMarketDeviceKey(root, protector).OpenAsync())));
            try
            {
                Check(created[0].KeySize == 3072); identity = created[0].ExportSubjectPublicKeyInfo();
                Check(created.All(key => identity.AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo())));
            }
            finally { foreach (var key in created) key.Dispose(); }
            // Independently constructed owners must select the same persisted device.
            var keys = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => new PluginMarketDeviceKey(root, protector).OpenAsync()));
            foreach (var key in keys)
            { using (key) Check(identity.AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo())); }
            using (var restarted = new PlatformPluginKeyProtector(secrets))
            using (var key = await new PluginMarketDeviceKey(root, restarted).OpenAsync())
                Check(identity.AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()));
            string path = Path.Combine(root, "market-device.npkey");
            byte[] valid = await File.ReadAllBytesAsync(path);
            using var missingSecrets = new KeyProtectionTests.FakeSecrets();
            using (var wrong = new PlatformPluginKeyProtector(missingSecrets))
                await Reject(() => new PluginMarketDeviceKey(root, wrong).OpenAsync());
            Check((await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(valid));
            foreach (int offset in new[] { 0, valid.Length - 1 })
            {
                byte[] changed = valid.ToArray(); changed[offset] ^= 1;
                await File.WriteAllBytesAsync(path, changed);
                await Reject(() => new PluginMarketDeviceKey(root, protector).OpenAsync());
                Check((await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(changed));
            }
            await File.WriteAllBytesAsync(path, valid[..32]);
            await Reject(() => new PluginMarketDeviceKey(root, protector).OpenAsync());
            Check(new FileInfo(path).Length == 32);
            Check(!Directory.EnumerateFiles(root, "*.tmp").Any());
            using var stop = new CancellationTokenSource(); stop.Cancel();
            string cancelled = Path.Combine(root, "cancelled");
            await Reject(() => new PluginMarketDeviceKey(cancelled, protector).OpenAsync(stop.Token));
            Check(!Directory.Exists(cancelled));
            Console.WriteLine("PASS: Market RSA device identity persists across owners/restarts, rejects corruption and missing OS keys without rotation, and leaves no cancelled record.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task Reject(Func<Task<RSA>> action)
    {
        try { using var key = await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Invalid device identity admitted.");
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Market device identity assertion failed."); }
}
