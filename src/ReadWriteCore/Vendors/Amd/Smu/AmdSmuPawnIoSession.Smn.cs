using System;
using System.Collections.Generic;
using System.IO;

namespace ResourceManager.ReadWrite.Vendors.Amd.Smu;

/// <summary>Explicit AMDFamily17 module selection; uses the existing installed
/// PawnIO driver. The module is supplied by the established component payload.</summary>
public sealed record AmdSmnPawnIoOpenOptions(string ModulePath, IReadOnlyList<string> LibraryPaths,
    bool UseDirectDevice, TimeSpan PciLockTimeout);

public sealed partial class AmdSmuPawnIoSession
{
    public static IAmdSmnSession OpenSmn(AmdSmnPawnIoOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return OpenModule<IAmdSmnSession>(options.ModulePath, options.LibraryPaths,
            options.UseDirectDevice, options.PciLockTimeout,
            (executor, _) => new SmnSession(executor, options.PciLockTimeout));
    }

    private sealed class SmnSession(IAmdSmuPawnIoExecutor executor, TimeSpan timeout) : IAmdSmnSession
    {
        private readonly object gate = new();
        private bool disposed;
        public uint ReadRegister(uint address)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var output = WithPciLock(timeout, () => executor.Execute("ioctl_read_smn", [address], 1));
                if (output.Length != 1 || output[0] > uint.MaxValue)
                    throw new InvalidDataException("PawnIO SMN read did not return one 32-bit register.");
                return (uint)output[0];
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                executor.Dispose();
            }
        }
    }
}
