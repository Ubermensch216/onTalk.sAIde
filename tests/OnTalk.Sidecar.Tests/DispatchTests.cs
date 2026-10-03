using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using static OnTalk.Sidecar.Tests.NoteFixtures;

namespace OnTalk.Sidecar.Tests;

public class ReservationTimeTests
{
    [Theory]
    [InlineData("2026. 10. 3 / 오후 3:30", 2026, 10, 3, 15, 30)]
    [InlineData("2026. 11. 2 / 오전 12:00", 2026, 11, 2, 0, 0)]
    [InlineData("2026. 10. 31 / 오후 12:59", 2026, 10, 31, 12, 59)]
    public void 작성_창의_예약_표시를_해석한다(string text, int y, int mo, int d, int h, int mi)
    {
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), ReservationTime.ParseCompose(text));
    }

    [Theory]
    [InlineData("설정")]
    [InlineData("2026. 13. 3 / 오후 3:30")]
    [InlineData("2026. 10. 3 / 오후 13:30")]
    public void 예약_표시가_아니면_null(string text)
    {
        Assert.Null(ReservationTime.ParseCompose(text));
    }

    [Fact]
    public void 대화상자의_일시_표시를_해석한다()
    {
        Assert.Equal(new DateTime(2026, 10, 3, 15, 30, 0), ReservationTime.ParseDialog("2026년 10월 03일 오후 03:30"));
    }

    [Theory]
    [InlineData(0, 0, "오전", "12", "0")]
    [InlineData(9, 5, "오전", "9", "5")]
    [InlineData(12, 59, "오후", "12", "59")]
    [InlineData(15, 30, "오후", "3", "30")]
    public void 선택_상자에_넣을_값을_만든다(int hour, int minute, string amPm, string h, string m)
    {
        Assert.Equal((amPm, h, m), ReservationTime.ComboValues(new DateTime(2026, 10, 5, hour, minute, 0)));
    }
}

public class OnTalkPagesTests
{
    private const string SearchUrl = "file:///C:/x/app.asar/app/windows/userSearch/app.html?userSearch#/userSearch";

    private static AccNode Row(string name, string title, params string[] details) => Div(Div(
        Div(new AccNode(AccNode.Graphic, "", "", 0, [])),
        Div(Text(name), Text(" "), Text($" {title}"), Text(" ")),
        Div(Div(Text("오프라인"))),
        Div(details.Select(d => Div(Text(d))).ToArray()),
        Div()));

    private static AccNode SearchWindow(string? footer, params AccNode[] rows) => Document(SearchUrl,
        Div(Text("받는 사람 추가")),
        Div(new AccNode(AccNode.EditableText, "이름, 전화번호, 부서, 메일, 담당업무 검색", "", 0, [])),
        Div(Div(Div(Text("키워드 검색")), Div(rows))),
        footer is null ? Div() : Div(Div(), Div(Text(footer)), Div(Text("확인"))));

    [Fact]
    public void 검색_결과에서_사람마다_이름_직급_세부를_읽는다()
    {
        var tree = SearchWindow(null,
            Row("김철수", "주무관", "A시", "A시 / 총무과", "051-000-0001", "a@example.kr"),
            Row("김철수", "소방장", "A시", "A시 / 소방서", "051-000-0002", "b@example.kr"));

        var rows = OnTalkPages.PersonRows(tree);

        Assert.Equal(2, rows.Count);
        Assert.Equal(("김철수", "주무관"), (rows[0].Name, rows[0].Title));
        Assert.Equal(["A시", "A시 / 총무과", "051-000-0001", "a@example.kr"], rows[0].Details);
        Assert.False(rows[0].SamePerson(rows[1]));
        Assert.True(OnTalkPages.IsUserSearchPage(tree));
        Assert.NotNull(OnTalkPages.SearchBox(tree));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("1명 선택", 1)]
    [InlineData("12명 선택", 12)]
    public void 선택한_인원을_읽는다(string? footer, int expected)
    {
        Assert.Equal(expected, OnTalkPages.SelectedCount(SearchWindow(footer)));
    }

    private static AccNode Cell(int day) => new(AccNode.Cell, day.ToString("00"), "", 0, [Div(Text(day.ToString("00")))]);

