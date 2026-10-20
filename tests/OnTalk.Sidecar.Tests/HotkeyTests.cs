using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

public sealed class HotkeyTests
{
    [Fact]
    public void 기본_단축키는_Ctrl_Shift_Space이고_쓸_수_있다()
    {
        Assert.Equal("Ctrl+Shift+Space", Hotkey.Default.Format());
        Assert.Null(Hotkey.Default.Problem);
    }

    [Theory]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("Ctrl+Alt+K")]
    [InlineData("Win+F12")]
    [InlineData("Ctrl+Alt+Shift+Win+7")]
    [InlineData("Alt+Home")]
    public void 표시_문자열은_저장했다_읽어도_같다(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var key));
        Assert.Equal(text, key.Format());
        Assert.Null(key.Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+K")]
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Shift")]
    public void 읽을_수_없는_문자열은_거절한다(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void 수정키_표시는_누른_수정키만_정해진_순서로_적는다()
    {
        Assert.Equal("", Hotkey.FormatModifiers(0));
        Assert.Equal("Ctrl", Hotkey.FormatModifiers(Hotkey.Control));
        Assert.Equal("Ctrl+Alt+Shift+Win", Hotkey.FormatModifiers(Hotkey.Win | Hotkey.Shift | Hotkey.Alt | Hotkey.Control));
    }

    [Theory]
    [InlineData("Shift+K", "Ctrl, Alt, Win")]
    [InlineData("Ctrl+C", "기본 편집")]
    [InlineData("Ctrl+V", "기본 편집")]
    [InlineData("Alt+F4", "Windows")]
    public void 쓸_수_없는_조합은_이유를_알려_준다(string text, string reason)
    {
        Assert.True(Hotkey.TryParse(text, out var key));
        Assert.Contains(reason, key.Problem);
    }

    [Fact]
    public void 키가_수정키_없이_또는_지원하지_않는_키면_문제로_본다()
    {
        Assert.NotNull(new Hotkey(0, 0x41).Problem);
        Assert.NotNull(new Hotkey(Hotkey.Control, 0x1B).Problem); // Esc
        Assert.Null(new Hotkey(Hotkey.Control | Hotkey.Shift, 0x43).Problem); // Ctrl+Shift+C는 허용
    }
}
