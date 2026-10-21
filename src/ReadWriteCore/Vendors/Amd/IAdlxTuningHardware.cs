
namespace ResourceManager.ReadWrite.Vendors.Amd;

public enum AdlxGfxMode { Unknown = -1, Absolute = 0, Offset = 1 }
public sealed class AdlxUnknownGenerationException : NotSupportedException
{
    public AdlxUnknownGenerationException() : base("尚未识别此显卡代际，无法确认核心频率/电压的绝对值或偏移量语义。") { }
}
public sealed record AdlxTuningValue(string Pnp, int Current, int Minimum, int Maximum, int Step, AdlxGfxMode GfxMode = AdlxGfxMode.Absolute);
public interface IAdlxTuningHardware
{
    AdlxTuningValue Read(int? adapterIndex, int field);
    void Write(string pnp, int field, int value);
}
