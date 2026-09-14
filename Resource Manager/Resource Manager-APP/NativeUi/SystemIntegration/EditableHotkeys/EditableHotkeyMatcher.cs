namespace ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

internal sealed class EditableHotkeyMatcher(EditableHotkeyDefinition definition)
{
    private readonly HashSet<int> configuredKeys = definition.Keys.ToHashSet();
    private readonly Dictionary<int, long> downOrder = new();
    private long nextOrder;
    private bool latched;

    public bool HandleKeyDown(int virtualKey)
    {
        var configuredKey = ResolveConfiguredKey(virtualKey);
        if (configuredKey == 0)
        {
            return false;
        }

        if (!downOrder.ContainsKey(configuredKey))
        {
            downOrder[configuredKey] = ++nextOrder;
        }

        if (latched || downOrder.Count != configuredKeys.Count || !OrderedRelationsMatch())
        {
            return false;
        }

        latched = true;
        return true;
    }

    public void HandleKeyUp(int virtualKey)
    {
        var configuredKey = ResolveConfiguredKey(virtualKey);
        if (configuredKey == 0)
        {
            return;
        }

        downOrder.Remove(configuredKey);
        latched = false;
        if (downOrder.Count == 0)
        {
            nextOrder = 0;
        }
    }

    private bool OrderedRelationsMatch()
    {
        for (var index = 1; index < definition.Keys.Count; index++)
        {
            if (definition.Relations[index - 1] == 1
                && downOrder[definition.Keys[index - 1]] >= downOrder[definition.Keys[index]])
            {
                return false;
            }
        }

        return true;
    }

    private int ResolveConfiguredKey(int virtualKey)
    {
        if (configuredKeys.Contains(virtualKey))
        {
            return virtualKey;
        }

        var genericModifier = virtualKey switch
        {
            160 or 161 => 16,
            162 or 163 => 17,
            164 or 165 => 18,
            _ => 0
        };
        return configuredKeys.Contains(genericModifier) ? genericModifier : 0;
    }
}
