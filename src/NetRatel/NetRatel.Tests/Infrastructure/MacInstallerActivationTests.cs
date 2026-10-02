using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using NetRatel.Application.Artifacts;
using NetRatel.Infrastructure.Artifacts;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class MacInstallerActivationTests
{
    [Fact]
    public async Task Rendered_pointer_replacement_replaces_a_directory_symlink_atomically()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("The filesystem probe requires bash and Python.");
        var root = NewFixture();
        try
        {
            var old = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
            var next = Directory.CreateDirectory(Path.Combine(root, "next")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "current"), old);
            var result = await BashAsync(Function(Render(), "replace_current"), root, "replace_current \"$NEXT\"", next);
            result.ExitCode.Should().Be(0, result.Error);
            new DirectoryInfo(Path.Combine(root, "current")).ResolveLinkTarget(true)!.FullName.Should().Be(next);
            Directory.EnumerateFileSystemEntries(old).Should().BeEmpty("replacement must not follow current into the old directory");
            Directory.EnumerateFileSystemEntries(root, ".current-*").Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("linux")]
    public async Task Rendered_rollback_restores_previous_plist_and_restarts_daemon_even_before_pointer_switch(bool switched)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The filesystem probe requires Linux with bash and Python.");
        var root = NewFixture();
        try
        {
            var old = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
            var next = Directory.CreateDirectory(Path.Combine(root, "next")).FullName;
            var temporary = Directory.CreateDirectory(Path.Combine(root, "tmp")).FullName;
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "current"), switched ? next : old);
            var plist = Path.Combine(root, "daemon.plist");
            var backup = Path.Combine(temporary, "previous-launchd.plist");
            await File.WriteAllTextAsync(backup, "previous plist and service environment");
            await File.WriteAllTextAsync(plist, "new plist");
            var fake = Path.Combine(bin, "launchctl");
            await File.WriteAllTextAsync(fake, "#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >> \"$ROOT_DIR/launchctl.log\"\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var script = Render();
            var body = Function(script, "cleanup") + "\n" + Function(script, "replace_current") + "\n" + Function(script, "rollback");
            var command = "TMP_DIR=\"$ROOT_DIR/tmp\"; STAGE_DIR=''; LABEL=co.za.netratel.client; ACTIVATED=true; HAD_PREVIOUS_PLIST=true; " +
                          "PLIST_PATH=\"$ROOT_DIR/daemon.plist\"; PLIST_BACKUP=\"$TMP_DIR/previous-launchd.plist\"; " +
                          "PREVIOUS_TARGET=\"$ROOT_DIR/old\"; PATH=\"$ROOT_DIR/bin:$PATH\"; false; rollback";
            var result = await BashAsync(body, root, command, next);
            result.ExitCode.Should().Be(1, "the original activation failure must remain a failure");
            new DirectoryInfo(Path.Combine(root, "current")).ResolveLinkTarget(true)!.FullName.Should().Be(old);
            (await File.ReadAllTextAsync(plist)).Should().Be("previous plist and service environment");
            (await File.ReadAllTextAsync(Path.Combine(root, "launchctl.log"))).Should().Contain("bootstrap system " + plist);
            Directory.EnumerateFileSystemEntries(old).Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Rendered_mutation_boundary_precedes_bootout_and_preserves_prior_plist()
    {
        var script = Render();
        script.IndexOf("cp -p \"${PLIST_PATH}\" \"${PLIST_BACKUP}\"", StringComparison.Ordinal)
            .Should().BeLessThan(script.IndexOf("ACTIVATED=true", StringComparison.Ordinal));
        var mutation = script.IndexOf("ACTIVATED=true", StringComparison.Ordinal);
        mutation.Should().BeLessThan(script.IndexOf("launchctl bootout", mutation, StringComparison.Ordinal));
        script.Should().Contain("${XML_ROOT_DIR}/current/NetRatel.Client");
        script.Should().NotContain("mv -f \"${ROOT_DIR}/current.next\"");
    }

    private static string Render() => new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
        7, "osx-x64", "synthetic-enrollment-input", "https://api.example.invalid", DateTimeOffset.UtcNow.AddHours(1),
        true, false, "1.2.3", new string('a', 64)));

    private static string Function(string script, string name)
    {
        var start = script.IndexOf(name + "() {", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = script.IndexOf("\n}\n", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return script[start..(end + 3)];
    }

    private static string NewFixture() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "netratel-mac-pointer-" + Guid.NewGuid().ToString("N"))).FullName;

    private static async Task<(int ExitCode, string Error)> BashAsync(string functions, string root, string command, string next)
    {
        var path = Path.Combine(root, "probe.sh");
        await File.WriteAllTextAsync(path, functions + "\n" + command + "\n");
        var start = new ProcessStartInfo("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(path);
        start.Environment["ROOT_DIR"] = root;
        start.Environment["NEXT"] = next;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(bounded.Token);
        await output;
        return (process.ExitCode, await error);
    }
}
