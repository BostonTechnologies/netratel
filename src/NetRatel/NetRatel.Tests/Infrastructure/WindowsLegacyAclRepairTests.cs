using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using NetRatel.Infrastructure.Auth;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class WindowsLegacyAclRepairTests
{
    [Fact]
    public Task SyntheticLegacyAclDecisionsRemainFailClosed() => RunProbe(native: false);

    [Fact]
    [Trait("category", "hosted")]
    public async Task NativeOfflineLegacyAclRepairPreservesIdentityAndHelperAccess()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Native Windows ACL evidence requires the existing Windows package lane."); return; }
        await RunProbe(native: true);
    }

    [Fact]
    [Trait("category", "hosted")]
    public async Task NativeFreshCanonicalRootCreationDoesNotInheritUsersWrites()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Native Windows ACL evidence requires the existing Windows package lane."); return; }
        await FreshRootProbe();
    }

    [SupportedOSPlatform("windows")]
    private static async Task FreshRootProbe()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "netratel-fresh-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var parent = new DirectoryInfo(fixture).GetAccessControl();
            parent.SetAccessRuleProtection(true, false);
            parent.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
            foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
                parent.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            parent.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            new DirectoryInfo(fixture).SetAccessControl(parent);
            var before = parent.GetSecurityDescriptorSddlForm(AccessControlSections.All);
            var root = Path.Combine(fixture, "NetRatel");
            // Test-only injection into the private creation core. Public production callers
            // select only Environment.SpecialFolder.CommonApplicationData/NetRatel.
            var create = typeof(WindowsAgentDataDirectory).GetMethod("EnsureCreated", BindingFlags.NonPublic | BindingFlags.Static)!;
            create.Invoke(null, [root]);
            var security = new DirectoryInfo(root).GetAccessControl();
            security.AreAccessRulesProtected.Should().BeTrue();
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(rule => rule.IdentityReference.Value == "S-1-5-32-545" && rule.AccessControlType == AccessControlType.Allow)
                .Should().OnlyContain(rule => ((int)rule.FileSystemRights & 0xD0156) == 0);
            var store = new AgentCredentialStore(Path.Combine(root, "agent.dat"));
            await store.SaveAsync("synthetic-agent", "synthetic-refresh");
            (await store.LoadAsync()).Should().Be(("synthetic-agent", "synthetic-refresh"));
            new DirectoryInfo(fixture).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All).Should().Be(before);
            var safe = security.GetSecurityDescriptorSddlForm(AccessControlSections.All);
            create.Invoke(null, [root]);
            new DirectoryInfo(root).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All).Should().Be(safe);
        }
        finally { Directory.Delete(fixture, recursive: true); }
    }

    private static async Task RunProbe(bool native)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "AGENTS.md"))) repository = repository.Parent;
        repository.Should().NotBeNull("the offline fixture is a repository test asset");
        var shell = native ? "powershell.exe" : Environment.GetEnvironmentVariable("NETRATEL_TEST_POWERSHELL") ?? (OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");
        var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var value in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                     Path.Combine(repository!.FullName, "tools", "ci", "test-windows-legacy-acl.ps1"), "-TemplatePath",
                     Path.Combine(repository.FullName, "src", "NetRatel", "NetRatel.Infrastructure", "Artifacts", "Templates", "install.ps1") })
            start.ArgumentList.Add(value);
        if (native) start.ArgumentList.Add("-Native");
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("An available PowerShell parser is required for offline ACL probes."); throw; }
        using (process)
        {
            process.Should().NotBeNull();
            var stdout = process!.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            process.ExitCode.Should().Be(0, await stdout + await stderr);
        }
    }
}
