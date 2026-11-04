using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.Cpu;

// Temporary typed bridge; P-cpu-fields removes the managed projection, H3 removes the binding.
// No state, cache, thread, allocator or async borrow belongs to this one-call adapter.
internal static unsafe partial class NativeCpuOsFeatures
{
    internal const uint AbiVersion = 1;
    [Flags]
    internal enum Probe : uint { Features = 1, XState = 2 }
    internal enum ArchitectureKind : uint { X86 = 1, X64 = 2, Other = 3 }
    internal enum Status : uint
    {
        NotRequested, Present, MissingDll, MissingEntry, UnsupportedArchitecture, Failed
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Request
    {
        internal uint StructSize, Version;
        internal Probe Probes;
        internal ArchitectureKind Architecture;
        internal static Request Create(Probe probes, ArchitectureKind architecture) => new()
        { StructSize = (uint)sizeof(Request), Version = AbiVersion, Probes = probes, Architecture = architecture };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FeatureName { internal char* Text; internal nuint Length; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        internal uint StructSize;
        internal Status FeaturesStatus, XStateStatus;
        internal uint FeatureBits;
        internal ulong EnabledXStateMask;
        internal uint FeaturesError, XStateError;
        internal FeatureName* Names;
        internal uint NameCount, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        internal uint StructSize, Version;
        internal delegate* unmanaged[Cdecl]<nint, uint, uint*, uint*, uint> Feature;
        internal delegate* unmanaged[Cdecl]<nint, ulong*, uint*, uint> XState;
    }

    internal readonly record struct Observation(IReadOnlyList<string>? Features, ulong? EnabledXStateMask);
    [RuntimeRead(ReadVolatilityKind.Fixed)]
    internal static ArchitectureKind ProcessArchitecture => RuntimeInformation.ProcessArchitecture switch
    { Architecture.X86 => ArchitectureKind.X86, Architecture.X64 => ArchitectureKind.X64, _ => ArchitectureKind.Other };

    [RuntimeRead(ReadVolatilityKind.Fixed)]
    internal static Observation Read(Probe probes)
    {
        var request = Request.Create(probes, ProcessArchitecture);
        Result result = default;
        EnsureSuccess(Capture(in request, &result, (uint)sizeof(Result)), in result);
        return Project(in result);
    }

    internal static IReadOnlyList<string> ReadFeatures(Func<uint, bool> present)
    {
        var result = ReadInjected(Probe.Features, ProcessArchitecture, present, null, out var failure);
        failure?.Throw(); // The original injected feature overload does not catch missing APIs.
        return result.Features!;
    }

    internal static ulong? ReadEnabledStateMask(Func<ulong> read)
    {
        // Original injected overload has no architecture gate; only its production caller does.
        var result = ReadInjected(Probe.XState, ArchitectureKind.X64, null, read, out var failure);
        if (failure?.SourceException is not (DllNotFoundException or EntryPointNotFoundException)) failure?.Throw();
        return result.EnabledXStateMask;
    }

    internal static Observation ReadInjected(Probe probes, ArchitectureKind architecture,
        Func<uint, bool>? present, Func<ulong>? read, out ExceptionDispatchInfo? failure)
    {
        var source = new CallbackSource(present, read);
        var handle = GCHandle.Alloc(source);
        try
        {
            var request = Request.Create(probes, architecture);
            var callbacks = new Callbacks
            {
                StructSize = (uint)sizeof(Callbacks), Version = AbiVersion,
                Feature = &ReadFeatureCallback, XState = &ReadXStateCallback
            };
            Result result = default;
            var code = CaptureWithCallbacks(in request, GCHandle.ToIntPtr(handle), in callbacks, &result, (uint)sizeof(Result));
            failure = source.Failure;
            // Preserve original exception identity/stack; never throw through unmanaged frames.
            if (failure?.SourceException is not (DllNotFoundException or EntryPointNotFoundException)) failure?.Throw();
            EnsureSuccess(code, in result);
            return Project(in result);
        }
        finally { handle.Free(); }
    }

    private static Observation Project(in Result result)
    {
        IReadOnlyList<string>? features = null;
        if (result.FeaturesStatus == Status.Present)
        {
            if (result.Names == null || result.NameCount > sizeof(uint) * 8
                || (result.NameCount < sizeof(uint) * 8 && result.FeatureBits >> (int)result.NameCount != 0))
                throw new InvalidOperationException("Invalid CPU OS feature name catalog.");
            // These strings/array are the original public result, not an intermediate payload mirror.
            var values = new string[System.Numerics.BitOperations.PopCount(result.FeatureBits)];
            var index = 0;
            var destination = 0;
            while (index < result.NameCount)
            {
                if ((result.FeatureBits & (1u << index)) != 0)
                {
                    var name = result.Names[index];
                    values[destination++] = new string(name.Text, 0, checked((int)name.Length));
                }
                index++;
            }
            features = values;
        }
        return new(features, result.XStateStatus == Status.Present ? result.EnabledXStateMask : null);
    }

    private static void EnsureSuccess(int code, in Result result)
    {
        if (code != 0 || result.StructSize != sizeof(Result))
            throw new InvalidOperationException("CPU OS feature ABI request/layout mismatch.");
        ThrowFailure(result.FeaturesStatus, result.FeaturesError);
        ThrowFailure(result.XStateStatus, result.XStateError);
    }

    private static void ThrowFailure(Status status, uint error)
    {
        if (status == Status.Failed) throw new Win32Exception(unchecked((int)error));
        if ((uint)status > (uint)Status.Failed)
            throw new InvalidOperationException("Invalid CPU OS feature probe status.");
    }

    private sealed class CallbackSource(Func<uint, bool>? feature, Func<ulong>? xstate)
    {
        internal readonly Func<uint, bool>? Feature = feature;
        internal readonly Func<ulong>? XState = xstate;
        internal ExceptionDispatchInfo? Failure;
        internal uint Record(Exception exception)
        {
            // The first non-absence exception wins over an earlier independent API absence.
            if (Failure is null || exception is not (DllNotFoundException or EntryPointNotFoundException))
                Failure = ExceptionDispatchInfo.Capture(exception);
            return (uint)(exception switch
            { DllNotFoundException => Status.MissingDll, EntryPointNotFoundException => Status.MissingEntry, _ => Status.Failed });
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ReadFeatureCallback(nint context, uint flag, uint* value, uint* error)
    {
        var source = (CallbackSource)GCHandle.FromIntPtr(context).Target!;
        try { *value = source.Feature!(flag) ? 1u : 0u; return (uint)Status.Present; }
        catch (Exception exception) { *error = 0; return source.Record(exception); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ReadXStateCallback(nint context, ulong* value, uint* error)
    {
        var source = (CallbackSource)GCHandle.FromIntPtr(context).Target!;
        try { *value = source.XState!(); return (uint)Status.Present; }
        catch (Exception exception) { *error = 0; return source.Record(exception); }
    }

    static NativeCpuOsFeatures() => ReadWriteNativeLibraryResolver.EnsureRegistered();
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_cpu_os_features_capture")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Capture(in Request request, Result* output, uint outputSize);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_cpu_os_features_capture_with_callbacks")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CaptureWithCallbacks(in Request request, nint context, in Callbacks callbacks, Result* output, uint outputSize);
}
