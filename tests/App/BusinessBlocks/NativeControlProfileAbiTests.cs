using System.Reflection;
using System.Runtime.InteropServices;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class NativeControlProfileAbiTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public unsafe void ProfileExportReturnsExactNonRootUidsAndReportsBufferCapacity(uint profile)
    {
        Assert.Equal(5, NativeControl.ProfileHostBlocksNative(profile, null, 0, out var needed));
        var expected = BackendExpectedProductionTree.Parents(profile).Keys.Order().ToArray();
        Assert.Equal((nuint)expected.Length, needed);
        var buffer = Enumerable.Repeat(uint.MaxValue, expected.Length + 1).ToArray();
        fixed (uint* output = buffer)
        {
            Assert.Equal(5, NativeControl.ProfileHostBlocksNative(profile, output, needed - 1, out var shortNeeded));
            Assert.Equal(needed, shortNeeded);
            Assert.All(buffer, uid => Assert.Equal(uint.MaxValue, uid));
            Assert.Equal(1, NativeControl.ProfileHostBlocksNative(profile, null, needed, out var nullNeeded));
            Assert.Equal(needed, nullNeeded);
            Assert.Equal(0, NativeControl.ProfileHostBlocksNative(profile, output, (nuint)buffer.Length, out var written));
            Assert.Equal(needed, written);
        }
        Assert.Equal(expected, buffer.Take(expected.Length).Select(uid => checked((int)uid)).Order());
        Assert.Equal(uint.MaxValue, buffer[^1]);
        Assert.DoesNotContain((uint)BlockUids.Program, buffer.Take(expected.Length));
        Assert.Equal(expected, NativeControl.GetProfileHostBlockUids(profile).Order());
    }

    [Fact]
    public unsafe void ProfileExportRejectsUnknownProfilesUsingTheExistingArgumentExceptionMapping()
    {
        Assert.Equal(1, NativeControl.ProfileHostBlocksNative(99, null, 0, out var needed));
        Assert.Equal((nuint)0, needed);
        Assert.Throws<ArgumentException>(() => NativeControl.GetProfileHostBlockUids(99));
        Assert.Throws<ArgumentException>(() => NativeControl.Check(5));
    }

    [Fact]
    public unsafe void ProfileExportUsesTheDeclaredCAbiScalarWidthsAndCallingConvention()
    {
        var method = typeof(NativeControl).GetMethod("ProfileHostBlocksNative", BindingFlags.NonPublic | BindingFlags.Static)!;
        var import = method.GetCustomAttribute<DllImportAttribute>()!;
        Assert.Equal("rm_control_profile_host_blocks", import.EntryPoint);
        Assert.Equal(CallingConvention.Cdecl, import.CallingConvention);
        Assert.Equal(typeof(int), method.ReturnType);
        Assert.Equal([typeof(uint), typeof(uint*), typeof(nuint), typeof(nuint).MakeByRefType()],
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(4, sizeof(uint));
        Assert.Equal(8, sizeof(nuint));
    }
}
