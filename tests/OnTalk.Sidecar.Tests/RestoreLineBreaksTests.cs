using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

public sealed class RestoreLineBreaksTests
{
    [Fact]
    public void 한덩어리로_돌아온_결과에_원문의_줄과_문단_구조를_되살린다()
    {
        var original = "안녕하세요.\n\n회의 자료를 보내 드립니다. 내용을 확인해 주세요.\n\n감사합니다.";
        var refined = "안녕하세요. 회의 자료를 송부드립니다. 내용을 검토해 주시기 바랍니다. 감사합니다.";
        Assert.Equal("안녕하세요.\n\n회의 자료를 송부드립니다. 내용을 검토해 주시기 바랍니다.\n\n감사합니다.",
            DraftText.RestoreLineBreaks(original, refined));
    }

    [Fact]
    public void 줄바꿈이_이미_있거나_원문이_한줄이면_결과를_바꾸지_않는다()
    {
        Assert.Equal("가.\n나.", DraftText.RestoreLineBreaks("원문 첫줄.\n둘째줄.", "가.\n나."));
        Assert.Equal("가. 나. 다.", DraftText.RestoreLineBreaks("원문 한 줄입니다.", "가. 나. 다."));
        Assert.Equal("가.", DraftText.RestoreLineBreaks("첫줄\n둘째줄", "가."));
    }

    [Fact]
    public void 문장이_원문의_줄보다_적으면_문장_수만큼만_줄을_나눈다()
    {
        Assert.Equal("가.\n나.", DraftText.RestoreLineBreaks("첫줄 내용\n둘째줄 내용\n셋째줄 내용", "가. 나."));
    }
}
