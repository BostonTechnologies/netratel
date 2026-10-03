using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service;

internal readonly record struct BoundedProcessResult(int ExitCode, string Output, string Error);

internal static class BoundedProcessRunner
{
    internal const int MaxCapturedOutputCharacters = 64 * 1024;
    internal const string TruncationMarker = "\n[output truncated]";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(1);

    internal static async Task<BoundedProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout), "A finite positive process timeout is required.");
        if (startInfo.UseShellExecute || !startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
            throw new ArgumentException("The process must redirect both output streams without shell execution.", nameof(startInfo));

        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Establish the deadline before process creation or either redirected read.
        deadline.CancelAfter(timeout);
        using var drains = new CancellationTokenSource();
        using var process = new Process { StartInfo = startInfo };
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        Task? completionTask = null;
        var started = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started)
                throw new InvalidOperationException($"Unable to start {startInfo.FileName} to {operation}.");

            outputTask = DrainAsync(process.StandardOutput, drains.Token, deadline.Token);
            errorTask = DrainAsync(process.StandardError, drains.Token, deadline.Token);
            completionTask = Task.WhenAll(process.WaitForExitAsync(), outputTask, errorTask);
            // EOF can lag behind process exit when a descendant inherits the pipes.
            await completionTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            return new BoundedProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
        }
        catch (Exception failure)
        {
            var cleanupFailure = started
                ? await TerminateAndCleanupAsync(process, outputTask, errorTask, drains).ConfigureAwait(false)
                : null;
            ObserveFault(completionTask);
            var cleanupDetail = cleanupFailure is null ? "" : $" Cleanup failed: {cleanupFailure.Message}";
            if (failure is OperationCanceledException && deadline.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException($"Cancelled while trying to {operation}.{cleanupDetail}", cleanupFailure, cancellationToken);
                throw new TimeoutException($"Timed out while trying to {operation}.{cleanupDetail}", cleanupFailure);
            }

            if (cleanupFailure is not null)
                throw new IOException($"Failed while trying to {operation}.{cleanupDetail}", new AggregateException(failure, cleanupFailure));
            throw;
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken cancellationToken, CancellationToken executionToken)
    {
        var captured = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while (true)
        {
            // Also bound a stream whose reads keep completing synchronously.
            executionToken.ThrowIfCancellationRequested();
            count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            var retained = Math.Min(count, MaxCapturedOutputCharacters - captured.Length);
            captured.Append(buffer, 0, retained);
            truncated |= retained < count;
        }

        if (truncated)
            captured.Append(TruncationMarker);
        return captured.ToString();
    }

    private static async Task<Exception?> TerminateAndCleanupAsync(
        Process process,
        Task<string>? outputTask,
        Task<string>? errorTask,
        CancellationTokenSource drains)
    {
        Exception? killFailure = null;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException exception)
        {
            // Only an already-exited parent is benign; preserve other kill failures.
            killFailure = process.HasExited ? null : exception;
        }
        catch (Exception exception) when (exception is Win32Exception or NotSupportedException or AggregateException)
        {
            // Carry termination evidence to the operation's thrown exception.
            killFailure = exception;
        }

        Exception? cleanupFailure = null;
        Task? cleanupTask = null;
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        try
        {
            cleanupTask = Task.WhenAll(
                process.WaitForExitAsync(cleanup.Token),
                (Task?)outputTask ?? Task.CompletedTask,
                (Task?)errorTask ?? Task.CompletedTask);
            await cleanupTask.WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            // A drain can observe the execution deadline after a synchronous read.
            // WhenAll has still awaited the independently bounded process reap.
            cleanupFailure = cleanup.IsCancellationRequested
                ? new TimeoutException("Process termination or redirected-stream cleanup exceeded its one-second bound.")
                : outputTask?.IsCanceled == true || errorTask?.IsCanceled == true ? null : exception;
        }
        catch (Exception exception)
        {
            // Report failed reaping or draining alongside the original operation failure.
            cleanupFailure = exception;
        }
        finally
        {
            drains.Cancel();
            // Do not await a read again after the independent cleanup deadline.
            ObserveFault(outputTask);
            ObserveFault(errorTask);
            ObserveFault(cleanupTask);
        }

        return killFailure is null ? cleanupFailure
            : cleanupFailure is null ? killFailure
            : new AggregateException(killFailure, cleanupFailure);
    }

    private static void ObserveFault(Task? task)
    {
        if (task is null)
            return;
        _ = task.ContinueWith(static completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
