using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

public class NoteWindowParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 14, 0, 0, KoreanNoteTime.SeoulOffset);

    [Fact]
    public void 받은_쪽지의_발신자_시각_본문_첨부를_읽는다()
    {
        var content = NoteWindowParser.Parse(
            NoteFixtures.Received("첫 줄\n\n둘째 줄", "안내문.hwp", "화면.jpg"), Now);

        Assert.Equal(NoteWindowKind.Received, content.Kind);
        var note = Assert.IsType<ReceivedNote>(content.Note);
        Assert.Equal("홍길동", note.SenderName);
        Assert.Equal("지방행정주사/주무관 │ 총무과", note.SenderDetail);
        Assert.Equal("10월 2일 오후 07:51", note.SentAtText);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 19, 51, 0, KoreanNoteTime.SeoulOffset), note.SentAt!.Value);
        Assert.Equal(3, note.RecipientCount);
        Assert.Equal("첫 줄\n\n둘째 줄", note.Body);
        Assert.Equal(["안내문.hwp", "화면.jpg"], note.AttachmentNames);
    }

    [Fact]
    public void 첨부가_없는_쪽지도_읽는다()
    {
        var note = NoteWindowParser.Parse(NoteFixtures.Received("본문"), Now).Note;

        Assert.NotNull(note);
        Assert.Empty(note.AttachmentNames);
    }

    [Theory]
    [InlineData("새 쪽지")]
    [InlineData("답장")]
    public void 작성_창은_받은_쪽지로_수집하지_않는다(string title)
    {
        var content = NoteWindowParser.Parse(NoteFixtures.Compose(title), Now);

        Assert.Equal(NoteWindowKind.Compose, content.Kind);
        Assert.Equal(title, content.Title);
        Assert.Null(content.Note);
    }

    [Fact]
    public void 구조가_달라지면_추측하지_않고_비워_둔다()
    {
        var broken = NoteFixtures.Document(NoteFixtures.NoteUrl,
            NoteFixtures.Div(NoteFixtures.Text("받은 쪽지")),
            NoteFixtures.Div(NoteFixtures.Text("내용")));

        var content = NoteWindowParser.Parse(broken, Now);

        Assert.Equal(NoteWindowKind.Received, content.Kind);
        Assert.Null(content.Note);
    }

    [Fact]
    public void 쪽지_페이지가_아닌_창을_구분한다()
    {
        var main = NoteFixtures.Document("file:///C:/x/app.asar/app/windows/renderer/app.html");

        Assert.False(NoteWindowParser.IsNotePage(main));
        Assert.True(NoteWindowParser.IsNotePage(NoteFixtures.Received("본문")));
    }
}

public class KoreanNoteTimeTests
{
    private static DateTimeOffset Seoul(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, KoreanNoteTime.SeoulOffset);

    [Theory]
    [InlineData("10월 2일 오전 12:05", 0, 5)]
    [InlineData("10월 2일 오전 09:30", 9, 30)]
    [InlineData("10월 2일 오후 12:00", 12, 0)]
    [InlineData("10월 2일 오후 07:51", 19, 51)]
    public void 오전_오후를_24시간으로_바꾼다(string text, int hour, int minute)
    {
        var time = KoreanNoteTime.Parse(text, Seoul(2026, 10, 3, 14, 0));

        Assert.Equal(Seoul(2026, 10, 2, hour, minute), time!.Value);
        Assert.True(time.YearInferred);
    }

    // 온톡은 오늘 받은 쪽지의 시각을 날짜 없이 표시한다.
    [Theory]
    [InlineData("오후 03:05", 15, 5)]
    [InlineData("오전 12:30", 0, 30)]
    public void 날짜가_없으면_오늘로_본다(string text, int hour, int minute)
    {
        var time = KoreanNoteTime.Parse(text, Seoul(2026, 10, 3, 15, 10));

        Assert.Equal(Seoul(2026, 10, 3, hour, minute), time!.Value);
        Assert.True(time.YearInferred);
        Assert.True(KoreanNoteTime.LooksLikeTime(text));
    }

    [Fact]
    public void 오늘_본_쪽지와_다음_날_본_같은_쪽지는_같은_시각이다()
    {
        var today = KoreanNoteTime.Parse("오후 03:05", Seoul(2026, 10, 3, 15, 10));
        var nextDay = KoreanNoteTime.Parse("10월 3일 오후 03:05", Seoul(2026, 10, 4, 9, 0));

        Assert.Equal(today!.Value, nextDay!.Value);
    }

    [Fact]
    public void 연도가_없고_날짜가_미래면_지난해로_본다()
    {
        var time = KoreanNoteTime.Parse("12월 30일 오후 03:00", Seoul(2026, 1, 2, 9, 0));

        Assert.Equal(Seoul(2025, 12, 30, 15, 0), time!.Value);
    }

    [Fact]
    public void 연도가_있으면_추정하지_않는다()
    {
        var time = KoreanNoteTime.Parse("2025년 3월 4일 오전 08:00", Seoul(2026, 10, 3, 14, 0));

        Assert.Equal(Seoul(2025, 3, 4, 8, 0), time!.Value);
        Assert.False(time.YearInferred);
    }

    [Theory]
    [InlineData("어제")]
    [InlineData("13월 1일 오전 01:00")]
    [InlineData("")]
    public void 해석할_수_없으면_null(string text)
    {
        Assert.Null(KoreanNoteTime.Parse(text, Seoul(2026, 10, 3, 14, 0)));
    }
}
