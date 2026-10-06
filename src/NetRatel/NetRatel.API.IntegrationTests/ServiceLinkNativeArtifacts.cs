using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

/// <summary>Resolve prebuilt artifacts from the same exact checkout; never build or substitute an agent inside a test.</summary>
internal sealed record ServiceLinkNativeArtifacts(string Runner, string CandidateSourceSha, string ClientAssemblySha256)
{
    public static async Task<ServiceLinkNativeArtifacts> ResolveAsync(CancellationToken ct)
    {
        var repository = FindRepositoryRoot();
        var source = await GitAsync(repository, ["rev-parse", "HEAD"], ct);
        if (source.Length != 40 || source.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("The required native proof has no exact source commit.");
        var integrationSource = typeof(ServiceLinkNativeArtifacts).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "ServiceLinkProofSourceSha")?.Value;
        if (integrationSource != source)
            throw new InvalidOperationException("Rebuild the integration assembly from this exact committed checkout before physical proof.");
        var testedSource = Environment.GetEnvironmentVariable("NETRATEL_REVIEW_TEST_MERGE_SHA");
        if (testedSource is not null && testedSource != source)
            throw new InvalidOperationException("The declared CI test checkout differs from the actual candidate source.");
        var configuredSource = Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_CANDIDATE_SHA");
        if (configuredSource is not null && configuredSource != source)
            throw new InvalidOperationException("The native proof override differs from the actual test checkout.");
        // Intermediate development builds remain possible. Final physical evidence requires
        // committed production/test source, not a HEAD stamp pasted onto dirty implementation.
        var dirty = await GitAsync(repository, ["status", "--porcelain", "--untracked-files=normal"], ct);
        if (dirty.Length != 0)
            throw new InvalidOperationException("Commit the actual reviewed source before running the required physical proof.");
        var configuration = typeof(ServiceLinkNativeArtifacts).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        if (configuration is not ("Debug" or "Release"))
            throw new InvalidOperationException("The native proof has an unsupported test build configuration.");
        var runner = Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_NATIVE_RUNNER")
            ?? Path.Combine(repository, "src", "NetRatel", "NetRatel.ServiceLink.NativeRunner", "bin", configuration,
                "net10.0", "NetRatel.ServiceLink.NativeRunner.dll");
        var expectedClient = Path.Combine(repository, "src", "NetRatel", "NetRatel.Client", "bin", configuration, "net10.0", "NetRatel.Client.dll");
        if (!Path.IsPathFullyQualified(runner) || Path.GetExtension(runner) != ".dll" || !File.Exists(runner) || !File.Exists(expectedClient))
            throw new InvalidOperationException("The mandatory same-checkout native runner/client build is missing; the physical test cannot skip or build itself.");
        var clientHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(expectedClient, ct)));
        return new(runner, source, clientHash);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "NetRatel.sln"))) return directory.FullName;
        throw new InvalidOperationException("The required physical lane must run against its actual candidate checkout.");
    }

    private static async Task<string> GitAsync(string repository, string[] arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Native proof source inspection could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var result = await output; _ = await errors;
            if (process.ExitCode != 0 || result.Length > 65536)
                throw new InvalidOperationException("The native proof source inspection failed or exceeded its bounded result.");
            return result.Trim();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            // Observe cancellation/faults without exposing arbitrary Git diagnostics.
            try { _ = await output; _ = await errors; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }
}
