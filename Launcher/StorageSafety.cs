using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HytaleAccounts;

internal static class StorageSafety
{
    private static readonly string User = WindowsIdentity.GetCurrent().User!.Value;
    private static bool Trusted(string sid, bool administratorsOnly) =>
        sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" ||
        (!administratorsOnly && sid == User);

    internal static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Error(path, ex); }
    }
    internal static bool FileExists(string path) => Attributes(path) is { } attributes && (attributes & FileAttributes.Directory) == 0;
    internal static bool DirectoryExists(string path) => Attributes(path) is { } attributes && (attributes & FileAttributes.Directory) != 0;

    internal static void Require(FileSystemSecurity security, string path, bool administratorsOnly = false, bool ancestor = false)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner is null || !Trusted(descriptor.Owner.Value, administratorsOnly))
            throw new IOException($"Unsafe owner of '{path}'; operation stopped and files preserved.");
        if (descriptor.DiscretionaryAcl is null)
            throw new IOException($"Unrestricted permissions on '{path}'; operation stopped and files preserved.");
        var mutation = (uint)(FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);
        // Windows profile ancestors may allow new siblings, but never replacement of an existing child.
        if (ancestor && security is DirectorySecurity) mutation &= ~6u;
        var denied = new Dictionary<string, uint>();
        foreach (var entry in descriptor.DiscretionaryAcl)
        {
            if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                throw new IOException($"Unsupported access rule on '{path}'; operation stopped and files preserved.");
            var sid = ace.SecurityIdentifier.Value;
            var rights = MapGeneric((uint)ace.AccessMask);
            if (ace.AceQualifier == AceQualifier.AccessDenied)
            { denied[sid] = denied.GetValueOrDefault(sid) | rights; continue; }
            // Package/capability access also needs the normal user/group side of an AppContainer token.
            if (Trusted(sid, administratorsOnly) || sid == "S-1-3-4" || sid.StartsWith("S-1-15-2-", StringComparison.Ordinal) ||
                sid.StartsWith("S-1-15-3-", StringComparison.Ordinal)) continue;
            // Evaluate each trustee separately: a deny for one group cannot protect an allow for another.
            var effective = rights & ~(denied.GetValueOrDefault(sid) | denied.GetValueOrDefault("S-1-1-0"));
            if ((effective & mutation) != 0)
                throw new IOException($"Unsafe mutation permissions on '{path}'; operation stopped and files preserved.");
        }
    }

    private static uint MapGeneric(uint mask)
    {
        if ((mask & 0x10000000) != 0) mask |= 0x1f01ff;
        if ((mask & 0x40000000) != 0) mask |= 0x120116;
        if ((mask & 0x80000000) != 0) mask |= 0x120089;
        if ((mask & 0x20000000) != 0) mask |= 0x1200a0;
        return mask & 0x0fffffff;
    }

    internal static IOException Error(string path, Exception exception)
    {
        if (exception is PathException previous) return previous;
        var code = exception is Win32Exception native ? native.NativeErrorCode : exception.HResult & 0xffff;
        return new PathException(code is 32 or 33
            ? $"Cannot lock '{path}' because another program has a conflicting handle. Close the program using this path, then retry. Files were preserved."
            : $"Cannot safely access '{path}' (Windows error {code}); operation stopped and files preserved.", exception);
    }
    private sealed class PathException(string message, Exception inner) : IOException(message, inner);
}
