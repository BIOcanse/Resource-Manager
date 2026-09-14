namespace ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

internal sealed class EditableHotkeyDefinition : IEquatable<EditableHotkeyDefinition>
{
    public const int EncodingLength = 7;
    public const int MaxKeys = 4;

    private EditableHotkeyDefinition(int[] encoding, int[] keys, int[] relations)
    {
        Encoding = encoding;
        Keys = keys;
        Relations = relations;
    }

    public IReadOnlyList<int> Encoding { get; }

    public IReadOnlyList<int> Keys { get; }

    public IReadOnlyList<int> Relations { get; }

    public bool IsEmpty => Keys.Count == 0;

    public bool UsesKeyboardKeys => Keys.Any(static key => !IsMouseButton(key));

    public bool UsesMouseButtons => Keys.Any(IsMouseButton);

    public bool IsSafeForDestructiveGlobalAction =>
        Keys.Count >= 2
        && Keys.Any(IsApprovedModifier)
        && Keys.Any(static key => !IsModifier(key));

    public static EditableHotkeyDefinition Create(IEnumerable<int>? encoding)
    {
        var source = (encoding ?? []).Take(EncodingLength).ToArray();
        var keys = new List<int>(MaxKeys);
        var relations = new List<int>(MaxKeys - 1);
        for (var keyIndex = 0; keyIndex < MaxKeys; keyIndex++)
        {
            var slotIndex = keyIndex * 2;
            var virtualKey = slotIndex < source.Length ? source[slotIndex] : 0;
            if (virtualKey is <= 0 or > 255 || keys.Contains(virtualKey))
            {
                continue;
            }

            if (keys.Count > 0)
            {
                var relationSlot = slotIndex - 1;
                relations.Add(relationSlot >= 0
                    && relationSlot < source.Length
                    && source[relationSlot] == 1
                        ? 1
                        : 0);
            }

            keys.Add(virtualKey);
        }

        var normalized = new int[EncodingLength];
        for (var index = 0; index < keys.Count; index++)
        {
            normalized[index * 2] = keys[index];
            if (index > 0)
            {
                normalized[(index * 2) - 1] = relations[index - 1];
            }
        }

        return new EditableHotkeyDefinition(normalized, keys.ToArray(), relations.ToArray());
    }

    public bool Equals(EditableHotkeyDefinition? other)
    {
        return other is not null && Encoding.SequenceEqual(other.Encoding);
    }

    public override bool Equals(object? obj) => obj is EditableHotkeyDefinition other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Encoding)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    private static bool IsMouseButton(int virtualKey) => virtualKey is 1 or 2 or 4 or 5 or 6;

    private static bool IsApprovedModifier(int virtualKey) =>
        virtualKey is 17 or 18 or 91 or 92 or 162 or 163 or 164 or 165;

    private static bool IsModifier(int virtualKey) =>
        virtualKey is 16 or 17 or 18 or 91 or 92 or 160 or 161 or 162 or 163 or 164 or 165;
}
