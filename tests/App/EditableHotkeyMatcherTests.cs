using ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

namespace Resource_Manager_APP.Tests;

public sealed class EditableHotkeyMatcherTests
{
    [Fact]
    public void UnorderedChord_TriggersRegardlessOfPressOrderAndOnlyOnceUntilRelease()
    {
        var matcher = new EditableHotkeyMatcher(
            EditableHotkeyDefinition.Create([17, 0, 18, 0, 46, 0, 0]));

        Assert.False(matcher.HandleKeyDown(46));
        Assert.False(matcher.HandleKeyDown(17));
        Assert.True(matcher.HandleKeyDown(18));
        Assert.False(matcher.HandleKeyDown(18));

        matcher.HandleKeyUp(17);
        Assert.True(matcher.HandleKeyDown(17));
    }

    [Fact]
    public void OrderedRelation_RequiresPreviousKeyToBePressedFirst()
    {
        var matcher = new EditableHotkeyMatcher(
            EditableHotkeyDefinition.Create([17, 1, 18, 0, 0, 0, 0]));

        Assert.False(matcher.HandleKeyDown(18));
        Assert.False(matcher.HandleKeyDown(17));
        matcher.HandleKeyUp(18);
        matcher.HandleKeyUp(17);

        Assert.False(matcher.HandleKeyDown(17));
        Assert.True(matcher.HandleKeyDown(18));
    }

    [Fact]
    public void Definition_CompactsInvalidAndDuplicateKeys()
    {
        var definition = EditableHotkeyDefinition.Create([0, 1, 17, 0, 17, 1, 46]);

        Assert.Equal([17, 1, 46, 0, 0, 0, 0], definition.Encoding);
        Assert.Equal([17, 46], definition.Keys);
        Assert.Equal([1], definition.Relations);
    }

    [Fact]
    public void GenericModifier_MatchesLeftOrRightLowLevelVirtualKey()
    {
        var matcher = new EditableHotkeyMatcher(
            EditableHotkeyDefinition.Create([17, 0, 46, 0, 0, 0, 0]));

        Assert.False(matcher.HandleKeyDown(162));
        Assert.True(matcher.HandleKeyDown(46));
        matcher.HandleKeyUp(162);
        matcher.HandleKeyUp(46);
        Assert.False(matcher.HandleKeyDown(163));
        Assert.True(matcher.HandleKeyDown(46));
    }

    [Fact]
    public void MouseButtons_ShareTheSameOrderedMatcherState()
    {
        var matcher = new EditableHotkeyMatcher(
            EditableHotkeyDefinition.Create([17, 1, 5, 0, 0, 0, 0]));

        Assert.False(matcher.HandleKeyDown(17));
        Assert.True(matcher.HandleKeyDown(5));
        matcher.HandleKeyUp(5);
        matcher.HandleKeyUp(17);
    }

    [Theory]
    [InlineData(new[] { 65, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new[] { 1, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new[] { 16, 0, 65, 0, 0, 0, 0 }, false)]
    [InlineData(new[] { 17, 0, 18, 0, 0, 0, 0 }, false)]
    [InlineData(new[] { 17, 0, 65, 0, 0, 0, 0 }, true)]
    [InlineData(new[] { 91, 0, 1, 0, 0, 0, 0 }, true)]
    public void Definition_ReportsDestructiveGlobalHotkeySafety(int[] encoding, bool expected)
    {
        Assert.Equal(
            expected,
            EditableHotkeyDefinition.Create(encoding).IsSafeForDestructiveGlobalAction);
    }

    [Theory]
    [InlineData(0x0201, 0u, 1)]
    [InlineData(0x0204, 0u, 2)]
    [InlineData(0x0207, 0u, 4)]
    [InlineData(0x020B, 1u << 16, 5)]
    [InlineData(0x020B, 2u << 16, 6)]
    public void MouseHook_MapsStandardButtonsToVirtualKeys(int message, uint mouseData, int expected)
    {
        Assert.Equal(expected, EditableHotkeyHook.ResolveMouseVirtualKey(message, mouseData));
    }
}
