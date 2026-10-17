using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.ClientTools;

// Compiled by the supported PowerShell launcher; uses only the installed runtime.
public static class HttpMcpProcessRelay
{
    private static readonly TimeSpan FailureShutdownBudget = TimeSpan.FromSeconds(3);

    public static Task<int> RunAsync(ProcessStartInfo start, SecureString credential,
        Stream input, Stream output, Stream error)
        => RunWithCredentialAsync(start, credential, input, output, error, "NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN");

    public static Task<int> RunAccessPrepareAsync(ProcessStartInfo start, SecureString credential,
        Stream input, Stream output, Stream error)
        => RunWithCredentialAsync(start, credential, input, output, error, "NETRATEL_MCP_ACCESS_REQUESTER_TOKEN");

    private static async Task<int> RunWithCredentialAsync(ProcessStartInfo start, SecureString credential,
        Stream input, Stream output, Stream error, string credentialEnvironmentVariable)
    {
        start.UseShellExecute = false;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        foreach (var key in new System.Collections.Generic.List<string>(start.Environment.Keys))
            if (key.StartsWith("NETRATEL", StringComparison.OrdinalIgnoreCase)) start.Environment.Remove(key);

        using var process = new Process { StartInfo = start };
        var cleartext = IntPtr.Zero;
        try
        {
            cleartext = Marshal.SecureStringToBSTR(credential);
            var token = Marshal.PtrToStringBSTR(cleartext);
            if (string.IsNullOrWhiteSpace(token) || token.Length > 16_384 || token.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidDataException("The protected integration credential is invalid.");
            // Child-only environment: never written to the parent environment, files or arguments.
            start.Environment[credentialEnvironmentVariable] = token;
            if (!process.Start()) throw new IOException("The supported bridge did not start.");
        }
        finally
        {
            start.Environment.Remove(credentialEnvironmentVariable);
            if (cleartext != IntPtr.Zero) Marshal.ZeroFreeBSTR(cleartext);
        }

        using var stopInput = new CancellationTokenSource();
        using var stopOutput = new CancellationTokenSource();
        var childInput = process.StandardInput.BaseStream;
        var outgoing = PumpAsync(process.StandardOutput.BaseStream, output, stopOutput.Token);
        var diagnostics = PumpAsync(process.StandardError.BaseStream, error, stopOutput.Token);
        var incoming = PumpInputAsync(process, input, childInput, stopInput.Token);
        var exited = process.WaitForExitAsync();
        try
        {
            // A failed consumer pipe must be observed while stdin is still
            // open; otherwise the child can block forever writing that pipe.
            var monitored = new System.Collections.Generic.List<Task> { incoming, outgoing, diagnostics, exited };
            while (!exited.IsCompleted)
            {
                var completed = await Task.WhenAny(monitored).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                monitored.Remove(completed);
            }
            await exited.ConfigureAwait(false);
            stopInput.Cancel();
            await Task.WhenAll(outgoing, diagnostics).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch
        {
            stopInput.Cancel();
            childInput.Close();
            // EOF allows the owned bridge to shut down normally first. If a
            // failed pipe prevents that, terminate only this spawned process;
            // no external consumer or process tree is controlled or retried.
            await StopOwnedChildAfterFailureAsync(process, exited).ConfigureAwait(false);
            throw;
        }
        finally
        {
            stopInput.Cancel();
            stopOutput.Cancel();
            childInput.Close();
            // Some console streams cannot cancel an already pending read.
            // Do not await those forever, but observe any later pump failure.
            ObserveLaterFailure(incoming);
            ObserveLaterFailure(outgoing);
            ObserveLaterFailure(diagnostics);
        }
    }

    private static async Task StopOwnedChildAfterFailureAsync(Process process, Task exited)
    {
        try
        {
            await exited.WaitAsync(FailureShutdownBudget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: false); }
                catch (InvalidOperationException) when (process.HasExited)
                { await exited.ConfigureAwait(false); }
            }
            await exited.WaitAsync(FailureShutdownBudget).ConfigureAwait(false);
        }
    }

    private static void ObserveLaterFailure(Task task)
        => _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task PumpInputAsync(Process process, Stream input, Stream childInput, CancellationToken ct)
    {
        try { await PumpAsync(input, childInput, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (IOException) when (ct.IsCancellationRequested || process.HasExited) { return; }
        finally { childInput.Close(); }
    }

    private static async Task PumpAsync(Stream input, Stream output, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) != 0)
        {
            await output.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
