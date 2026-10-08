using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NetRatel.Client.Service.Auth;

/// <summary>Creates the canonical Windows product root before runtime log/credential children.</summary>
public static class WindowsAgentDataDirectory
{
    [SupportedOSPlatform("windows")]
    public static void EnsureForPath(string path)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRatel");
        var full = Path.GetFullPath(path);
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            EnsureCreated(root);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureCreated(string root)
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal) { "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" };
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            trusted.Add(identity.User!.Value);
        }

        for (var component = root; component is not null; component = Path.GetDirectoryName(component))
        {
            if (!Directory.Exists(component) && !File.Exists(component)) continue;
            var item = new DirectoryInfo(component);
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || !item.Exists)
                throw new IOException($"NetRatel canonical data path '{component}' is a reparse point or unexpected object.");
            var acl = item.GetAccessControl();
            var owner = ((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))!).Value;
            if (!trusted.Contains(owner))
                throw new UnauthorizedAccessException($"NetRatel canonical data path '{component}' has untrusted ownerSid={owner}; existing permissions were left unchanged.");
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var unsafeRights = component.Equals(root, StringComparison.OrdinalIgnoreCase) ? 0xD0156 : 0xD0040;
                if (rule.AccessControlType == AccessControlType.Allow &&
                    (component.Equals(root, StringComparison.OrdinalIgnoreCase) || (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0) &&
                    !trusted.Contains(rule.IdentityReference.Value) && ((int)rule.FileSystemRights & unsafeRights) != 0)
                    throw new UnauthorizedAccessException($"NetRatel canonical data path '{component}' permits untrusted writes: ownerSid={owner}, aceSid={rule.IdentityReference.Value}, type={rule.AccessControlType}, rights={rule.FileSystemRights}, inherited={rule.IsInherited}, inheritance={rule.InheritanceFlags}, propagation={rule.PropagationFlags}; preview the installer legacy ACL repair before proceeding.");
            }
        }

        if (Directory.Exists(root)) return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.Traverse,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).Create(security);
        // Create does not reset a directory that appeared concurrently. Validate it too.
        EnsureCreated(root);
    }
}
