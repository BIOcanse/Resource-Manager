using System.Buffers.Binary;
using System.Text;

namespace Resource_Manager_APP.Tests.KernelEtwFixtures;

internal sealed unsafe partial class NativeKernelEtwTestResource
{
    // Controlled x64 version-2 SDK payloads. Native Consumer/decoder remains the
    // only parser; these helpers do not fabricate notifications or revisions.
    internal static readonly Guid ProcessProvider = new("3d6fa8d0-fe05-11d0-9dda-00c04fd7ba7c");
    internal static readonly Guid ImageProvider = new("2cb15d1d-5fc1-11d2-abe1-00a0c911f518");
    internal static readonly Guid FileProvider = new("90cbdc39-4a3e-11d1-84f4-0000f80464e3");

    internal void RawProcess(long qpc, uint pid, uint parentPid, string image, ulong uniqueKey = 0, byte opcode = 1)
    {
        var name = Encoding.UTF8.GetBytes(image);
        var payload = new byte[28 + name.Length + 1 + 2]; // null SID + ANSI name + empty UTF16 command line
        BinaryPrimitives.WriteUInt64LittleEndian(payload, uniqueKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), pid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), parentPid);
        name.CopyTo(payload.AsSpan(28));
        Raw(ProcessProvider, opcode, 2, 8, qpc, 0, pid, 0, payload);
    }
    internal void RawImage(long qpc, uint pid, ulong address, ulong size, string path, byte opcode = 10)
    {
        var name = Encoding.Unicode.GetBytes(path + '\0');
        var payload = new byte[56 + name.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, address);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), size);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), pid);
        name.CopyTo(payload.AsSpan(56));
        Raw(ImageProvider, opcode, 2, 8, qpc, 0, pid, 0, payload);
    }
    internal void RawFileName(long qpc, ulong fileKey, string path, byte opcode = 32)
    {
        var name = Encoding.Unicode.GetBytes(path + '\0');
        var payload = new byte[8 + name.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, fileKey); name.CopyTo(payload.AsSpan(8));
        Raw(FileProvider, opcode, 2, 8, qpc, 0, 0, 0, payload);
    }
    internal void RawFileCreate(long qpc, uint pid, uint tid, ulong fileObject, string path,
        ulong irp = 0, uint createOptions = 0, byte createDisposition = 1)
    {
        var name = Encoding.Unicode.GetBytes(path + '\0');
        var payload = new byte[36 + name.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, irp);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), tid);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), fileObject);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24), createOptions | ((uint)createDisposition << 24));
        name.CopyTo(payload.AsSpan(36));
        Raw(FileProvider, 64, 2, 8, qpc, 0, pid, tid, payload);
    }
    internal void RawFileWrite(long qpc, uint pid, uint tid, ulong fileObject, ulong fileKey, uint bytes, ulong irp = 0)
    {
        var payload = new byte[48];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), irp);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), tid);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(24), fileObject);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(32), fileKey);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40), bytes);
        Raw(FileProvider, 68, 2, 8, qpc, 0, pid, tid, payload);
    }
    internal void RawFileInfo(long qpc, uint pid, uint tid, ulong fileObject, ulong fileKey, byte opcode,
        ulong irp = 0, ulong extraInfo = 0, uint informationClass = 0)
    {
        var payload = new byte[44];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, irp);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), tid);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), fileObject);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(24), fileKey);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(32), extraInfo);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(40), informationClass);
        Raw(FileProvider, opcode, 2, 8, qpc, 0, pid, tid, payload);
    }
}
