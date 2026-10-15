using AwesomeAssertions;
using System.Diagnostics;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class NetRatelMcpHttpDockerfileTests
{
    public static bool IsPosix => !OperatingSystem.IsWindows();

    [Fact]
    public void Http_image_runs_unprivileged_and_has_an_internal_liveness_probe()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var dockerfile = File.ReadAllText(Path.Combine(repositoryRoot, "docker", "mcp-http", "Dockerfile"));
        var entrypoint = File.ReadAllText(Path.Combine(repositoryRoot, "docker", "mcp-http", "docker-entrypoint.sh"));

        dockerfile.Should().Contain("ARG APP_UID=10001");
        dockerfile.Should().Contain("ASPNETCORE_URLS=http://+:9224");
        dockerfile.Should().Contain("http://127.0.0.1:9224/health/live");
        dockerfile.Should().Contain("USER netratel-mcp");
        dockerfile.Should().Contain("ENTRYPOINT [\"/app/docker-entrypoint.sh\"]");
        entrypoint.Should().Contain("case \"${1:-http}\" in");
        entrypoint.Should().Contain("exec dotnet /app/NetRatel.Mcp.Http.dll \"$@\"");
    }

    public static IEnumerable<object[]> DispatchCases =>
    [
        [new[] { "setup", "--mode", "host", "--non-interactive", "--instance", "https://instance.example/mcp" }, "/app/NetRatel.Mcp.Http.dll", "/setup-output"],
        [new[] { "setup", "--profile", "isolated", "--target", "cli" }, "/app/NetRatel.Mcp.Http.dll", "/setup-output"],
        [new[] { "setup", "--instance", "https://instance.example", "--target", "stdio" }, "/app/NetRatel.Mcp.Http.dll", "/setup-output"],
        [new[] { "setup", "--target", "http-client", "--config", "/durable/client profile.json" }, "/app/NetRatel.Mcp.Http.dll", "/durable"],
        [new[] { "setup", "--target", "http-host" }, "/app/NetRatel.Mcp.Http.dll", "/setup-output"],
        [new[] { "setup" }, "/app/NetRatel.Mcp.Http.dll", "/setup-output"],
        [new[] { "cli", "auth", "whoami" }, "/app/tooling/cli/netratel.dll", ""],
        [new[] { "stdio", "--config", "/durable/stdio profile.json" }, "/app/tooling/stdio/NetRatel.Mcp.dll", ""],
        [new[] { "http", "--urls", "http://+:9555" }, "/app/NetRatel.Mcp.Http.dll", ""],
        [new[] { "--urls", "http://+:9555" }, "/app/NetRatel.Mcp.Http.dll", ""]
    ];

    [Theory(Skip = "The shipped Docker entrypoint requires POSIX sh.", SkipUnless = nameof(IsPosix))]
    [MemberData(nameof(DispatchCases))]
    public async Task Real_shell_dispatch_preserves_arguments_and_initializes_only_setup_storage(string[] arguments, string assembly, string setupDirectory)
    {
        var directory = Path.Combine(Path.GetTempPath(), "netratel-mcp-dispatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var dotnet = Path.Combine(directory, "dotnet");
            await File.WriteAllTextAsync(dotnet, "#!/bin/sh\nprintf '%s\\0' \"${NETRATEL_SETUP_DIRECTORY-}\" \"$@\"\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(repositoryRoot, "docker", "mcp-http", "docker-entrypoint.sh"));
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["PATH"] = directory + Path.PathSeparator + start.Environment["PATH"];
            start.Environment["NETRATEL_SETUP_VOLUME"] = "disposable-setup-volume";
            start.Environment.Remove("NETRATEL_SETUP_DIRECTORY");
            if (setupDirectory == "/durable") start.Environment["NETRATEL_SETUP_DIRECTORY"] = setupDirectory;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
            process.ExitCode.Should().Be(0, await error);
            (await error).Should().BeEmpty();
            var forwarded = arguments[0] is "cli" or "stdio" or "http" ? arguments[1..] : arguments;
            (await output).Should().Be(string.Join('\0', new[] { setupDirectory, assembly }.Concat(forwarded)) + '\0');
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
