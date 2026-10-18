using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.ClientTools;

namespace NetRatel.Tests.ClientTools;

// Hosted Windows smoke: compile beside the exact relay extracted from the archive.
// The fixture child reads a synthetic credential only from its own environment.
public static class HttpMcpWindowsRelaySmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    public static async Task RunAsync(string powershell, string childScript, SecureString credential, string tokenHash)
    {
        var parentToken = Environment.GetEnvironmentVariable("NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN");
        await VerifyEarlyBytesAsync(powershell, childScript, credential, tokenHash).ConfigureAwait(false);
        await VerifyExitWithOpenInputAsync(powershell, childScript, credential, tokenHash).ConfigureAwait(false);
        await VerifyConsumerFailureAsync(powershell, childScript, credential, tokenHash).ConfigureAwait(false);
        await VerifyRequesterPurposeAsync(powershell, childScript, credential, tokenHash).ConfigureAwait(false);
        Require(parentToken == Environment.GetEnvironmentVariable("NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN"),
            "The relay changed the parent credential environment.");
    }

    private static ProcessStartInfo Start(string powershell, string script, string mode, string hash)
    {
        var start = new ProcessStartInfo(powershell);
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, "-Mode", mode, "-ExpectedHash", hash })
            start.ArgumentList.Add(arg);
        start.Environment["NETRATEL_SMOKE_PARENT_CONTROL"] = "must-not-reach-child";
        return start;
    }

    private static async Task VerifyEarlyBytesAsync(string powershell, string script, SecureString credential, string hash)
    {
        using var input = new GatedInput();
        using var output = new ObservedOutput();
        using var error = new MemoryStream();
        var start = Start(powershell, script, "gated", hash);
        var relaying = HttpMcpProcessRelay.RunAsync(start, credential, input, output, error);
        var first = await output.FirstLine.Task.WaitAsync(Deadline).ConfigureAwait(false);
        var childPid = ReadChildPid(first);
        try
        {
            Require(!relaying.IsCompleted, "The progress frame must arrive while the final response is gated.");
            Require(!start.Environment.ContainsKey("NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN"), "The start descriptor retained plaintext.");
            input.Send("continue\n");
            input.Complete();
            Require(await relaying.WaitAsync(Deadline).ConfigureAwait(false) == 0, "The synthetic relay child failed.");
            var expected = Progress(childPid) + "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"message\":\"café ☃\"}}\n";
            Require(output.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)), "The relay changed protocol bytes or order.");
            Require(error.ToArray().AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("fixture-error-µ\n")), "The relay changed diagnostic bytes.");
        }
        finally { StopFixtureChild(childPid); }
    }

    private static async Task VerifyExitWithOpenInputAsync(string powershell, string script, SecureString credential, string hash)
    {
        using var input = new GatedInput(); // Kept open: child exit must not wait for the consumer's stdin EOF.
        using var output = new ObservedOutput();
        using var error = new MemoryStream();
        var relaying = HttpMcpProcessRelay.RunAsync(Start(powershell, script, "exit", hash), credential, input, output, error);
        var childPid = ReadChildPid(await output.FirstLine.Task.WaitAsync(Deadline).ConfigureAwait(false));
        try
        {
            Require(await relaying.WaitAsync(Deadline).ConfigureAwait(false) == 0, "A child that exits with stdin open must finish.");
            Require(!IsRunning(childPid), "The completed relay left its child running.");
        }
        finally { StopFixtureChild(childPid); }
    }

    private static async Task VerifyConsumerFailureAsync(string powershell, string script, SecureString credential, string hash)
    {
        using var input = new GatedInput();
        using var output = new ObservedOutput();
        using var error = new MemoryStream();
        var relaying = HttpMcpProcessRelay.RunAsync(Start(powershell, script, "fault", hash), credential, input, output, error);
        var childPid = ReadChildPid(await output.FirstLine.Task.WaitAsync(Deadline).ConfigureAwait(false));
        try
        {
            Require(!relaying.IsCompleted, "The consumer failure fixture must still be in flight.");
            output.FailWrites = true;
            input.Send("continue\n"); // Keep input open while the output consumer fails.
            var observedFailure = false;
            try { await relaying.WaitAsync(Deadline).ConfigureAwait(false); }
            catch (IOException) { observedFailure = true; }
            Require(observedFailure, "The in-flight output failure was not propagated.");
            Require(!IsRunning(childPid), "The output failure left the relay's own child running.");
        }
        finally { StopFixtureChild(childPid); }
    }

    private static async Task VerifyRequesterPurposeAsync(string powershell, string script, SecureString credential, string hash)
    {
        using var input = new GatedInput();
        using var output = new ObservedOutput();
        using var error = new MemoryStream();
        var start = Start(powershell, script, "requester", hash);
        start.Environment["NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN"] = "inherited-primary-must-not-reach-child";
        var relaying = HttpMcpProcessRelay.RunAccessPrepareAsync(start, credential, input, output, error);
        var childPid = ReadChildPid(await output.FirstLine.Task.WaitAsync(Deadline).ConfigureAwait(false));
        try
        {
            Require(await relaying.WaitAsync(Deadline).ConfigureAwait(false) == 0, "The requester token purpose was not isolated.");
            Require(!start.Environment.ContainsKey("NETRATEL_MCP_ACCESS_REQUESTER_TOKEN") &&
                !start.Environment.ContainsKey("NETRATEL_MCP_CLIENT_INTEGRATION_TOKEN"), "The requester start descriptor retained a credential.");
        }
        finally { StopFixtureChild(childPid); }
    }

    private static string Progress(int pid)
        => "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progressToken\":\"fixture\",\"progress\":1,\"message\":\"café ☃\",\"childPid\":" + pid + "}}\n";

    private static int ReadChildPid(byte[] line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Require(root.GetProperty("method").GetString() == "notifications/progress", "The first child output was not a progress frame.");
        var pid = root.GetProperty("params").GetProperty("childPid").GetInt32();
        Require(pid > 0 && pid != Environment.ProcessId, "The fixture child identity is invalid.");
        return pid;
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void StopFixtureChild(int pid)
    {
        // Cleanup is limited to a PID explicitly emitted by the synthetic child started above.
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; } // The fixture has already exited and was reaped.
        using (process)
        {
            if (process.HasExited) return;
            try { process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            Require(process.WaitForExit(5000), "The synthetic fixture child did not stop during cleanup.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class GatedInput : Stream
    {
        private readonly Queue<byte[]> chunks = new();
        private readonly SemaphoreSlim available = new(0);
        private bool complete;

        public void Send(string value)
        {
            lock (chunks) chunks.Enqueue(Encoding.UTF8.GetBytes(value));
            available.Release();
        }

        public void Complete() { lock (chunks) complete = true; available.Release(); }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (chunks)
                {
                    if (chunks.Count > 0)
                    {
                        var chunk = chunks.Dequeue();
                        Require(chunk.Length <= count, "The fixture input chunk exceeds the relay buffer.");
                        Buffer.BlockCopy(chunk, 0, buffer, offset, chunk.Length);
                        return chunk.Length;
                    }
                    if (complete) return 0;
                }
                await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ObservedOutput : Stream
    {
        private readonly MemoryStream bytes = new();
        public readonly TaskCompletionSource<byte[]> FirstLine = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool FailWrites;
        public byte[] Bytes { get { lock (bytes) return bytes.ToArray(); } }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailWrites) throw new IOException("Synthetic output consumer closed.");
            lock (bytes)
            {
                Require(bytes.Length + count <= 16 * 1024, "Synthetic relay output exceeded its bound.");
                bytes.Write(buffer, offset, count);
                if (!FirstLine.Task.IsCompleted)
                {
                    var captured = bytes.ToArray();
                    var newline = Array.IndexOf(captured, (byte)'\n');
                    if (newline >= 0) FirstLine.TrySetResult(captured.AsSpan(0, newline + 1).ToArray());
                }
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length { get { lock (bytes) return bytes.Length; } }
        public override long Position { get => Length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