    // 2026년 10월 달력: 9월 27~30일, 10월 1~31일, 11월 1~7일.
    private static AccNode ReservationWindow() => Document(NoteUrl,
        Div(Text("새 쪽지"), Div(Text("첨부 파일")), Div(Text("0/4000 Bytes"))),
        Div(Div(
            Div(Text("예약 전송")),
            Div(
                Div(Text("예약 전송 일시")),
                Div(Text("2026년 10월 03일 "), Text("오후 "), Text("03:02")),
                Div(new AccNode("DIV", "", "", 0, []) { Path = "prev" }, Text("2026년 10월"), new AccNode("DIV", "", "", 0, []) { Path = "next" }),
                Div([.. new[] { 27, 28, 29, 30 }.Concat(Enumerable.Range(1, 31)).Concat(Enumerable.Range(1, 7)).Select(Cell)]),
                Div(Text("시간 선택")),
                Div(new AccNode(AccNode.ComboBox, "오후", "", 0, []), new AccNode(AccNode.ComboBox, "3", "", 0, []), Text("시"),
                    new AccNode(AccNode.ComboBox, "2", "", 0, []), Text("분"))),
            Div(Text("확인")))));

    [Fact]
    public void 예약_대화상자의_일시와_달과_선택_상자를_읽는다()
    {
        var dialog = OnTalkPages.ReservationDialog(ReservationWindow());

        Assert.NotNull(dialog);
        Assert.Equal(new DateTime(2026, 10, 3, 15, 2, 0), OnTalkPages.ReservationDialogValue(dialog));
        var month = OnTalkPages.CalendarMonth(dialog);
        Assert.Equal((2026, 10, "prev", "next"), (month!.Value.Year, month.Value.Month, month.Value.Previous!.Path, month.Value.Next!.Path));
        Assert.Equal(["오후", "3", "2"], OnTalkPages.TimeCombos(dialog).Select(c => c.Name));
        Assert.DoesNotContain("첨부 파일", dialog.StaticTexts());
    }

    [Fact]
    public void 앞뒤_달의_같은_숫자가_아니라_이번_달_칸을_고른다()
    {
        var dialog = OnTalkPages.ReservationDialog(ReservationWindow())!;
        var cells = dialog.DescendantsAndSelf().Where(n => n.Role == AccNode.Cell).ToList();

        // 27~30은 9월 것이 앞에, 1~7은 11월 것이 뒤에 또 나온다.
        Assert.Same(cells[4], OnTalkPages.DayCell(dialog, 2026, 10, 1));
        Assert.Same(cells[4 + 26], OnTalkPages.DayCell(dialog, 2026, 10, 27));
        Assert.Same(cells[4 + 30], OnTalkPages.DayCell(dialog, 2026, 10, 31));
        Assert.Null(OnTalkPages.DayCell(dialog, 2026, 10, 32));
    }

    [Fact]
    public void 대화상자가_없으면_찾지_않는다()
    {
        Assert.Null(OnTalkPages.ReservationDialog(Compose("새 쪽지")));
    }
}

public class SendGuardTests
{
    private static ComposeState State(string[] recipients, string body, DateTime? reservation = null, bool sms = false, bool dialog = false) =>
        new(recipients, body, NoteBytes.Count(body), 4000, sms, dialog, default, reservation);

    private static readonly ExpectedCompose Expected = new(["김철수", "이영희"], "시험 본문", null, false);

    [Fact]
    public void 모두_같으면_차이가_없다()
    {
        Assert.Null(ComposeActions.Difference(State(["이영희", "김철수"], "시험 본문"), Expected));
    }

    [Fact]
    public void 수신자가_하나라도_다르면_막는다()
    {
        Assert.NotNull(ComposeActions.Difference(State(["김철수"], "시험 본문"), Expected));
        Assert.NotNull(ComposeActions.Difference(State(["김철수", "이영희", "박민수"], "시험 본문"), Expected));
        Assert.NotNull(ComposeActions.Difference(State(["김철수", "박민수"], "시험 본문"), Expected));
        Assert.NotNull(ComposeActions.Difference(State([], ""), new ExpectedCompose([], "", null, false)));
    }

    [Fact]
    public void 본문_예약_SMS_대화상자가_다르면_막는다()
    {
        string[] names = ["김철수", "이영희"];
        var when = new DateTime(2026, 10, 5, 9, 0, 0);

        Assert.NotNull(ComposeActions.Difference(State(names, "시험 본문."), Expected));
        Assert.NotNull(ComposeActions.Difference(State(names, "시험 본문", when), Expected));
        Assert.NotNull(ComposeActions.Difference(State(names, "시험 본문"), Expected with { Reservation = when }));
        Assert.NotNull(ComposeActions.Difference(State(names, "시험 본문", when.AddMinutes(1)), Expected with { Reservation = when }));
        Assert.NotNull(ComposeActions.Difference(State(names, "시험 본문", sms: true), Expected));
        Assert.NotNull(ComposeActions.Difference(State(names, "시험 본문", dialog: true), Expected));
        Assert.Null(ComposeActions.Difference(State(names, "시험 본문", when), Expected with { Reservation = when }));
    }
}
