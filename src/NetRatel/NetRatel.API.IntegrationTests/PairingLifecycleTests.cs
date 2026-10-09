using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace NetRatel.API.IntegrationTests.SystemPairing;

/// <summary>Opt-in acceptance uses the actual current products, Client and normal PostgreSQL/bootstrap paths.</summary>
public sealed class PairingLifecycleTests
{
    [Fact]
    [Trait("category", "manual-integration")]
    [Trait("category", "PairingProductAcceptance")]
    public async Task Candidate_pair_proves_both_operator_flows_real_incident_replay_execution_and_offline_deletion()
    {
        if (Environment.GetEnvironmentVariable("NETRATEL_PAIRING_ACCEPTANCE") != "1")
        { Assert.Skip("Set NETRATEL_PAIRING_ACCEPTANCE=1 with explicit candidate source roots for the bounded real-product check."); return; }
        var source = RequiredDirectory("NETRATEL_PAIRING_SOURCE_ROOT");
        var companion = RequiredDirectory("RATELDESK_PAIRING_SOURCE_ROOT");
        var runtime = RequiredDirectory("NETRATEL_PAIRING_RUNTIME_ROOT");
        var script = Path.Combine(source, "tools", "ci", "run-pairing-acceptance.sh");
        Assert.True(File.Exists(script), "The current source's bounded acceptance entry point is missing.");
        var receiptPath = Path.Combine(runtime, "acceptance-receipt.json");
        Assert.False(File.Exists(receiptPath), "Choose a fresh disposable runtime directory; a previous receipt cannot prove this execution.");
        var start = new ProcessStartInfo("bash") { WorkingDirectory = source, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The actual product-pair acceptance process could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await Task.WhenAll(stdout, stderr);
        // Private process diagnostics stay in the owning fixture; never expose credential-bearing output.
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(receiptPath), "A successful real-product check must produce its safe acceptance receipt after cleanup.");
        Assert.True(new FileInfo(receiptPath).Length <= 64 * 1024, "Acceptance receipt exceeds the bounded safe summary size.");
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, TestContext.Current.CancellationToken));
        var stages = receipt.RootElement.GetProperty("stages");
        foreach (var stage in new[] { "pairedFromRatelDesk", "pairedFromNetRatel", "incidentReceiptReplay",
            "nativeAutomationResult", "sideEffectFreeTest", "deleteWhilePeerOffline" })
            Assert.Equal("passed", stages.GetProperty(stage).GetString());
        var sources = receipt.RootElement.GetProperty("sources");
        foreach (var product in new[] { "netratelSha", "rateldeskSha" })
            Assert.Matches("^[0-9a-f]{40}$", sources.GetProperty(product).GetString()!);
        Assert.Equal(await GitIdentityAsync(source), sources.GetProperty("netratelSha").GetString());
        Assert.Equal(await GitIdentityAsync(companion), sources.GetProperty("rateldeskSha").GetString());
        var result = receipt.RootElement.GetProperty("result");
        Assert.Equal(1, result.GetProperty("incidentCount").GetInt32());
        Assert.Equal(1, result.GetProperty("receiptCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("executionId").GetString()));
        Assert.True(result.GetProperty("resultReceived").GetBoolean());
        Assert.True(result.GetProperty("historyPreserved").GetBoolean());
        Assert.True(result.GetProperty("cleanupComplete").GetBoolean());
    }

    private static string RequiredDirectory(string name)
    {
        var path = Environment.GetEnvironmentVariable(name);
        Assert.True(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Directory.Exists(path),
            $"{name} must identify an existing explicit absolute directory.");
        return Path.GetFullPath(path!);
    }

    private static async Task<string> GitIdentityAsync(string source)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true };
        foreach (var argument in new[] { "-C", source, "rev-parse", "HEAD" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Candidate source identity could not be read.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken); await errors;
        Assert.Equal(0, process.ExitCode);
        var sha = (await output).Trim(); Assert.Matches("^[0-9a-f]{40}$", sha); return sha;
    }
}
