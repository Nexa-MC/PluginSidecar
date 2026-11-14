using System.Diagnostics;
using System.Security.Cryptography;
using PluginSidecar.Application;
using PluginSidecar.Packages;
using PluginSidecar.Protection;

internal static class CatalogRevisionTests
{
    internal static async Task HoldLock(string directory)
    {
        using var lease = new FileStream(Path.Combine(directory, ".catalog.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("locked");
        await Console.Out.FlushAsync();
        await Console.In.ReadLineAsync();
    }

    internal static async Task Run(byte[] bytes)
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-catalog-revisions-" + Guid.NewGuid().ToString("N"));
        using var secrets = new KeyProtectionTests.FakeSecrets();
        using var package = VerifiedPluginPackage.Parse(bytes, "example.desktop", "1.0.0");
        string id = package.Manifest.PluginId, version = package.Manifest.PluginVersion;
        var grants = package.Manifest.RequestedPermissions;
        using var first = new PluginOfflineCatalog(root, new PlatformPluginKeyProtector(secrets));
        using var second = new PluginOfflineCatalog(root, new PlatformPluginKeyProtector(secrets));
        try
        {
            Check(await first.ReadRevisionAsync(id, default) is null);
            string initial = await first.SaveIfUnchangedAsync(bytes, id, version, grants, null, default);
            string path = first.Records().Single();
            Check(initial == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant());
            using var saved = await second.OpenAsync(path, default);
            Check(saved.Revision == initial && saved.Package.AsSpan().SequenceEqual(bytes));
            await Conflict(() => second.SaveIfUnchangedAsync(bytes, id, version, grants, null, default));

            // Even identical package bytes produce a new revision: stale rollback must not erase a reinstall.
            string replacement = await second.SaveIfUnchangedAsync(bytes, id, version, grants, initial, default);
            Check(replacement != initial);
            await Conflict(() => first.SaveIfUnchangedAsync(saved.Package, id, version, saved.Grants, initial, default));
            Check(!await first.RemoveIfUnchangedAsync(id, initial, default));
            Check(await first.ReadRevisionAsync(id, default) == replacement);
            string rolledBack = await first.SaveIfUnchangedAsync(saved.Package, id, version, saved.Grants, replacement, default);
            using (var restored = await second.OpenAsync(path, default))
                Check(restored.Revision == rolledBack && restored.Grants.SequenceEqual(saved.Grants));

            // Independent catalog objects compete for the same observed revision.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<string?> Write(PluginOfflineCatalog catalog)
            {
                await start.Task;
                try { return await catalog.SaveIfUnchangedAsync(bytes, id, version, grants, rolledBack, default); }
                catch (IOException error) when (error.Message == "plugin.catalog.conflict") { return null; }
            }
            Task<string?> left = Write(first), right = Write(second);
            start.SetResult();
            string winner = (await Task.WhenAll(left, right)).Where(value => value is not null).Single()!;
            Check(await first.ReadRevisionAsync(id, default) == winner);

            await ExternalLock(root, first, bytes, id, version, winner);
            Check(await first.ReadRevisionAsync(id, default) != winner);
            // Legacy/unconditional writers also invalidate conditional mutations.
            string current = (await first.ReadRevisionAsync(id, default))!;
            await second.SaveAsync(bytes, id, version, grants, default);
            Check(!await first.RemoveIfUnchangedAsync(id, current, default));
            current = (await first.ReadRevisionAsync(id, default))!;
            Check(await second.RemoveIfUnchangedAsync(id, current, default));
            Check(!await first.RemoveIfUnchangedAsync(id, current, default));
            Check(first.Records().IsEmpty && await first.ReadRevisionAsync(id, default) is null);
            await Reject<ArgumentException>(() => first.RemoveIfUnchangedAsync(id, "invalid", default));
            await Reject<ArgumentException>(() => first.SaveIfUnchangedAsync(bytes, id, version, grants, new string('z', 64), default));
            Check(!Directory.EnumerateFiles(root, "*.tmp").Any());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: offline catalog revisions, conditional rollback, competing writers, cross-process lock cancellation and stale deletion rejection.");
    }

    private static async Task ExternalLock(string root, PluginOfflineCatalog catalog, byte[] bytes,
        string id, string version, string revision)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add("--catalog-hold-lock"); start.ArgumentList.Add(root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Catalog lock process did not start.");
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            Check(await process.StandardOutput.ReadLineAsync(timeout.Token) == "locked");
            string record = catalog.Records().Single();
            byte[] original = await File.ReadAllBytesAsync(record, timeout.Token);
            foreach (Func<CancellationToken, Task> operation in new Func<CancellationToken, Task>[]
            {
                token => catalog.SaveAsync(bytes, id, version, [], token),
                token => catalog.SaveIfUnchangedAsync(bytes, id, version, [], revision, token),
                token => catalog.RemoveAsync(id, token),
                token => catalog.RemoveIfUnchangedAsync(id, revision, token),
                token => catalog.ReadRevisionAsync(id, token),
                async token => { using var ignored = await catalog.OpenAsync(record, token); }
            })
            {
                using var stop = new CancellationTokenSource();
                Task waiting = operation(stop.Token);
                Check(!waiting.IsCompleted);
                stop.Cancel();
                await Reject<OperationCanceledException>(() => waiting.WaitAsync(timeout.Token));
                Check((await File.ReadAllBytesAsync(record, timeout.Token)).AsSpan().SequenceEqual(original));
            }
            Task<string> accepted = catalog.SaveIfUnchangedAsync(bytes, id, version, [], revision, timeout.Token);
            Check(!accepted.IsCompleted);
            await process.StandardInput.WriteLineAsync("release");
            await process.StandardInput.FlushAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
            Check(await accepted != revision);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    private static async Task Conflict(Func<Task> action)
    {
        try { await action(); }
        catch (IOException error) when (error.Message == "plugin.catalog.conflict") { return; }
        throw new InvalidOperationException("A stale catalog write succeeded.");
    }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Check(bool value)
    { if (!value) throw new InvalidOperationException("Offline catalog revision assertion failed."); }
}
