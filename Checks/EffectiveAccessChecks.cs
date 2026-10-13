using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

internal static class EffectiveAccessChecks
{
    // A synthetic SID, not a created/logged-on Windows account. Skip directory-service group lookup.
    private static readonly SecurityIdentifier Stranger = new("S-1-5-21-314159265-271828182-161803398-4242");

    internal static void Run(Action<bool, string> check)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        check(!Stranger.Equals(user), "synthetic unrelated SID differs from the running user");
        foreach (var broadSid in new[] { "WD", "AU", "BU", Stranger.Value })
        {
            var acl = new FileSecurity();
            acl.SetSecurityDescriptorSddlForm($"O:{user.Value}G:{user.Value}D:P(A;;FA;;;{user.Value})(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;{broadSid})");
            Check(acl.GetSecurityDescriptorBinaryForm(), "permissive descriptor " + broadSid, true, check);
            acl.SetSecurityDescriptorSddlForm($"O:{user.Value}G:{user.Value}D:P(A;;FA;;;{user.Value})(A;;FA;;;SY)(A;;FA;;;BA)");
            Check(acl.GetSecurityDescriptorBinaryForm(), "private descriptor", false, check);
        }
    }

    internal static void Check(string path, bool accessible, Action<bool, string> check)
    {
        FileSystemSecurity acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        Check(acl.GetSecurityDescriptorBinaryForm(), Path.GetFileName(path), accessible, check);
    }

    private static void Check(byte[] descriptor, string name, bool accessible, Action<bool, string> check)
    {
        var read = (uint)FileSystemRights.ReadAndExecute;
        check(Allowed(descriptor, Stranger, read) == accessible,
            "Windows Authz simulated unrelated standard user " + (accessible ? "can read " : "cannot read ") + name);
        var forbidden = new[] { FileSystemRights.WriteData, FileSystemRights.AppendData, FileSystemRights.WriteAttributes,
            FileSystemRights.WriteExtendedAttributes, FileSystemRights.Delete, FileSystemRights.DeleteSubdirectoriesAndFiles,
            FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership };
        if (!accessible) forbidden = forbidden.Concat([FileSystemRights.ReadData, FileSystemRights.ReadAttributes,
            FileSystemRights.ReadExtendedAttributes, FileSystemRights.ReadPermissions, FileSystemRights.ExecuteFile]).ToArray();
        check(forbidden.All(right => !Allowed(descriptor, Stranger, (uint)right)),
            "Windows Authz denies each unrelated-user forbidden right on " + name);
        check(Allowed(descriptor, WindowsIdentity.GetCurrent().User!, (uint)FileSystemRights.FullControl),
            "Windows Authz simulated owner retains full control of " + name);
    }

    private static bool Allowed(byte[] descriptor, SecurityIdentifier user, uint rights)
    {
        IntPtr manager = IntPtr.Zero, context = IntPtr.Zero, grouped = IntPtr.Zero;
        var allocations = new List<IntPtr>();
        IntPtr Sid(SecurityIdentifier sid)
        {
            var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
            var pointer = Marshal.AllocHGlobal(bytes.Length); allocations.Add(pointer); Marshal.Copy(bytes, 0, pointer, bytes.Length); return pointer;
        }
        void Require(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            Require(AuthzInitializeResourceManager(1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, "Synthetic ACL regression", out manager));
            Require(AuthzInitializeContextFromSid(2, Sid(user), manager, IntPtr.Zero, default, IntPtr.Zero, out context));
            var groups = new[] { WellKnownSidType.WorldSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.BuiltinUsersSid }
                .Select(type => new SidAndAttributes { Sid = Sid(new SecurityIdentifier(type, null)), Attributes = 4u }).ToArray();
            Require(AuthzAddSidsToContext(context, groups, (uint)groups.Length, IntPtr.Zero, 0, out grouped));
            var granted = Marshal.AllocHGlobal(4); allocations.Add(granted);
            var error = Marshal.AllocHGlobal(4); allocations.Add(error);
            var request = new AccessRequest { DesiredAccess = rights };
            var reply = new AccessReply { Count = 1, Granted = granted, Error = error };
            Require(AuthzAccessCheck(0, grouped, ref request, IntPtr.Zero, descriptor, IntPtr.Zero, 0, ref reply, IntPtr.Zero));
            var accessError = Marshal.ReadInt32(error);
            if (accessError != 0 && accessError != 5) throw new Win32Exception(accessError);
            return accessError == 0 && ((uint)Marshal.ReadInt32(granted) & rights) == rights;
        }
        finally
        {
            if (grouped != IntPtr.Zero) AuthzFreeContext(grouped);
            if (context != IntPtr.Zero) AuthzFreeContext(context);
            if (manager != IntPtr.Zero) AuthzFreeResourceManager(manager);
            foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint Low; internal int High; }
    [StructLayout(LayoutKind.Sequential)] private struct AccessRequest
    { internal uint DesiredAccess; internal IntPtr PrincipalSelf, ObjectTypes; internal uint ObjectTypeCount; internal IntPtr OptionalArguments; }
    [StructLayout(LayoutKind.Sequential)] private struct AccessReply
    { internal uint Count; internal IntPtr Granted, Sacl, Error; }
    [DllImport("authz.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AuthzInitializeResourceManager(uint flags, IntPtr access, IntPtr compute, IntPtr free, string name, out IntPtr manager);
    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzInitializeContextFromSid(uint flags, IntPtr sid, IntPtr manager, IntPtr expiration, Luid id, IntPtr args, out IntPtr context);
    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzAddSidsToContext(IntPtr context, [In] SidAndAttributes[] groups, uint count, IntPtr restricted, uint restrictedCount, out IntPtr result);
    [DllImport("authz.dll", SetLastError = true)]
    private static extern bool AuthzAccessCheck(uint flags, IntPtr context, ref AccessRequest request, IntPtr audit, byte[] descriptor, IntPtr optional, uint count, ref AccessReply reply, IntPtr results);
    [DllImport("authz.dll")] private static extern bool AuthzFreeContext(IntPtr context);
    [DllImport("authz.dll")] private static extern bool AuthzFreeResourceManager(IntPtr manager);
}
