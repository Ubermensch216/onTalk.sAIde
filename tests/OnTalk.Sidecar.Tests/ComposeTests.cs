using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;
using static OnTalk.Sidecar.Tests.NoteFixtures;

namespace OnTalk.Sidecar.Tests;

public class ComposeStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 14, 0, 0, KoreanNoteTime.SeoulOffset);

    // 온톡 5.4.3 작성 창에서 관측한 배치.
    private static AccNode Window(string body, string counter, int smsState = 0, bool dialog = false, params string[] recipients) =>
        Document(NoteUrl,
            Div(Text("답장")),
            Div(Div(
                Div(Text("수신"), Div(Text(recipients.Length.ToString())), Div(Text("추가"))),
                Div(recipients.Select(r => Div(Div(), Div(), Div(Text(r)))).ToArray()),
                new AccNode(AccNode.EditableText, "쪽지 내용을 입력해주세요.", body, 0, []) { Bounds = new AccBounds(10, 20, 400, 200) },
                Div(Text(counter)),
                Div(Div(Text("첨부 파일")), new AccNode(AccNode.EditableText, "", "", 0, [Text("추가")])),
                Div(Text("예약 전송"), Div(Text("설정"))),
                Div(new AccNode(AccNode.CheckBox, "SMS 발송", "", smsState, []), Div(Text("전송"), Text("(Alt+S)"))))),
            dialog ? Div(Text("예약 전송"), Text("예약 전송 일시"), Text("확인")) : Div());

    [Fact]
    public void 수신자_본문_바이트_표시를_읽는다()
    {
        var compose = NoteWindowParser.Parse(Window("첫 줄\r\n둘째 줄", "23/4000 Bytes", 0, false, "홍길동", "김철수"), Now).Compose;

        Assert.NotNull(compose);
        Assert.Equal(["홍길동", "김철수"], compose.Recipients);
        Assert.Equal("첫 줄\n둘째 줄", compose.Body);
        Assert.Equal(23, compose.UsedBytes);
        Assert.Equal(4000, compose.LimitBytes);
        Assert.False(compose.SmsChecked);
        Assert.False(compose.DialogOpen);
        Assert.Equal((210, 120), compose.BodyBounds.Center);
    }

    [Fact]
    public void 수신자가_없는_빈_창도_읽는다()
    {
        var compose = NoteWindowParser.Parse(Window("", "0/4000 Bytes"), Now).Compose;

        Assert.NotNull(compose);
        Assert.Empty(compose.Recipients);
        Assert.Equal("", compose.Body);
    }

    [Fact]
    public void SMS_선택과_예약_대화상자를_알아본다()
    {
        var compose = NoteWindowParser.Parse(Window("", "0/4000 Bytes", AccNode.StateChecked, dialog: true), Now).Compose;

        Assert.True(compose!.SmsChecked);
        Assert.True(compose.DialogOpen);
    }

    [Fact]
    public void 바이트_표시를_해석하지_못하면_상태를_만들지_않는다()
    {
        var content = NoteWindowParser.Parse(Window("", "많음/4000 Bytes"), Now);

        Assert.Null(content.Compose);
    }
}

public class DraftTextTests
{
    private const string Quote = "==========================================\n보낸사람 : 홍길동\n받은 시간 : 2026-10-02 19:51:52\n\n원문 본문";

    [Fact]
    public void 온톡이_넣은_인용과_내가_쓴_글을_나눈다()
    {
        Assert.Equal(("", Quote), DraftText.SplitQuote("\n" + Quote));
        Assert.Equal(("네, 알겠습니다.\n감사합니다.", Quote), DraftText.SplitQuote("네, 알겠습니다.\n감사합니다.\n" + Quote));
        Assert.Equal(("인용 없는 글\n=== 짧은 줄", ""), DraftText.SplitQuote("인용 없는 글\n=== 짧은 줄"));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("답장입니다.\n")]
    [InlineData("줄1\r\n줄2\n\n")]
    public void 나눈_것을_합치면_원래_본문이다(string prefix)
    {
        var body = (prefix + Quote).Replace("\r\n", "\n");

        var (message, quote) = DraftText.SplitQuote(body);

        Assert.Equal(body, DraftText.JoinQuote(message, quote));
    }

    [Fact]
    public void 채우지_않은_자리_표시를_찾는다()
    {
        Assert.Equal(["{이름}", "{기한}"], DraftText.UnfilledPlaceholders("{이름}님, {기한}까지 제출 바랍니다. {이름}님께."));
        Assert.Empty(DraftText.UnfilledPlaceholders("중괄호 없음. 빈 {} 과 줄을 넘는 {가\n나} 는 자리 표시가 아니다."));
    }

