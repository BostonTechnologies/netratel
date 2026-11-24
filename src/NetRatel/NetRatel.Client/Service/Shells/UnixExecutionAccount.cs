using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Client.Service.Shells;

internal sealed class UnixExecutionAccount(string launcher, string uid, string gid, string display)
{
    private const string LauncherName = "netratel-run-as";
    private string Readiness => $"netratel-run-as-ready {uid} {gid}";
    public ObservedExecutionIdentity Identity => new(display, uid);

    public static bool IsAvailable => (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && GetEffectiveUserId() == 0 &&
        File.Exists(LauncherPath);

    private static string LauncherPath => Path.Combine(AppContext.BaseDirectory, LauncherName);

    public static async Task<UnixExecutionAccount> ResolveAsync(RunAsCredential credential, CancellationToken ct)
    {
        if (!IsAvailable || credential.Domain.Length != 0 || credential.Password.Length != 0)
            throw Unavailable();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var start = new ProcessStartInfo(LauncherPath, "--resolve")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        start.Environment.Clear();
        using var lookup = Process.Start(start) ?? throw Unavailable();
        try
        {
            await lookup.StandardInput.WriteLineAsync(credential.Username.AsMemory(), deadline.Token).ConfigureAwait(false);
            lookup.StandardInput.Close();
            var result = await lookup.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            await lookup.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var ids = result?.Split(' ');
            if (lookup.ExitCode != 0 || ids is not { Length: 2 } ||
                !ObservedExecutionIdentity.IsUnixAccountId(ids[0]) || !ObservedExecutionIdentity.IsUnixAccountId(ids[1]) ||
                (credential.ExpectedAccountId.Length != 0 && credential.ExpectedAccountId != ids[0]))
                throw Unavailable();
            return new UnixExecutionAccount(LauncherPath, ids[0], ids[1], credential.Username);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Unavailable();
        }
        finally
        {
            if (!lookup.HasExited) lookup.Kill();
        }
    }

    public void PrepareJob(ProcessStartInfo start, string? workingDirectory, IReadOnlyDictionary<string, string>? parameters)
    {
        var cwd = Quote(workingDirectory ?? string.Empty);
        start.Arguments = $"--job {uid} {gid} {cwd} {Quote(start.FileName)} {start.Arguments}";
        start.FileName = launcher;
        start.WorkingDirectory = "/";
        start.Environment.Clear();
        if (parameters is null) return;
        foreach (var (name, value) in parameters)
            start.Environment["NETRATEL_TARGET_PARAM_" + name] = value;
    }

    public string[] TerminalArguments(string? cwd, string shellCommand) =>
        [launcher, "--terminal", uid, gid, cwd ?? string.Empty, "/bin/sh", "-c", shellCommand];

    public async Task VerifyJobAsync(StreamReader status, CancellationToken ct)
    {
        var line = await status.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
        if (line != Readiness) throw Unavailable();
    }

    public async Task VerifyTerminalAsync(Stream terminal, CancellationToken ct)
    {
        var expected = Encoding.ASCII.GetBytes(Readiness);
        await ReadTerminalReadinessAsync(terminal, expected, ct).WaitAsync(TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
    }

    private static async Task ReadTerminalReadinessAsync(Stream terminal, byte[] expected, CancellationToken ct)
    {
        var next = new byte[1];
        foreach (var value in expected)
        {
            await terminal.ReadExactlyAsync(next, ct).ConfigureAwait(false);
            if (next[0] != value) throw Unavailable();
        }
        await terminal.ReadExactlyAsync(next, ct).ConfigureAwait(false);
        if (next[0] == '\r') await terminal.ReadExactlyAsync(next, ct).ConfigureAwait(false);
        if (next[0] != '\n') throw Unavailable();
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder().Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static InvalidOperationException Unavailable() => new("The selected Unix account could not be verified on this client.");

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
