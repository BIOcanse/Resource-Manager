namespace ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

internal sealed class KernelModuleAddressMap
{
    internal const string UnknownModuleName = "未知内核模块";

    // ISR and DPC routines run in kernel space, which starts here on x64 and ARM64. The kernel
    // ImageLoad keyword also reports every user-mode DLL load (hundreds per second on a busy
    // machine); those images can never own a routine, so the session does not keep them.
    private const ulong KernelAddressSpaceStart = 0xFFFF_8000_0000_0000UL;

    private readonly object gate = new();
    // Sorted by ImageBase and replaced whole on every change, so Resolve reads without the lock.
    // Changes are rare (driver loads and the rundown); Resolve runs for every ISR/DPC event.
    private Table table = Table.Empty;

    public static bool IsKernelAddress(ulong address) => address >= KernelAddressSpaceStart;

    public void Upsert(ulong imageBase, int imageSize, string? fileName)
    {
        if (imageBase == 0 || imageSize <= 0)
        {
            return;
        }

        var path = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim();
        var name = ModuleName(path);
        var range = new KernelModuleRange(imageBase, (ulong)imageSize, new(name, path));
        lock (gate)
        {
            var current = table.Ranges;
            var index = FirstAbove(current, imageBase) - 1;
            KernelModuleRange[] next;
            if (index >= 0 && current[index].ImageBase == imageBase)
            {
                next = (KernelModuleRange[])current.Clone();
                next[index] = range;
            }
            else
            {
                var insertAt = index + 1;
                next = new KernelModuleRange[current.Length + 1];
                Array.Copy(current, 0, next, 0, insertAt);
                next[insertAt] = range;
                Array.Copy(current, insertAt, next, insertAt + 1, current.Length - insertAt);
            }

            Volatile.Write(ref table, Table.Create(next));
        }
    }

    public void Remove(ulong imageBase)
    {
        lock (gate)
        {
            var current = table.Ranges;
            var index = FirstAbove(current, imageBase) - 1;
            if (index < 0 || current[index].ImageBase != imageBase)
            {
                return;
            }

            var next = new KernelModuleRange[current.Length - 1];
            Array.Copy(current, 0, next, 0, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            Volatile.Write(ref table, Table.Create(next));
        }
    }

    public KernelModuleIdentity Resolve(ulong routineAddress)
    {
        if (routineAddress == 0)
        {
            return KernelModuleIdentity.Unknown;
        }

        var current = Volatile.Read(ref table);
        var ranges = current.Ranges;
        // The containing image with the highest base wins. Walk down from the last base at or
        // below the address; once no earlier image reaches past it, none can contain it.
        var index = FirstAbove(ranges, routineAddress) - 1;
        while (index >= 0 && current.MaxLastThrough[index] >= routineAddress)
        {
            var module = ranges[index];
            if (routineAddress - module.ImageBase < module.ImageSize)
            {
                return module.Identity;
            }

            index--;
        }

        return KernelModuleIdentity.Unknown;
    }

    public void Clear()
    {
        lock (gate)
        {
            Volatile.Write(ref table, Table.Empty);
        }
    }

    // Index of the first range whose ImageBase is above the address.
    private static int FirstAbove(KernelModuleRange[] ranges, ulong address)
    {
        var low = 0;
        var high = ranges.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (ranges[middle].ImageBase <= address)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static string ModuleName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return UnknownModuleName;
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return path;
        }
    }

    private sealed record KernelModuleRange(
        ulong ImageBase,
        ulong ImageSize,
        KernelModuleIdentity Identity)
    {
        // Inclusive, so an image that reaches the top of the address space still covers its last
        // byte; an image that would wrap past it saturates there, as Resolve's containment test does.
        public ulong Last => ImageBase + (ImageSize - 1) < ImageBase ? ulong.MaxValue : ImageBase + (ImageSize - 1);
    }

    // MaxLastThrough[i] is the highest last address among ranges 0..i; it bounds the downward walk in Resolve.
    private sealed class Table(KernelModuleRange[] ranges, ulong[] maxLastThrough)
    {
        public static Table Empty { get; } = new([], []);

        public KernelModuleRange[] Ranges { get; } = ranges;

        public ulong[] MaxLastThrough { get; } = maxLastThrough;

        public static Table Create(KernelModuleRange[] ranges)
        {
            var maxLast = new ulong[ranges.Length];
            var highest = 0UL;
            for (var i = 0; i < ranges.Length; i++)
            {
                highest = Math.Max(highest, ranges[i].Last);
                maxLast[i] = highest;
            }

            return new Table(ranges, maxLast);
        }
    }
}

internal sealed record KernelModuleIdentity(string Name, string? Path)
{
    public static KernelModuleIdentity Unknown { get; } = new(KernelModuleAddressMap.UnknownModuleName, null);
}
