using System.Security.Cryptography;
using PluginSidecar.Protection;

internal static class KeyProtectionTests
{
    private static void Check(bool result) { if (!result) throw new InvalidOperationException("Catalog key protection check failed."); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    internal static async Task Run()
    {
        using var store = new FakeSecrets();
        byte[] key = RandomNumberGenerator.GetBytes(32), envelope;
        using (var protector = new PlatformPluginKeyProtector(store))
        {
            var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => protector.ProtectAsync(key, default).AsTask()));
            envelope = replies[0]; Check(envelope[0] == 4 && envelope.Length == 77 && store.Writes == 1);
            Check(replies.DistinctBy(Convert.ToHexString).Count() == 4);
            Check((await protector.UnprotectAsync(envelope, default)).AsSpan().SequenceEqual(key));
            await Reject<CryptographicException>(() => protector.ProtectAsync(new byte[31], default).AsTask());
        }
        using (var restarted = new PlatformPluginKeyProtector(store))
        {
            Check((await restarted.UnprotectAsync(envelope, default)).AsSpan().SequenceEqual(key));
            var reused = await restarted.ProtectAsync(key, default); Check(store.Writes == 1);
            Check(reused.AsSpan(1, 16).SequenceEqual(envelope.AsSpan(1, 16)));
            foreach (int index in new[] { 17, 29, 45, 76 })
            {
                var tampered = envelope.ToArray(); tampered[index] ^= 1;
                await Reject<CryptographicException>(() => restarted.UnprotectAsync(tampered, default).AsTask());
            }
            var changedScope = envelope.ToArray(); changedScope[0] = 2;
            await Reject<IOException>(() => restarted.UnprotectAsync(changedScope, default).AsTask());
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await Reject<OperationCanceledException>(() => restarted.UnprotectAsync(envelope, stop.Token).AsTask());
            var legacy = store.Legacy(key);
            Check((await restarted.UnprotectAsync(legacy, default)).AsSpan().SequenceEqual(key));
            Check(store.LegacyReads == 1);
            Check((await restarted.ProtectAsync(key, default))[0] == 4);
        }
        Check(store.Returned.All(bytes => bytes.All(value => value == 0)));
        using (var missing = new PlatformPluginKeyProtector(new FailingSecrets()))
        {
            await Reject<IOException>(() => missing.ProtectAsync(key, default).AsTask());
            await Reject<IOException>(() => missing.UnprotectAsync(envelope, default).AsTask());
        }
        if (OperatingSystem.IsWindows())
        {
            using var native = new PlatformPluginKeyProtector();
            byte[] current = await native.ProtectAsync(key, default);
            Check(current[0] == 3 && (await native.UnprotectAsync(current, default)).AsSpan().SequenceEqual(key));
            byte[] legacy = [1, .. PlatformPluginNativeProtection.Windows(key, true, true)];
            Check((await native.UnprotectAsync(legacy, default)).AsSpan().SequenceEqual(key));
            current[0] = 1;
            await Reject<CryptographicException>(() => native.UnprotectAsync(current, default).AsTask());
        }
        CryptographicOperations.ZeroMemory(key);
        Console.WriteLine("PASS: OS catalog key envelopes, restart, scope isolation, legacy import, tamper rejection, fail-closed storage and key retirement.");
    }
    private sealed class FailingSecrets : IPluginSecretStore
    {
        public ValueTask StoreAsync(Guid id, ReadOnlyMemory<byte> key, CancellationToken token) => ValueTask.FromException(new IOException("Locked"));
        public ValueTask<byte[]> ReadAsync(Guid id, bool legacy, CancellationToken token) => ValueTask.FromException<byte[]>(new IOException("Locked"));
    }
    internal sealed class FakeSecrets : IPluginSecretStore, IDisposable
    {
        internal int Writes, LegacyReads;
        private readonly Dictionary<(bool, Guid), byte[]> _values = [];
        internal readonly List<byte[]> Returned = [];
        public ValueTask StoreAsync(Guid id, ReadOnlyMemory<byte> key, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Writes++; _values.Add((false, id), key.ToArray()); return ValueTask.CompletedTask; }
        public ValueTask<byte[]> ReadAsync(Guid id, bool legacy, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_values.TryGetValue((legacy, id), out var key)) throw new IOException("Missing scope");
            if (legacy) LegacyReads++;
            byte[] returned = key.ToArray(); Returned.Add(returned); return ValueTask.FromResult(returned);
        }
        internal byte[] Legacy(byte[] key)
        {
            Guid id = Guid.NewGuid(); byte[] secret = RandomNumberGenerator.GetBytes(32); _values.Add((true, id), secret);
            byte[] envelope = new byte[77]; envelope[0] = 2; id.TryWriteBytes(envelope.AsSpan(1, 16));
            RandomNumberGenerator.Fill(envelope.AsSpan(17, 12));
            using var cipher = new AesGcm(secret, 16);
            cipher.Encrypt(envelope.AsSpan(17, 12), key, envelope.AsSpan(45), envelope.AsSpan(29, 16), envelope.AsSpan(0, 17));
            return envelope;
        }
        public void Dispose() { foreach (var bytes in _values.Values) CryptographicOperations.ZeroMemory(bytes); }
    }
}
