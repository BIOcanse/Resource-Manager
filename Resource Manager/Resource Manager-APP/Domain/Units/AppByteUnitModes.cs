namespace ResourceManager.App.Domain.Units;

/// <summary>
/// 容量数字用哪个进制显示。
///
/// 后端不做单位换算，也不拼单位字符串：容量一律以原始字节发出，
/// 天生按位计的量（链路速率）以原始 bit 发出，字段名写明单位。
/// 选进制、跳档和加标签都在前端，由 <c>ClientApp/src/presentation/byteUnits.ts</c> 统一负责。
///
/// 这里只保留模式本身 —— 它是持久化的用户设置，后端负责存和校验，不负责应用。
/// </summary>
public static class AppByteUnitModes
{
    /// <summary>每个量按它本来的进制：内存类走 1024（GiB），存储类走 1000（GB）。</summary>
    public const string Native = "native";

    /// <summary>一律 1024，标签一律 KiB/MiB/GiB/TiB。</summary>
    public const string Binary = "binary";

    /// <summary>一律 1000，标签一律 kB/MB/GB/TB。</summary>
    public const string Decimal = "decimal";

    /// <summary>认不出的值一律落回 <see cref="Native"/>。</summary>
    public static string Normalize(string? mode)
        => mode switch
        {
            Binary => Binary,
            Decimal => Decimal,
            _ => Native
        };
}
