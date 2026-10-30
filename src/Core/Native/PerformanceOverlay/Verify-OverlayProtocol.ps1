#Requires -Version 7
[CmdletBinding()]
param(
    [string]$ReportDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $repositoryRoot '.local\reports'
}
if (-not (Test-Path -LiteralPath $ReportDirectory -PathType Container)) {
    throw "The registered report workspace does not exist: $ReportDirectory"
}
$harnessPath = Join-Path $ReportDirectory 'OverlayPublisherProtocolProbe.cs'
@'
global using System;
global using System.IO;
global using System.Threading;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using ResourceManager.Shared.Overlay;

namespace ResourceManager.NativeUi.Overlay
{
    internal readonly record struct OverlayTargetId(int ProcessId, ulong ProcessStartKey);
    internal sealed record OverlayImage(int Width, int Height, byte[] Pixels)
    {
        public int Stride => checked(Width * 4);
    }

    public static unsafe class OverlayPublisherProtocolProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime { public uint Low, High; }
        [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(
            nint process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", EntryPoint = "OpenFileMappingW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint MapViewOfFile(
            nint mapping, uint access, uint high, uint low, nuint bytes);
        [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(nint view);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static string Run()
        {
            Check(PerformanceOverlaySharedMemory.Version == 2, "Protocol version is not 2.");
            Check(PerformanceOverlaySharedMemory.Handshake == 0 && PerformanceOverlaySharedMemory.Enabled == 1
                && PerformanceOverlaySharedMemory.Disabled == -1, "Protocol lifecycle states differ from the native mirror.");
            Check(PerformanceOverlaySharedMemory.HeaderBytes == 88 && Marshal.SizeOf<PerformanceOverlayHeader>() == 88,
                "Protocol header size is not 88.");
            Check((int)Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.Generation)) == 16
                && (int)Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.SwapChainWidth)) == 72
                && (int)Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.PublisherProcessId)) == 80,
                "Protocol field offsets differ from the native mirror.");
            Check(GetProcessTimes(GetCurrentProcess(), out var created, out _, out _, out _), "GetProcessTimes failed.");
            var start = ((ulong)created.High << 32) | created.Low;
            var id = new OverlayTargetId(Environment.ProcessId, start);
            var name = PerformanceOverlaySharedMemory.SectionName(id.ProcessId, checked((long)id.ProcessStartKey));
            using (var section = new InjectedOverlaySection(id, 0))
            {
                var mapping = OpenFileMapping(0x0006, false, name);
                if (mapping == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                var view = MapViewOfFile(mapping, 0x0006, 0, 0, 0);
                if (view == 0) { CloseHandle(mapping); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                try
                {
                    var header = (PerformanceOverlayHeader*)view;
                    Check(header->Magic == PerformanceOverlaySharedMemory.Magic && header->Version == 2
                        && header->HeaderBytes == 88 && header->PixelCapacity == PerformanceOverlaySharedMemory.MaximumPixelBytes
                        && header->PublisherProcessId == (uint)Environment.ProcessId && header->Enabled == 0
                        && header->Generation == 0, "Real publisher initialized an incompatible header.");
                    var pixels = new byte[] { 0x12, 0x34, 0x56, 0xff, 0x78, 0x9a, 0xbc, 0xff };
                    section.Publish(new OverlayImage(2, 1, pixels));
                    Check(header->Enabled == 1 && header->Generation == 2 && header->Width == 2
                        && header->Height == 1 && header->Stride == 8, "Real publisher Publish did not set the expected frame.");
                    var nativePixels = (byte*)view + PerformanceOverlaySharedMemory.HeaderBytes;
                    for (int i = 0; i < pixels.Length; ++i)
                        Check(nativePixels[i] == pixels[i], "Real publisher pixel payload differs.");
                    using (var adopted = new InjectedOverlaySection(id, 0))
                    {
                        Check(!adopted.Enabled && header->Enabled == 0 && header->Generation == 2
                            && header->PublisherProcessId == (uint)Environment.ProcessId,
                            "Existing-section adoption did not enter handshake and identify its publisher.");
                        adopted.Publish(new OverlayImage(2, 1, pixels));
                        Check(header->Enabled == 1 && header->Generation == 4,
                            "Adopted publisher did not resume publication after handshake.");
                        adopted.Disable();
                        Check(header->Enabled == -1, "Adopted publisher Disable did not set terminal disabled.");
                    }
                    header->Version = 1;
                    var refused = false;
                    try { using var incompatible = new InjectedOverlaySection(id, 0); }
                    catch (InvalidDataException) { refused = true; }
                    finally { header->Version = PerformanceOverlaySharedMemory.Version; }
                    Check(refused, "Real publisher accepted an incompatible v1 section.");
                    section.Publish(new OverlayImage(2, 1, pixels));
                    section.Disable();
                    Check(header->Enabled == -1, "Real publisher Disable did not set terminal disabled.");
                    section.Dispose();
                    Check(header->Enabled == -1, "Real publisher Dispose did not leave terminal disabled.");
                }
                finally { UnmapViewOfFile(view); CloseHandle(mapping); }
            }
            var retained = OpenFileMapping(0x0004, false, name);
            var error = Marshal.GetLastWin32Error();
            if (retained != 0) CloseHandle(retained);
            Check(retained == 0 && error == 2, "Real publisher's section survived Dispose and probe release.");
            using (var reopened = new InjectedOverlaySection(id, 0))
                Check(!reopened.Enabled && reopened.Generation == 0, "Reopened publisher inherited stale frame state.");
            return "C# actual shared protocol + InjectedOverlaySection PASS version=2 size=88 offsets=16/72/80 publisherPid=current initial-handshake/publish/pixels/adopt-handshake/resume/v1-refusal/disable=-1/dispose/reopen";
        }
    }
}
'@ | Set-Content -LiteralPath $harnessPath -Encoding utf8
Add-Type -Path @(
    (Join-Path $repositoryRoot 'src\Shared\Overlay\PerformanceOverlaySharedMemory.cs'),
    (Join-Path $repositoryRoot 'src\UI\Core\Overlay\InjectedOverlaySection.cs'),
    $harnessPath
) -CompilerOptions @('/unsafe', '/nullable:enable')
[ResourceManager.NativeUi.Overlay.OverlayPublisherProtocolProbe]::Run() | Write-Output
