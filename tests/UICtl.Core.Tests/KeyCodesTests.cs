using UICtl.Core;

namespace UICtl.Core.Tests;

public class KeyCodesTests
{
    [Theory]
    [InlineData('a', false)]
    [InlineData('A', true)]
    [InlineData('1', false)]
    [InlineData('!', true)]
    [InlineData(',', false)]
    [InlineData('<', true)]
    public void CharMap_ReportsCorrectShiftState(char c, bool expectedShift)
    {
        Assert.True(KeyCodes.CharMap.TryGetValue(c, out var entry));
        Assert.Equal(expectedShift, entry.Shift);
    }

    [Fact]
    public void CharMap_LowerAndUpperShareTheSameKeyCode()
    {
        Assert.Equal(KeyCodes.CharMap['a'].KeyCode, KeyCodes.CharMap['A'].KeyCode);
    }

    [Fact]
    public void CharMap_DigitAndItsShiftedSymbolShareTheSameKeyCode()
    {
        Assert.Equal(KeyCodes.CharMap['1'].KeyCode, KeyCodes.CharMap['!'].KeyCode);
    }

    [Fact]
    public void ParseCombo_SplitsModifiersFromMainKey()
    {
        var (modifiers, mainKeyCode) = KeyCodes.ParseCombo("ctrl+shift+esc");
        Assert.Equal(2, modifiers.Count);
        Assert.Contains(KeyCodes.Modifiers["ctrl"], modifiers);
        Assert.Contains(KeyCodes.Modifiers["shift"], modifiers);
        Assert.Equal(KeyCodes.ResolveKeyToken("esc"), mainKeyCode);
    }

    [Fact]
    public void ParseCombo_IsCaseInsensitiveOnModifiers()
    {
        var (modifiers, _) = KeyCodes.ParseCombo("CTRL+a");
        Assert.Single(modifiers);
        Assert.Equal(KeyCodes.Modifiers["ctrl"], modifiers[0]);
    }

    [Fact]
    public void ParseCombo_SingleKeyNoModifiers()
    {
        var (modifiers, mainKeyCode) = KeyCodes.ParseCombo("a");
        Assert.Empty(modifiers);
        Assert.Equal(KeyCodes.ResolveKeyToken("a"), mainKeyCode);
    }

    [Fact]
    public void ParseCombo_ThrowsOnMoreThanOneNonModifierKey()
    {
        Assert.Throws<UiCtlException>(() => KeyCodes.ParseCombo("a+b"));
    }

    [Fact]
    public void ParseCombo_ThrowsOnModifiersOnly()
    {
        Assert.Throws<UiCtlException>(() => KeyCodes.ParseCombo("ctrl+shift"));
    }

    [Fact]
    public void ResolveKeyToken_ThrowsNamingUnrecognizedToken()
    {
        var ex = Assert.Throws<UiCtlException>(() => KeyCodes.ResolveKeyToken("nonsense"));
        Assert.Contains("nonsense", ex.Message);
    }
}
