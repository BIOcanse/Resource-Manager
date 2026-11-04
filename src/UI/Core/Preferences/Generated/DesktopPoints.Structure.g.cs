#nullable enable
using System.Collections.Immutable;
namespace @ResourceManager.@Desktop;
public static class @DesktopPoints
{
    public readonly record struct __fpContractSlot(
        string Kind, string? Unit, long? IntegerMinimum, long? IntegerMaximum, long? IntegerStep, string? IntegerMinimumPoint,
        ulong? UnsignedMinimum, ulong? UnsignedMaximum, ulong? UnsignedStep, string? UnsignedMaximumPoint,
        double? DecimalMinimum, double? DecimalMaximum, ImmutableArray<string> Options,
        bool ChoiceOptionsUnresolved, int RequiredSelectionCount, int? IntegerBitWidth, __fpEnumContract? Enumeration);
    public readonly record struct __fpEnumMember(string Name, string Literal);
    public readonly record struct __fpEnumContract(bool IsUnsigned, int BitWidth, ImmutableArray<__fpEnumMember> Members);
    public sealed class __fpContractDescriptor
    {
        public string? ComplexContract { get; }
        public ImmutableArray<__fpContractSlot> Slots { get; }
        public __fpContractDescriptor(string? complexContract, ImmutableArray<__fpContractSlot> slots)
            => (ComplexContract, Slots) = (complexContract, slots);
    }
    [global::FreedomPoint.FreedomPointBinding(0x0001000200000005UL)]
    public const int @backend_timeout_ms = @DesktopPointsValues.@__fpValue_backend_timeout_ms;
}
public sealed class @DesktopPointsRuntime
{
    private @DesktopPointsRuntime()
    {
    }
    public static @DesktopPointsRuntime Create() => new();
}
