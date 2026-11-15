using System.Collections.Immutable;
using System.Security.Cryptography;
using Nexa.Plugin.MarketDelivery;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Protection;

internal static class MarketCatalogTests
{
    internal static async Task Run(byte[] bytes)
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-market-catalog-" + Guid.NewGuid().ToString("N"));
        using var publisher = RSA.Create(2048);
        using var device = RSA.Create(3072);
        using var other = RSA.Create(2048);
        using var secrets = new KeyProtectionTests.FakeSecrets();
        using var protector = new PlatformPluginKeyProtector(secrets);
        var clock = new Clock();
        using var package = VerifiedPluginPackage.Parse(bytes, "example.desktop", "1.0.0");
        var manifest = package.Manifest;
        var entitlement = new PluginEntitlement(manifest.PluginId, manifest.PluginVersion, "account-1", clock.Now.AddMinutes(-1), null);
        var delivery = MarketDeliveryIssuer.Issue(bytes, entitlement, device, publisher);
        using var catalog = new PluginMarketCatalog(root, protector, new(publisher, device, clock));
        Task<string> Save(EncryptedDelivery value, ImmutableArray<string> grants, string? revision, CancellationToken token = default)
            => catalog.SaveIfUnchangedAsync(value, entitlement.PluginId, entitlement.Version, entitlement.Subject, grants, revision, token);
        try
        {
            string first = await Save(delivery, manifest.RequestedPermissions, null);
            string path = catalog.Records().Single();
            byte[] protectedRecord = await File.ReadAllBytesAsync(path);
            var installed = await catalog.OpenInstalledAsync(entitlement.PluginId, default);
            Check(installed.Revision == first && installed.Subject == entitlement.Subject);
            Check(installed.Grants.SequenceEqual(manifest.RequestedPermissions));
            Check(installed.Delivery.Grant.AsSpan().SequenceEqual(delivery.Grant));
            Check(installed.Delivery.Signature.AsSpan().SequenceEqual(delivery.Signature));
            Check(installed.Delivery.Ciphertext.AsSpan().SequenceEqual(delivery.Ciphertext));
            using (var verified = catalog.OpenPackage(installed.Delivery, installed.Id, installed.Version, installed.Subject))
                Check(verified.Manifest.PluginId == manifest.PluginId);
            // The store has no HTTP client or account session: a new owner only needs local keys and the publisher pin.
            using (var restarted = new PlatformPluginKeyProtector(secrets))
            using (var offline = new PluginMarketCatalog(root, restarted, new(publisher, device, clock)))
                Check((await offline.OpenInstalledAsync(installed.Id, default)).Revision == first);
            await Reject(() => Save(delivery, manifest.RequestedPermissions, null));
            await Reject(() => Save(delivery, ["undeclared.permission"], first));
            await Reject(() => Save(delivery, default, first));
            await Reject(() => catalog.SaveIfUnchangedAsync(delivery, installed.Id, installed.Version, "other", [], first, default));
            foreach (var bad in new[]
            {
                delivery with { Signature = [.. delivery.Signature[..^1], (byte)(delivery.Signature[^1] ^ 1)] },
                delivery with { Ciphertext = [.. delivery.Ciphertext[..^1], (byte)(delivery.Ciphertext[^1] ^ 1)] },
                MarketDeliveryIssuer.Issue([1, 2, 3], entitlement, device, publisher)
            }) await Reject(() => Save(bad, [], first));
            Check((await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(protectedRecord));
            using (var wrongDevice = new PluginMarketCatalog(root, protector, new(publisher, other, clock)))
                await Reject(() => wrongDevice.OpenInstalledAsync(installed.Id, default));
            using (var wrongPublisher = new PluginMarketCatalog(root, protector, new(other, device, clock)))
                await Reject(() => wrongPublisher.OpenInstalledAsync(installed.Id, default));
            using (var stop = new CancellationTokenSource())
            { stop.Cancel(); await Reject(() => Save(delivery, [], first, stop.Token)); }
            Check(await catalog.ReadRevisionAsync(installed.Id, default) == first);

            using var competitor = new PluginMarketCatalog(root, protector, new(publisher, device, clock));
            var attempts = await Task.WhenAll(
                TrySave(() => Save(delivery, [], first)),
                TrySave(() => competitor.SaveIfUnchangedAsync(delivery, installed.Id, installed.Version, installed.Subject, [], first, default)));
            Check(attempts.Count(value => value is not null) == 1);
            string current = attempts.Single(value => value is not null)!;
            Check(!await catalog.RemoveIfUnchangedAsync(installed.Id, first, default));
            Check(await catalog.ReadRevisionAsync(installed.Id, default) == current);
            clock.Now += TimeSpan.FromDays(730);
            Check((await catalog.OpenInstalledAsync(installed.Id, default)).Revision == current);

            var expiring = MarketDeliveryIssuer.Issue(bytes, entitlement with { Expires = clock.Now.AddSeconds(1) }, device, publisher);
            current = await Save(expiring, [], current);
            clock.Now += TimeSpan.FromSeconds(2);
            await Reject(() => catalog.OpenInstalledAsync(installed.Id, default));
            Check(await catalog.ReadRevisionAsync(installed.Id, default) == current);
            byte[] corrupt = await File.ReadAllBytesAsync(path); corrupt[^1] ^= 1; await File.WriteAllBytesAsync(path, corrupt);
            await Reject(() => catalog.OpenInstalledAsync(installed.Id, default));
            await File.WriteAllBytesAsync(path, protectedRecord);
            Check(await catalog.RemoveIfUnchangedAsync(installed.Id, first, default));
            Check(catalog.Records().IsEmpty && !Directory.EnumerateFiles(root, "*.tmp").Any());
            Console.WriteLine("PASS: approved Market records retain signed ciphertext, restore offline, bind local grants, reject tampering/wrong keys/expired grants, and preserve concurrent revisions.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task<string?> TrySave(Func<Task<string>> action)
    { try { return await action(); } catch (IOException error) when (error.Message == "plugin.market.conflict") { return null; } }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or UnauthorizedAccessException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Invalid Market installation accepted.");
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Market catalog assertion failed."); }
    private sealed class Clock : TimeProvider
    { internal DateTimeOffset Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
}
