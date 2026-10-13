using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using HytaleAccounts;

internal static class AclPropagationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "acl-propagation-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "migrating");
        var victim = Path.Combine(directory, "late-child.bin");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(victim, "synthetic victim");
            var before = new FileInfo(victim).GetAccessControl();
            var acl = new DirectoryInfo(directory).GetAccessControl();
            // Keep the fixture's existing access (including sandbox access), and add a harmless synthetic ACE.
            acl.SetAccessRuleProtection(true, true);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-21-314159265-271828182-161803398-4242"),
                FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            PrivateFiles.SetDirectoryPermissions(directory, acl);
            var actual = new DirectoryInfo(directory).GetAccessControl();
            byte[] Dacl(FileSystemSecurity value)
            {
                var dacl = new RawSecurityDescriptor(value.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl!;
                var bytes = new byte[dacl.BinaryLength]; dacl.GetBinaryForm(bytes, 0); return bytes;
            }
            check(actual.AreAccessRulesProtected && Dacl(actual).AsSpan().SequenceEqual(Dacl(acl)),
                "handle ACL setter applies the protected directory DACL");
            var after = new FileInfo(victim).GetAccessControl();
            // Windows may update auto-inheritance bookkeeping; compare every ACE and the protection flag.
            check(after.AreAccessRulesProtected == before.AreAccessRulesProtected && Dacl(after).AsSpan().SequenceEqual(Dacl(before)) &&
                File.ReadAllText(victim) == "synthetic victim", "directory ACL setter never propagates to an uninspected child: " +
                before.GetSecurityDescriptorSddlForm(AccessControlSections.Access) + " -> " + after.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            // Positive control: the previous path setter must expose the exact failure being guarded against.
            new DirectoryInfo(directory).SetAccessControl(acl);
            check(!Dacl(new FileInfo(victim).GetAccessControl()).AsSpan().SequenceEqual(Dacl(before)),
                "old path-based directory ACL setter changes the child (regression reproduced)");
        }
        finally
        {
            File.Delete(victim);
            Directory.Delete(directory); Directory.Delete(root);
        }
    }

}
