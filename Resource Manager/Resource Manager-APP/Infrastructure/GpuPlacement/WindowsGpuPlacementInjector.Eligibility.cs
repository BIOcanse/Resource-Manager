using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsGpuPlacementInjector
{
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const uint SignaturePolicyLoadRestrictionMask = 0x7;
    private const uint DynamicCodeProhibited = 0x1;

    private static RuntimeGpuProviderInjectionResult? CheckRuntimeProviderEligibility(
        SafeKernelHandle processHandle, GpuPlacementProcessInstance target)
    {
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.SessionId == 0)
                return Failed(target.ProcessId, "session-zero", "Session 0 服务进程不接受运行时 GPU Provider 注入。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return Failed(target.ProcessId, "process-unavailable", "目标进程已退出或无法读取。", ex.HResult);
        }
        if (!IsCompatibleNativeX64Process(processHandle, out var architectureReason))
            return Failed(target.ProcessId, "architecture-unsupported", architectureReason);
        if (TryResolveProviderCompatibilityBlock(processHandle, out var status, out var message, out var error))
            return Failed(target.ProcessId, status, message, error);
        return IsWindowsSystemPath(target.ExecutablePath)
            ? Failed(target.ProcessId, "windows-system-path", "Windows 系统目录下的进程不接受运行时 GPU Provider 注入。")
            : null;
    }

    private static bool TryResolveProviderCompatibilityBlock(
        SafeKernelHandle process,
        out string status,
        out string message,
        out int? win32Error)
    {
        status = string.Empty;
        message = string.Empty;
        win32Error = null;

        var protection = new NativeMethods.ProcessProtectionLevelInformation();
        if (NativeMethods.GetProcessInformation(
                process,
                NativeMethods.ProcessInformationClass.ProtectionLevel,
                out protection,
                checked((uint)Marshal.SizeOf<NativeMethods.ProcessProtectionLevelInformation>())))
        {
            var blockReason = ResolveProcessMitigationBlockReason(0, 0, protection.ProtectionLevel);
            if (blockReason is not null)
            {
                status = "protected-process";
                message = blockReason;
                return true;
            }
        }
        else if (TryResolveUnexpectedQueryFailure("进程保护级别", out status, out message, out win32Error))
        {
            return true;
        }

        var signature = new NativeMethods.ProcessMitigationBinarySignaturePolicy();
        if (NativeMethods.GetProcessMitigationPolicy(
                process,
                NativeMethods.ProcessMitigationPolicy.BinarySignature,
                out signature,
                checked((nuint)Marshal.SizeOf<NativeMethods.ProcessMitigationBinarySignaturePolicy>())))
        {
            var blockReason = ResolveProcessMitigationBlockReason(signature.Flags, 0, NativeMethods.ProtectionLevelNone);
            if (blockReason is not null)
            {
                status = "binary-signature-policy";
                message = blockReason;
                return true;
            }
        }
        else if (TryResolveUnexpectedQueryFailure("二进制签名策略", out status, out message, out win32Error))
        {
            return true;
        }

        var dynamicCode = new NativeMethods.ProcessMitigationDynamicCodePolicy();
        if (NativeMethods.GetProcessMitigationPolicy(
                process,
                NativeMethods.ProcessMitigationPolicy.DynamicCode,
                out dynamicCode,
                checked((nuint)Marshal.SizeOf<NativeMethods.ProcessMitigationDynamicCodePolicy>())))
        {
            var blockReason = ResolveProcessMitigationBlockReason(0, dynamicCode.Flags, NativeMethods.ProtectionLevelNone);
            if (blockReason is not null)
            {
                status = "dynamic-code-policy";
                message = blockReason;
                return true;
            }
        }
        else if (TryResolveUnexpectedQueryFailure("动态代码策略", out status, out message, out win32Error))
        {
            return true;
        }

        return false;
    }

    internal static string? ResolveProcessMitigationBlockReason(
        uint signaturePolicyFlags,
        uint dynamicCodePolicyFlags,
        uint protectionLevel)
    {
        if (protectionLevel != NativeMethods.ProtectionLevelNone)
        {
            return $"目标进程保护级别为 0x{protectionLevel:x8}，拒绝加载用户态 GPU Provider。";
        }
        if ((signaturePolicyFlags & SignaturePolicyLoadRestrictionMask) != 0)
        {
            return $"目标进程限制可加载模块签名，策略标志为 0x{signaturePolicyFlags:x8}。";
        }
        if ((dynamicCodePolicyFlags & DynamicCodeProhibited) != 0)
        {
            return $"目标进程禁止动态代码，策略标志为 0x{dynamicCodePolicyFlags:x8}，无法安全建立 D3D hook。";
        }
        return null;
    }

    private static bool TryResolveUnexpectedQueryFailure(
        string queryName,
        out string status,
        out string message,
        out int? win32Error)
    {
        var error = Marshal.GetLastWin32Error();
        if (error is ErrorNotSupported or ErrorInvalidParameter)
        {
            status = string.Empty;
            message = string.Empty;
            win32Error = null;
            return false;
        }

        status = "process-mitigation-query-failed";
        message = $"读取{queryName}失败：{new Win32Exception(error).Message}";
        win32Error = error;
        return true;
    }
}
