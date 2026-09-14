[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [ValidateSet('asInvoker', 'highestAvailable', 'requireAdministrator')]
    [string]$ExpectedExecutionLevel = 'asInvoker'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('ResourceManagerExecutableManifestReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class ResourceManagerExecutableManifestReader
{
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(
        string fileName,
        IntPtr file,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResourceW(
        IntPtr module,
        IntPtr name,
        IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr resourceData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    public static string ReadManifest(string executablePath)
    {
        IntPtr module = LoadLibraryExW(
            executablePath,
            IntPtr.Zero,
            LoadLibraryAsDataFile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not load the executable as a resource image.");
        }

        try
        {
            IntPtr resource = FindResourceW(module, new IntPtr(1), new IntPtr(24));
            if (resource == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The executable does not contain RT_MANIFEST resource 1.");
            }
            uint length = SizeofResource(module, resource);
            if (length == 0 || length > Int32.MaxValue)
            {
                throw new InvalidDataException("The executable manifest length is invalid.");
            }
            IntPtr loaded = LoadResource(module, resource);
            if (loaded == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not load the executable manifest resource.");
            }
            IntPtr locked = LockResource(loaded);
            if (locked == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not lock the executable manifest resource.");
            }

            byte[] bytes = new byte[(int)length];
            Marshal.Copy(locked, bytes, 0, bytes.Length);
            MemoryStream stream = new MemoryStream(bytes, false);
            try
            {
                StreamReader reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    true,
                    1024,
                    false);
                try
                {
                    return reader.ReadToEnd();
                }
                finally
                {
                    reader.Dispose();
                }
            }
            finally
            {
                stream.Dispose();
            }
        }
        finally
        {
            FreeLibrary(module);
        }
    }
}
'@
}

function Get-FileSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = [System.IO.FileStream]::new(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read,
        65536,
        [System.IO.FileOptions]::SequentialScan)
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $hasher.Dispose()
        $stream.Dispose()
    }
}

$executable = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not [System.IO.File]::Exists($executable)) {
    throw "Executable does not exist: $executable"
}
$manifestText = [ResourceManagerExecutableManifestReader]::ReadManifest($executable)
$document = [System.Xml.XmlDocument]::new()
$document.PreserveWhitespace = $true
$document.LoadXml($manifestText)
$executionNodes = @($document.SelectNodes("//*[local-name()='requestedExecutionLevel']"))
if ($executionNodes.Count -ne 1) {
    throw "Executable must contain exactly one requestedExecutionLevel element: $executable"
}
$executionNode = $executionNodes[0]
$actualLevel = $executionNode.GetAttribute('level')
$actualUiAccess = $executionNode.GetAttribute('uiAccess')
if ($actualLevel -cne $ExpectedExecutionLevel) {
    throw "Executable requestedExecutionLevel mismatch: expected=$ExpectedExecutionLevel actual=$actualLevel"
}
if ($actualUiAccess -cne 'false') {
    throw "Executable uiAccess must be false: actual=$actualUiAccess"
}
$autoElevateNodes = @($document.SelectNodes("//*[local-name()='autoElevate']"))
if ($autoElevateNodes.Count -ne 0) {
    throw 'Executable manifest must not contain an autoElevate declaration.'
}

[pscustomobject]@{
    Contract = 'resource-manager-executable-manifest-v1'
    ExecutablePath = $executable
    ExecutableSha256 = Get-FileSha256 -Path $executable
    RequestedExecutionLevel = $actualLevel
    UiAccess = $actualUiAccess
    AutoElevateDeclared = $false
    Valid = $true
} | ConvertTo-Json -Depth 2
