using System.Security.AccessControl;
using System.Security.Principal;

namespace GptAccountManager.Infrastructure;

public static class WindowsAcl
{
    public static void RestrictDirectoryToCurrentUser(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user is null)
            {
                return;
            }

            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        catch
        {
            // DPAPI remains the primary credential protection. Some managed
            // enterprise policies prevent applications from replacing ACLs.
        }
    }
}
