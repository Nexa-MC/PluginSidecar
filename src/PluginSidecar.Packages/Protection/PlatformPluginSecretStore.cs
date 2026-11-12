using System.Diagnostics;
using System.Security.Cryptography;

namespace PluginSidecar.Protection;

internal sealed class PlatformPluginSecretStore : IPluginSecretStore
{
    public async ValueTask StoreAsync(Guid id, ReadOnlyMemory<byte> key, CancellationToken token)
    {
        if (OperatingSystem.IsMacOS())
        {
            byte[] owned = key.ToArray();
            await MacAsync(() =>
            {
                try { PlatformPluginNativeProtection.MacStore(id, owned); return []; }
                finally { CryptographicOperations.ZeroMemory(owned); }
            }, token).ConfigureAwait(false);
        }
        else if (OperatingSystem.IsLinux()) _ = await SecretToolAsync(id, false, Convert.ToBase64String(key.Span), token).ConfigureAwait(false);
        else throw new PlatformNotSupportedException("No OS catalog key store is available.");
    }

    public async ValueTask<byte[]> ReadAsync(Guid id, bool legacy, CancellationToken token)
    {
        if (OperatingSystem.IsMacOS()) return await MacAsync(() => PlatformPluginNativeProtection.MacRead(id, legacy), token).ConfigureAwait(false);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("No OS catalog key store is available.");
        string value = await SecretToolAsync(id, legacy, null, token).ConfigureAwait(false);
        try { return Convert.FromBase64String(value.Trim()); }
        catch (FormatException) { throw new CryptographicException("Invalid OS catalog key."); }
    }

    private static async Task<byte[]> MacAsync(Func<byte[]> operation, CancellationToken token)
    {
        // Always run the owned cleanup even if cancellation races task scheduling.
        var work = Task.Run(operation);
        try { return await work.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false); }
        catch
        {
            _ = DiscardLateResult(work);
            throw;
        }
    }
    private static async Task DiscardLateResult(Task<byte[]> task)
    {
        try { CryptographicOperations.ZeroMemory(await task.ConfigureAwait(false)); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
    }

    private static async Task<string> SecretToolAsync(Guid id, bool legacy, string? secret, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists("/usr/bin/secret-tool")) throw new IOException("Install libsecret-tools and unlock the OS key store to retain plugins offline.");
        var start = new ProcessStartInfo("/usr/bin/secret-tool")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(secret is null ? "lookup" : "store");
        if (secret is not null) start.ArgumentList.Add("--label=NexaCL PluginSidecar catalog encryption");
        start.ArgumentList.Add("application"); start.ArgumentList.Add(legacy ? "NexaCL" : "NexaCL.PluginSidecar");
        start.ArgumentList.Add("key-id"); start.ArgumentList.Add(id.ToString("N"));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("OS catalog key store is unavailable."); }
        catch (System.ComponentModel.Win32Exception) { throw new IOException("OS catalog key-store client could not start."); }
        using (process)
        {
            var output = ReadSmallAsync(process.StandardOutput, budget.Token);
            var errors = ReadSmallAsync(process.StandardError, budget.Token);
            var exited = process.WaitForExitAsync(budget.Token);
            try
            {
                if (secret is not null) await process.StandardInput.WriteAsync(secret.AsMemory(), budget.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                await Task.WhenAll(output, errors, exited).ConfigureAwait(false);
                if (process.ExitCode != 0 || secret is null && output.Result.Length == 0) throw new IOException("Catalog key is missing or the OS key store is locked.");
                return output.Result;
            }
            catch
            {
                budget.Cancel();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { await Task.WhenAll(output, errors, exited).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
                throw;
            }
        }
    }

    private static async Task<string> ReadSmallAsync(StreamReader reader, CancellationToken token)
    {
        char[] text = new char[1025]; int length = 0;
        while (length < text.Length)
        {
            int count = await reader.ReadAsync(text.AsMemory(length), token).ConfigureAwait(false);
            if (count == 0) return new string(text, 0, length);
            length += count;
        }
        throw new IOException("OS key-store reply exceeded its budget.");
    }
}
