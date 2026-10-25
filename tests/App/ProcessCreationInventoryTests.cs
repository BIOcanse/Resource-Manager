using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessCreationInventoryTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("short")]
    [InlineData("offset")]
    [InlineData("duplicate")]
    [InlineData("limit")]
    public void KernelInventoryCreationIdentitiesAreReadWithFiniteStructuralBounds(string shape)
    {
        var size = WindowsProcessSystemThreadParser.ProcessHeaderSize;
        var data = new byte[size * 2];
        for (var i = 0; i < 2; i++)
        {
            var row = data.AsSpan(i * size);
            BinaryPrimitives.WriteUInt32LittleEndian(row, i == 0 ? (uint)size : 0);
            BinaryPrimitives.WriteInt64LittleEndian(row[32..], 133_000_000_000_000_000 + i);
            var pidOffset = WindowsProcessSystemThreadParser.ProcessIdOffset;
            if (IntPtr.Size == 8) BinaryPrimitives.WriteInt64LittleEndian(row[pidOffset..], 100 + i);
            else BinaryPrimitives.WriteInt32LittleEndian(row[pidOffset..], 100 + i);
        }
        Assert.Equal(32, Marshal.OffsetOf<WindowsProcessSystemThreadParser.SystemProcessInformationHeader>("CreationTime").ToInt32());
        if (shape == "short") data = data[..(size - 1)];
        if (shape == "offset") BinaryPrimitives.WriteUInt32LittleEndian(data, uint.MaxValue);
        if (shape == "duplicate") data.AsSpan(WindowsProcessSystemThreadParser.ProcessIdOffset, IntPtr.Size)
            .CopyTo(data.AsSpan(size + WindowsProcessSystemThreadParser.ProcessIdOffset));
        var options = new WindowsProcessDetailsOptions(maximumProcesses: shape == "limit" ? 1 : 2);
        Assert.Equal(shape == "valid", WindowsProcessSystemThreadParser.TryParseCreations(data, options, out var creations));
        if (shape == "valid") Assert.Equal(133_000_000_000_000_001, creations[101]);
    }

    [Fact]
    public void RealKernelInventoryHasTheCurrentCreationWithoutOpeningEachProcess()
    {
        using var process = Process.GetCurrentProcess();
        using var buffer = new WindowsProcessInventoryBuffer();
        var creations = WindowsProcessInventoryReader.Instance.ReadCreations([process], buffer);
        Assert.Equal(ProcessReadStatus.Current, creations[process.Id].Status);
        Assert.Equal(WindowsProcessFieldOwner.ReadCreation(process).Creation, creations[process.Id].Creation);
    }
}
