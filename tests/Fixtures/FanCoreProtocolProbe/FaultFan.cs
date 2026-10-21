using FanControlCore;

// Deliberate exception from the injected release operation, never hardware.
sealed class FaultFan(bool failDrives) : IFanBackend
{
    private readonly object gate = new();
    private readonly bool[] automatic = [true, true];
    private readonly double[] duty = new double[2];
    private int firstFanDrives;
    private int firstFanRestores;
    public string Name => "owned-release-fault-fan";
    public bool Automatic { get { lock (gate) return automatic.All(value => value); } }
    public int FirstFanDrives { get { lock (gate) return firstFanDrives; } }
    public int FirstFanRestores { get { lock (gate) return firstFanRestores; } }
    public bool IsAutomatic(int fan) { lock (gate) return automatic[fan]; }
    public bool TryOpen(out string? reason) { reason = null; return true; }
    public IReadOnlyList<FanDescription> Describe()
        => [new(0, "Owned failed fan", true, true, FanRoles.Cpu, SupportsSoftwareCurve: true),
            new(1, "Owned other fan", true, true, FanRoles.Cpu, SupportsSoftwareCurve: true)];
    public IReadOnlyList<FanState> Read()
    {
        lock (gate) return Enumerable.Range(0, 2).Select(fan => new FanState(fan, "Owned fault fixture",
            null, automatic[fan] ? null : duty[fan], null, true, true, automatic[fan])).ToArray();
    }
    public bool TrySetDuty(int fan, double percent, out string? reason)
    {
        lock (gate)
        {
            automatic[fan] = false; duty[fan] = percent;
            if (fan == 0) firstFanDrives++;
            if (fan == 0 && failDrives) { reason = "Owned partial write failure."; return false; }
        }
        reason = null;
        return true;
    }
    public bool TrySetFullSpeed(int fan, out string? reason) => TrySetDuty(fan, 100, out reason);
    public bool TrySetAutomatic(int fan, out string? reason)
    {
        lock (gate)
        {
            if (fan == 0 && ++firstFanRestores == 1) throw new IOException("Owned release exception.");
            automatic[fan] = true;
        }
        reason = null;
        return true;
    }
    public void Dispose() { }
}
