using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed class WindowsProcessEffectValidationScopeStorageLease : IDisposable
{
    private readonly SafeFileHandle directoryHandle;

    internal WindowsProcessEffectValidationScopeStorageLease(
        string directoryPath,
        SafeFileHandle directoryHandle)
    {
        DirectoryPath = directoryPath;
        this.directoryHandle = directoryHandle;
    }

    internal string DirectoryPath { get; }

    public void Dispose() => directoryHandle.Dispose();
}

internal static class WindowsProcessEffectValidationScopeStorage
{
    private const uint GenericRead = 0x80000000;
    private const uint ReadControl = 0x00020000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int FileAttributeTagInfoClass = 9;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const int SeFileObject = 1;
    private static readonly SecurityIdentifier LocalSystemSid = new(
        WellKnownSidType.LocalSystemSid,
        null);
    private static readonly SecurityIdentifier AdministratorsSid = new(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);

    internal static WindowsProcessEffectValidationScopeStorageLease AcquireDirectory(
        string canonicalFilePath,
        bool create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalFilePath);
        var fullFilePath = Path.GetFullPath(canonicalFilePath);
        var directoryPath = Path.GetDirectoryName(fullFilePath)
            ?? throw new InvalidOperationException(
                "The validation scope storage path has no parent directory.");
        if (create)
        {
            var parent = Directory.GetParent(directoryPath)?.FullName
                ?? throw new InvalidOperationException(
                    "The validation scope storage directory has no parent.");
            Directory.CreateDirectory(parent);
            var attributes = GetAttributesExact(directoryPath);
            if (attributes is null)
            {
                new DirectoryInfo(directoryPath).Create(CreateDirectorySecurity());
            }
        }

        var handle = CreateFile(
            directoryPath,
            FileReadAttributes | ReadControl,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw CreateOpenException(error, directoryPath, "directory");
        }

