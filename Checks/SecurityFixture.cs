using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

internal static class SecurityFixture
{
    internal static string Create(string prefix)
    {
        // Temp may deliberately grant other accounts Modify; do not exempt it from production validation.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), prefix + Guid.NewGuid().ToString("N"));
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).Create(acl);
        return root;
    }
}
