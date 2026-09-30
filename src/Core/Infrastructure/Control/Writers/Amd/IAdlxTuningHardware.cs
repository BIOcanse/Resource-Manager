using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control.Writers.Amd;

internal enum AdlxGfxMode { Unknown = -1, Absolute = 0, Offset = 1 }
internal sealed class AdlxUnknownGenerationException : NotSupportedException
{
    internal AdlxUnknownGenerationException() : base("尚未识别此显卡代际，无法确认核心频率/电压的绝对值或偏移量语义。") { }
}
internal sealed record AdlxTuningValue(string Pnp, int Current, int Minimum, int Maximum, int Step, AdlxGfxMode GfxMode = AdlxGfxMode.Absolute);
internal interface IAdlxTuningHardware
{
    AdlxTuningValue Read(ControlObject target, int field);
    void Write(string pnp, int field, int value);
}
