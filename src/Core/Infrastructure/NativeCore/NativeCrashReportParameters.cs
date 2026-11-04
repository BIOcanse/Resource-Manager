using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.CrashReports;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.NativeCore;

// S-crash's synchronous field-borrow bridge. Not registered in the product until
// maintenance wires the leaf exports and the remaining design inputs close.
// Its managed source references retire in R-crash; WER's bridge retires with
// R-settings/CrashDumpSettings. This type owns no settings, plan, or lease.
internal static partial class NativeCrashReportParameters
{
    internal readonly record struct Mode(
        string Effective, string Source, string Default, string Channel, bool DumpsOn);

    // The original Channel algorithm remains the caller's responsibility for
    // now. This required argument never freezes the plan or supplies a default.
    internal static unsafe Mode ResolveForChannel(AppCrashDumpSettings settings, CrashReportOptions options, bool betaChannel)
    {
        fixed (char* mode = settings.Mode)
        fixed (char* beta = options.BetaDefaultMode)
        fixed (char* stable = options.StableDefaultMode)
        {
            var input = new ModeInput
            {
                Mode = new(mode, settings.Mode?.Length ?? 0),
                BetaDefault = new(beta, options.BetaDefaultMode?.Length ?? 0),
                StableDefault = new(stable, options.StableDefaultMode?.Length ?? 0),
                BetaChannel = betaChannel ? 1U : 0U
            };
            Check(ResolveMode(in input, out var output));
            string Field(uint field) => field switch
            {
                0 => settings.Mode,
                1 => options.BetaDefaultMode,
                2 => options.StableDefaultMode,
                _ => throw new InvalidOperationException("Unknown native crash-report field selector.")
            };
            var result = new Mode(Field(output.EffectiveField),
                output.ChannelDefault != 0 ? CrashDumpModeSources.ChannelDefault : CrashDumpModeSources.User,
                Field(output.DefaultField), betaChannel ? ReleaseChannels.Beta : ReleaseChannels.Stable,
                output.DumpsOn != 0);
            GC.KeepAlive(settings);
            GC.KeepAlive(options);
            return result;
        }
    }

    // There is intentionally no overload hiding the unresolved product minimum.
    internal static int KeepCount(int value, int minimum, int maximum)
    {
        var status = ResolveKeepCount(value, minimum, maximum, out var result);
        if (status == 2) throw new ArgumentException("The minimum keep count exceeds the maximum keep count.");
        Check(status);
        return result;
    }

    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"Native crash-report parameters failed ({status}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly unsafe struct Text(char* pointer, int length)
    {
        internal readonly char* Pointer = pointer;
        internal readonly nuint Length = checked((nuint)length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModeInput
    {
        internal Text Mode;
        internal Text BetaDefault;
        internal Text StableDefault;
        internal uint BetaChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModeOutput
    {
        internal uint EffectiveField;
        internal uint DefaultField;
        internal uint ChannelDefault;
        internal uint DumpsOn;
    }

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_crash_report_parameters_resolve_mode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int ResolveMode(in ModeInput input, out ModeOutput output);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_crash_report_parameters_keep_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int ResolveKeepCount(int value, int minimum, int maximum, out int output);
}
