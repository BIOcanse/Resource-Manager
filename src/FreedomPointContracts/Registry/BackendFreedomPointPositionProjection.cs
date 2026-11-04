using System.Text;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

/// <summary>One declaration-to-position projection shared by bootstrap and verification tools.</summary>
public static class BackendFreedomPointPositionProjection
{
    public static string Generate(CompiledFreedomPointTree tree)
    {
        var text = new StringBuilder("// Generated from the host declaration positions. FreedomPointCheck verifies this file.\nnamespace ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;\n\ninternal static class BackendFreedomPointPositions\n{\n");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var points = tree.EnumeratePoints().ToArray();
        var index = 0;
        while (index < points.Length)
        {
            var point = points[index++];
            var name = string.Concat(point.Id.Split('_').Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
            if (!seen.Add(name)) throw new InvalidDataException("Host binding names collide; supply distinct generated member names.");
            text.Append("    public const ulong ").Append(name).Append(" = 0x").Append(point.Path).Append("UL;\n");
        }
        return text.Append("}\n").ToString();
    }
}
