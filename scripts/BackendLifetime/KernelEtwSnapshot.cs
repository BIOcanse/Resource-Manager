using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ResourceManager.Tools.BackendLifetime
{
    public sealed class KernelEtwSnapshot
    {
        public string Name { get; set; }
        public string SessionGuid { get; set; }
        public ulong SessionHandle { get; set; }
        public uint LogFileMode { get; set; }
        public bool IsSystemLogger { get { return (LogFileMode & 0x02000000) != 0; } }
        public uint EnableFlags { get; set; }
        public uint BufferSizeKb { get; set; }
        public uint NumberOfBuffers { get; set; }
        public uint EventsLost { get; set; }
        public uint RealTimeBuffersLost { get; set; }

        public static KernelEtwSnapshot Read(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 1023)
                throw new ArgumentException("An exact ETW session name is required.", "name");
            IntPtr buffer = Allocate();
            try
            {
                uint error = ControlTraceW(0, name, buffer, 0);
                if (error == 4201) return null;
                if (error != 0) throw new Win32Exception((int)error, "ETW session query failed.");
                return Decode(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static KernelEtwSnapshot[] ReadAllVisible()
        {
            // The bounded query fails rather than returning an incomplete capacity claim.
            IntPtr[] buffers = new IntPtr[64];
            try
            {
                for (int i = 0; i < buffers.Length; i++) buffers[i] = Allocate();
                uint count;
                uint error = QueryAllTracesW(buffers, (uint)buffers.Length, out count);
                if (error != 0) throw new Win32Exception((int)error, "ETW session enumeration failed.");
                if (count > buffers.Length) throw new InvalidOperationException("ETW enumeration exceeded its bound.");
                var results = new List<KernelEtwSnapshot>((int)count);
                for (int i = 0; i < count; i++) results.Add(Decode(buffers[i]));
                return results.ToArray();
            }
            finally
            {
                foreach (IntPtr buffer in buffers)
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        private static IntPtr Allocate()
        {
            int headerSize = Marshal.SizeOf(typeof(TraceProperties));
            int size = headerSize + 4096;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, buffer, size);
                var properties = new TraceProperties();
                properties.Wnode.BufferSize = (uint)size;
                properties.LoggerNameOffset = (uint)headerSize;
                properties.LogFileNameOffset = (uint)(headerSize + 2048);
                Marshal.StructureToPtr(properties, buffer, false);
                return buffer;
            }
            catch { Marshal.FreeHGlobal(buffer); throw; }
        }

        private static KernelEtwSnapshot Decode(IntPtr buffer)
        {
            var p = (TraceProperties)Marshal.PtrToStructure(buffer, typeof(TraceProperties));
            int size = Marshal.SizeOf(typeof(TraceProperties)) + 4096;
            if (p.LoggerNameOffset < Marshal.SizeOf(typeof(TraceProperties)) || p.LoggerNameOffset > size - 2)
                throw new InvalidOperationException("ETW returned an invalid name offset.");
            string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, (int)p.LoggerNameOffset),
                (size - (int)p.LoggerNameOffset) / 2);
            int end = name.IndexOf('\0');
            if (end < 0) throw new InvalidOperationException("ETW returned an unterminated session name.");
            return new KernelEtwSnapshot {
                Name = name.Substring(0, end), SessionGuid = p.Wnode.Guid.ToString("D"),
                SessionHandle = p.Wnode.HistoricalContext, LogFileMode = p.LogFileMode,
                EnableFlags = p.EnableFlags, BufferSizeKb = p.BufferSize,
                NumberOfBuffers = p.NumberOfBuffers, EventsLost = p.EventsLost,
                RealTimeBuffersLost = p.RealTimeBuffersLost
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WnodeHeader
        {
            public uint BufferSize, ProviderId;
            public ulong HistoricalContext;
            public long TimeStamp;
            public Guid Guid;
            public uint ClientContext, Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TraceProperties
        {
            public WnodeHeader Wnode;
            public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize;
            public uint LogFileMode, FlushTimer, EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten;
            public uint LogBuffersLost, RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset, LoggerNameOffset;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint ControlTraceW(ulong handle, string name, IntPtr properties, uint control);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint QueryAllTracesW([In, Out] IntPtr[] properties, uint count, out uint loggerCount);
    }
}
