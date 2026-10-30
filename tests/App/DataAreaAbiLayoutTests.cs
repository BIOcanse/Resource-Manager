using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Native.DataArea;

namespace Resource_Manager_APP.Tests;

// The C# thin entry must match the region owner's extern structs in
// src/Core/NativeCore/src/data_area (exports.zig checks the same sizes).
public sealed class DataAreaAbiLayoutTests
{
    [Fact]
    public void ThinEntryStructuresMatchTheNativeLayout()
    {
        Assert.Equal(88, Unsafe.SizeOf<DataAreaSourceIdentity>());
        Assert.Equal(20, (int)Marshal.OffsetOf<DataAreaSourceIdentity>(nameof(DataAreaSourceIdentity.OwnerName)));
        Assert.Equal(24, Unsafe.SizeOf<DataAreaPositionDeclaration>());
        Assert.Equal(32, Unsafe.SizeOf<DataAreaDeclaration>());
        Assert.Equal(16, (int)Marshal.OffsetOf<DataAreaDeclaration>(nameof(DataAreaDeclaration.Sources)));
        Assert.Equal(24, Unsafe.SizeOf<DataAreaApplyFailure>());
        Assert.Equal(8, Unsafe.SizeOf<DataAreaSettings>());
        Assert.Equal(32, Unsafe.SizeOf<DataAreaBinding>());
        Assert.Equal(24, (int)Marshal.OffsetOf<DataAreaBinding>(nameof(DataAreaBinding.LayoutGeneration)));
        Assert.Equal(16, Unsafe.SizeOf<DataAreaReadView>());
        Assert.Equal(40, Unsafe.SizeOf<DataAreaBorrow>());
        Assert.Equal(32, (int)Marshal.OffsetOf<DataAreaBorrow>(nameof(DataAreaBorrow.Region)));
    }
}
