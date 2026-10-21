using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

static class CpuControlConsumer
{
    public static void Run()
    {
        var owner = typeof(ReadWriteCore).Assembly;
        Require(new[] { typeof(IntelMsrReader), typeof(IntelMsrCatalogIdentity), typeof(IntelMsrTopologyProjection),
            typeof(IntelRaplEncoding), typeof(IntelOcMailbox), typeof(PawnIoIntelCpuHardware), typeof(IIntelCpuHardware),
            typeof(AdlxTuningHardware), typeof(IAdlxTuningHardware), typeof(AdlxTuningValue), typeof(NativeMetricSnapshotCounterPair) }
            .All(type => type.Assembly == owner), "original CPU and tuning operations belong to independent package");
        var opens = 0;
        var reader = new IntelMsrReader(() => { opens++; throw new IOException("Unselected driver"); });
        Require(reader.Read([], new Dictionary<string, IReadOnlySet<MetricFieldCode>>()).Count == 0 && opens == 0,
            "public empty demand has no driver initialization");
        _ = new AdlxTuningHardware();
        var missing = Path.Combine(Path.GetTempPath(), "missing-intel-module-" + Guid.NewGuid().ToString("N"));
        try { using var hardware = new PawnIoIntelCpuHardware(missing, missing + ".dll"); throw new InvalidOperationException("Missing explicit module must fail"); }
        catch (FileNotFoundException error) { Require(error.FileName == missing, "explicit module path survives without product directory resolution"); }

        var data = new CpuThreadCpuidData(new("GenuineIntel", 6, 0x9E, 0, null, [], []), 0x906E0,
            null, 9, null, null, null, []) { IntelDtsSupported = true, IntelPackageThermalSupported = true,
                IntelPerformanceFeedbackSupported = true };
        var captured = IntelMsrTopologyProjection.Create(new([new("core", 0, true, [0, 1])], []) {
            Packages = [new("package", [0, 1])], Dies = [], ThreadCpuid = [new(0, data), new(1, data)] });
        Require(captured.Identities.Count == 4 && captured.CoreStatus == NativeMetricSnapshotCatalogTopologyStatus.Complete,
            "recorded package/core/thread identities and domains");
        Require(IntelMsrTopologyProjection.Create(null).CoreStatus == NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
            "unavailable topology has no successful empty claim");
        const ulong original = 0x1234abcd923480a0;
        const ulong units = 3UL | (10UL << 16);
        var changed = IntelRaplEncoding.SetPower(original, units, false, 45);
        Require(IntelRaplEncoding.ReadPower(changed, units, false) == 45
            && (original & ~IntelRaplEncoding.FieldMask(false, false)) == (changed & ~IntelRaplEncoding.FieldMask(false, false)),
            "RAPL conversion preserves every unrelated raw bit");
        using var mailbox = new RecordedMailbox();
        Require(IntelOcMailbox.Read(mailbox, 2) == 0 && mailbox.SettingWrites == 0, "OC query is distinct from a setting write");
        var encoded = IntelOcMailbox.Encode(-20);
        IntelOcMailbox.Write(mailbox, 2, encoded);
        Require(IntelOcMailbox.Read(mailbox, 2) == encoded && mailbox.SettingWrites == 1 && IntelOcMailbox.Decode(encoded) == -19.53125,
            "recorded mailbox submission, signed precision and readback");
        Console.WriteLine("ReadWriteCore CPU/control consumer passed: public Intel topology/MSR entry, explicit module path, original RAPL and recorded OC query/write/readback, AMD tuning entry; no driver installation or hardware writes.");
    }

    private static void Require(bool value, string description)
    { if (!value) throw new InvalidOperationException(description); }
    private sealed class RecordedMailbox : IIntelCpuHardware
    {
        public int SettingWrites;
        private ulong response;
        private readonly uint[] planes = new uint[4];
        public ulong ReadMsr(uint register) => register == IntelOcMailbox.Register ? response : throw new IOException();
        public void WriteMsr(uint register, ulong value)
        {
            Require(register == IntelOcMailbox.Register, "recorded register");
            var plane = (int)((value >> 40) & 0xff);
            if (((value >> 32) & 0xff) == 0x11) { SettingWrites++; planes[plane] = (uint)value; }
            response = planes[plane];
        }
        public bool CanWriteMsr(uint register) => register == IntelOcMailbox.Register;
        public void Dispose() { }
    }
}
