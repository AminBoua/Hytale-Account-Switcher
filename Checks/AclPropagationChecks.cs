using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using HytaleAccounts;

internal static class AclPropagationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = SecurityFixture.Create("acl-propagation-");
        try
        {
            foreach (var automatic in new[] { false, true })
            foreach (var oldSetter in new[] { false, true })
                RunCase(root, automatic, oldSetter, check);
        }
        finally { Directory.Delete(root); }

    }

    private static void RunCase(string root, bool automatic, bool oldSetter, Action<bool, string> check)
    {
        var name = (automatic ? "automatic" : "legacy") + (oldSetter ? "-path-setter" : "-handle-setter");
        var directory = Path.Combine(root, name);
        var victim = Path.Combine(directory, "late-child.bin");
        Directory.CreateDirectory(directory);
        try
        {
            if (automatic)
            {
                var initial = new DirectoryInfo(directory).GetAccessControl();
                initial.SetAccessRuleProtection(true, true);
                new DirectoryInfo(directory).SetAccessControl(initial);
            }
            File.WriteAllText(victim, "synthetic victim");
            var before = RawDescriptor(victim);
            check(new RawSecurityDescriptor(before, 0).ControlFlags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited) == automatic,
                name + " uses the intended inheritance format");
            var acl = new DirectoryInfo(directory).GetAccessControl();
            // Keep the fixture's existing access (including sandbox access), and add a harmless synthetic ACE.
            acl.SetAccessRuleProtection(true, true);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-21-314159265-271828182-161803398-4242"),
                FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            if (oldSetter) new DirectoryInfo(directory).SetAccessControl(acl);
            else PrivateFiles.SetDirectoryPermissions(directory, acl);
            var actual = new DirectoryInfo(directory).GetAccessControl();
            byte[] Dacl(FileSystemSecurity value)
            {
                var dacl = new RawSecurityDescriptor(value.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl!;
                var bytes = new byte[dacl.BinaryLength]; dacl.GetBinaryForm(bytes, 0); return bytes;
            }
            if (!oldSetter) check(actual.AreAccessRulesProtected && Dacl(actual).AsSpan().SequenceEqual(Dacl(acl)),
                name + " applies the protected directory DACL");
            // GetAccessControl infers legacy inheritance from the current parent; compare stored bytes instead.
            var unchanged = RawDescriptor(victim).AsSpan().SequenceEqual(before);
            check(unchanged != oldSetter && File.ReadAllText(victim) == "synthetic victim",
                name + (oldSetter ? " changes the child (regression reproduced)" : " leaves the child descriptor and bytes unchanged"));
        }
        finally
        {
            File.Delete(victim);
            Directory.Delete(directory);
        }
    }

    private static byte[] RawDescriptor(string path)
    {
        if (GetFileSecurity(path, 7, null, 0, out var length) || Marshal.GetLastWin32Error() != 122)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var descriptor = new byte[length];
        if (!GetFileSecurity(path, 7, descriptor, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return descriptor;
    }

    [DllImport("advapi32.dll", EntryPoint = "GetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSecurity(string path, uint information, byte[]? descriptor, uint length, out uint needed);

}