    [Fact]
    public void 블럭_지정_구간이_있으면_해당_구간만_바꾸고_앞뒤_공백과_나머지_문장은_유지한다()
    {
        var original = "안녕하세요.\n회의 일정이 내일 오후로 바뀌었습니다.\n감사합니다.";
        var target = "회의 일정이 내일 오후로 바뀌었습니다.";
        var idx = original.IndexOf(target, StringComparison.Ordinal);

        // 앞뒤 줄바꿈이 함께 드래그되어도 실제 문장 구간만 교체된다.
        var result = DraftText.ApplyRefinement(original, idx - 1, target.Length + 2, "회의 일정이 내일 오후 2시로 변경되었습니다.");

        Assert.True(result.IsBlock);
        Assert.Equal("안녕하세요.\n회의 일정이 내일 오후 2시로 변경되었습니다.\n감사합니다.", result.Text);
        Assert.Equal(idx, result.SelectionStart);
        Assert.Equal("회의 일정이 내일 오후 2시로 변경되었습니다.".Length, result.SelectionLength);
    }

    [Fact]
    public void 블럭_지정이_없거나_공백만_선택되면_전체_본문을_바꾼다()
    {
        var original = "첫 문장입니다.\n둘째 문장입니다.";

        var noSelection = DraftText.ApplyRefinement(original, 3, 0, "다듬은 전체 본문입니다.");
        Assert.False(noSelection.IsBlock);
        Assert.Equal("다듬은 전체 본문입니다.", noSelection.Text);

        var whitespaceOnly = DraftText.ApplyRefinement("가나다   라마바", 3, 3, "전체 교체");
        Assert.False(whitespaceOnly.IsBlock);
        Assert.Equal("전체 교체", whitespaceOnly.Text);
    }
}

public sealed class DraftStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ontalk-saide-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = new(2026, 10, 3, 14, 0, 0, KoreanNoteTime.SeoulOffset);

    [Fact]
    public void 초안은_자리별로_저장되고_비우면_지워진다()
    {
        using var store = ProfileStore.Open(_root, "user1");

        store.SaveDraft(DraftText.ReplySlot(7), "답장 초안", At);
        store.SaveDraft(DraftText.NewSlot, "새 쪽지 초안", At);
        store.SaveDraft(DraftText.ReplySlot(7), "고친 답장", At);

        Assert.Equal("고친 답장", store.GetDraft(DraftText.ReplySlot(7)));
        Assert.Equal("새 쪽지 초안", store.GetDraft(DraftText.NewSlot));
        Assert.Null(store.GetDraft(DraftText.ReplySlot(8)));

        store.SaveDraft(DraftText.NewSlot, "", At);
        Assert.Null(store.GetDraft(DraftText.NewSlot));
    }

    [Fact]
    public void 템플릿은_이름으로_덮어쓰고_이름순으로_나온다()
    {
        using var store = ProfileStore.Open(_root, "user1");

        store.SaveTemplate("회신", "확인했습니다.");
        store.SaveTemplate("감사", "감사합니다.");
        store.SaveTemplate("회신", "{이름}님, 확인했습니다.");

        var templates = store.GetTemplates();
        Assert.Equal(["감사", "회신"], templates.Select(t => t.Name));
        Assert.Equal("{이름}님, 확인했습니다.", templates[1].Body);

        store.DeleteTemplate(templates[0].Id);
        Assert.Equal("회신", Assert.Single(store.GetTemplates()).Name);
    }

    [Fact]
    public void 발송_기록은_누르기_전에_남고_결과로_갱신된다()
    {
        using var store = ProfileStore.Open(_root, "user1");
        var when = new DateTime(2026, 10, 5, 9, 5, 0);

        var first = store.BeginDispatch(["김철수"], "즉시 본문", null, At);
        var second = store.BeginDispatch(["김철수", "이영희"], "예약 본문", when, At);
        store.FinishDispatch(second, DispatchStatus.WindowClosed, "");

        var records = store.GetDispatches();
        Assert.Equal([second, first], records.Select(r => r.Id));
        Assert.Equal(DispatchStatus.WindowClosed, records[0].Status);
        Assert.Equal(when, records[0].ReserveAt);
        Assert.Equal(["김철수", "이영희"], records[0].RecipientNames);
        // 결과를 기록하지 못한 채 끝난 발송은 "결과 모름"으로 남는다.
        Assert.Equal(DispatchStatus.Pending, records[1].Status);
        Assert.Null(records[1].ReserveAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