        try
        {
            var information = QueryAttributeTag(handle, directoryPath);
            if ((information.FileAttributes & (uint)FileAttributes.Directory) == 0
                || (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0
                || information.ReparseTag != 0)
            {
                throw new InvalidDataException(
                    "The validation scope storage parent is not a regular directory.");
            }
            AssertFinalPath(handle, directoryPath);
            VerifyHandleSecurity(handle, directoryPath);
            return new(directoryPath, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static void ValidateExistingDirectoryIfPresent(string canonicalFilePath)
    {
        try
        {
            using var _ = AcquireDirectory(canonicalFilePath, create: false);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    internal static bool ProbeDirectoryExistsExact(string canonicalFilePath)
    {
        try
        {
            using var _ = AcquireDirectory(canonicalFilePath, create: false);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static FileStream OpenReadExact(
        WindowsProcessEffectValidationScopeStorageLease directory,
        string canonicalPath)
    {
        RequireContainedPath(directory, canonicalPath);
        var handle = CreateFile(
            canonicalPath,
            GenericRead | ReadControl,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw CreateOpenException(error, canonicalPath, "file");
        }

        try
        {
            var information = QueryAttributeTag(handle, canonicalPath);
            if ((information.FileAttributes & ((uint)FileAttributes.Directory |
                    (uint)FileAttributes.ReparsePoint)) != 0
                || information.ReparseTag != 0)
            {
                throw new InvalidDataException(
                    "The validation scope durable document is not a regular file.");
            }
            AssertFinalPath(handle, canonicalPath);
            VerifyHandleSecurity(handle, canonicalPath);
            var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            handle = null!;
            return stream;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static bool ProbeFileExistsExact(string canonicalPath)
    {
        var handle = CreateFile(
            canonicalPath,
            GenericRead | ReadControl,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            handle.Dispose();
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return error switch
        {
            ErrorFileNotFound or ErrorPathNotFound => false,
            _ => throw CreateOpenException(error, canonicalPath, "file")
        };
    }

    internal static void ApplySecureFileAcl(
        WindowsProcessEffectValidationScopeStorageLease directory,
        string path)
    {
        RequireContainedPath(directory, path);
        new FileInfo(path).SetAccessControl(CreateFileSecurity());
    }

    internal static void VerifyCommittedFile(
        WindowsProcessEffectValidationScopeStorageLease directory,
        string path)
    {
        using var _ = OpenReadExact(directory, path);
    }

    private static void RequireContainedPath(
        WindowsProcessEffectValidationScopeStorageLease directory,
        string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(
                Path.GetDirectoryName(fullPath),
                directory.DirectoryPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The validation scope durable document escaped its protected directory.");
        }
    }

    private static DirectorySecurity CreateDirectorySecurity()
    {
        var current = GetCurrentSid();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(current);
        foreach (var sid in RequiredSids(current))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        return security;
    }

    private static FileSecurity CreateFileSecurity()
    {
        var current = GetCurrentSid();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(current);
        foreach (var sid in RequiredSids(current))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }
        return security;
    }

    private static void VerifyHandleSecurity(SafeFileHandle handle, string path)
    {
        var current = GetCurrentSid();
        var required = RequiredSids(current)
            .Select(static sid => sid.Value)
            .ToHashSet(StringComparer.Ordinal);
        var status = GetSecurityInfo(
            handle,
            SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out var descriptorPointer);
        if (status != 0)
        {
            throw new IOException(
                $"The validation scope storage ACL could not be read: {path}",
                new Win32Exception(checked((int)status)));
        }

        try
        {
            if (descriptorPointer == IntPtr.Zero)
            {
                throw new UnauthorizedAccessException(
                    $"The validation scope storage ACL is missing: {path}");
            }
            var length = GetSecurityDescriptorLength(descriptorPointer);
            if (length == 0 || length > 64 * 1024)
            {
                throw new UnauthorizedAccessException(
                    $"The validation scope storage ACL length is invalid: {path}");
            }
            var descriptorBytes = new byte[checked((int)length)];
            Marshal.Copy(descriptorPointer, descriptorBytes, 0, descriptorBytes.Length);
            var descriptor = new RawSecurityDescriptor(descriptorBytes, 0);
            if (!descriptor.ControlFlags.HasFlag(
                    ControlFlags.DiscretionaryAclProtected)
                || descriptor.Owner != current
                || descriptor.DiscretionaryAcl is null)
            {
                throw new UnauthorizedAccessException(
                    $"The validation scope storage owner or ACL inheritance is unsafe: {path}");
            }

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (GenericAce ace in descriptor.DiscretionaryAcl)
            {
                if (ace is not CommonAce common
                    || common.AceQualifier != AceQualifier.AccessAllowed
                    || common.AceFlags.HasFlag(AceFlags.Inherited)
                    || !required.Contains(common.SecurityIdentifier.Value)
                    || (common.AccessMask & (int)FileSystemRights.FullControl) !=
                        (int)FileSystemRights.FullControl)
                {
                    throw new UnauthorizedAccessException(
                        $"The validation scope storage ACL contains an unsafe rule: {path}");
                }
                observed.Add(common.SecurityIdentifier.Value);
            }
            if (!observed.SetEquals(required))
            {
                throw new UnauthorizedAccessException(
                    $"The validation scope storage ACL is incomplete: {path}");
            }
        }
        finally
        {
            if (descriptorPointer != IntPtr.Zero)
            {
                _ = LocalFree(descriptorPointer);
            }
        }
    }

    private static SecurityIdentifier GetCurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new UnauthorizedAccessException(
                "The validation scope storage owner SID is unavailable.");
        return new SecurityIdentifier(user.Value);
    }

    private static IReadOnlyList<SecurityIdentifier> RequiredSids(
        SecurityIdentifier current)
        => new[] { current, LocalSystemSid, AdministratorsSid }
            .Distinct()
            .ToArray();

    private static FileAttributes? GetAttributesExact(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static FileAttributeTagInformation QueryAttributeTag(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out var information,
                checked((uint)Marshal.SizeOf<FileAttributeTagInformation>())))
        {
            throw new IOException(
                $"The validation scope storage attributes could not be read: {path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return information;
    }

    private static void AssertFinalPath(SafeFileHandle handle, string expectedPath)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new char[capacity];
            var length = GetFinalPathNameByHandle(handle, buffer, checked((uint)buffer.Length), 0);
            if (length == 0)
            {
                throw new IOException(
                    $"The validation scope storage final path could not be read: {expectedPath}",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
            if (length < buffer.Length)
            {
                var actual = NormalizeFinalPath(new string(buffer, 0, checked((int)length)));
                if (!string.Equals(
                        Path.GetFullPath(expectedPath),
                        actual,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The validation scope storage handle resolved to a different path.");
                }
                return;
            }
            capacity = checked((int)length + 1);
        }
    }

    private static string NormalizeFinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath($@"\\{path[uncPrefix.Length..]}");
        }
        if (path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[devicePrefix.Length..];
        }
        return Path.GetFullPath(path);
    }

    private static Exception CreateOpenException(int error, string path, string kind)
        => error switch
        {
            ErrorFileNotFound => new FileNotFoundException(
                $"The validation scope storage {kind} was not found.",
                path),
            ErrorPathNotFound => new DirectoryNotFoundException(
                $"The validation scope storage path was not found: {path}"),
            _ => new IOException(
                $"The validation scope storage {kind} could not be opened: {path}",
                new Win32Exception(error))
        };

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileAttributeTagInformation
    {
        internal readonly uint FileAttributes;
        internal readonly uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
