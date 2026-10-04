using System.Net;
using System.Text;
using System.Text.Json;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;
using static OnTalk.Sidecar.Tests.NoteFixtures;

namespace OnTalk.Sidecar.Tests;

public sealed class ChatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, KoreanNoteTime.SeoulOffset);
    private static AccNode ChatTree(params AccNode[] nodes) => Document("file:///x/windows/chat/app.html?chat#/chat", nodes);
    private static ChatMessage Message(string key = "A", string body = "내일까지 결과표를 보내 주세요.") => new(key, "발신자 미확인", "오전 10:00", new DateOnly(2026, 10, 4), body);

    [Fact]
    public void 관찰한_목록_경로는_대화_메뉴로_판별한다() => Assert.Equal(MainMenu.Conversations,
        WindowContextParser.Parse(Document("file:///x/windows/renderer/app.html#/chatrooms", [])).Menu);

    [Fact]
    public void 날짜와_시각_다음_텍스트를_읽고_불명확한_발신자는_추정하지_않는다()
    {
        var root = ChatTree(Text("싱글창"), Text("멀티창"), Text("예시 상대"), Text("주무관"), Text("온라인"),
            Text("2026년 10월 4일 일"), Text("오전 10:00"), Text("결과를 확인해 주세요."),
            new AccNode("graphic", "", "", 0, []), Text("상대 A"), Text(""), Text("주무관 │ 총무과"),
            Text("오전 10:01"), Text("확인했어요."), new AccNode(AccNode.EditableText, "비공개 입력", "읽지 않을 초안", 0, []), Text("전송"));
        var snapshot = ChatWindowParser.Parse(root, Now)!;
        Assert.Equal(2, snapshot.Messages.Count); Assert.Equal("발신자 미확인", snapshot.Messages[0].Sender);
        Assert.Equal("상대 A", snapshot.Messages[1].Sender); Assert.Equal(new DateOnly(2026, 10, 4), snapshot.Messages[1].Date);
        Assert.DoesNotContain("읽지 않을 초안", string.Join("\n", snapshot.Messages.Select(m => m.Body)));
        Assert.Contains("전체 이력", snapshot.Coverage); Assert.DoesNotContain("온라인", snapshot.RoomHint);
    }

    [Fact]
    public void 반복한_같은_메시지는_순번으로_구분한다()
    {
        var snapshot = ChatWindowParser.Parse(ChatTree(Text("2026년 10월 4일 일"), Text("오전 10:00"), Text("네 확인했습니다"),
            Text("오전 10:00"), Text("네 확인했습니다")), Now)!;
        Assert.Equal(2, snapshot.Messages.Select(m => m.Key).Distinct().Count());
    }

    [Fact]
    public void 날짜_표식_없는_창은_본문으로_추정하지_않는다() => Assert.Empty(ChatWindowParser.Parse(ChatTree(Text("오전 10:00"), Text("자료")), Now)!.Messages);

    [Fact]
    public void 숨긴_본문과_그래픽_파일은_일반_메시지로_사용하지_않는다()
    {
        var hidden = Div(Text("오전 10:00"), Text("숨긴 자료")) with { State = 0x8000 };
        var snapshot = ChatWindowParser.Parse(ChatTree(Text("2026년 10월 4일 일"), hidden, Text("오전 10:01"),
            new AccNode("graphic", "파일.png", "", 0, []), Text("파일.png"), Text("오전 10:02"),
            new AccNode("link", "첨부 파일", "", 0, [Text("첨부.txt")]), Text("오전 10:03"), Text("텍스트 자료")) with { IsTruncated = true }, Now)!;
        Assert.Equal("텍스트 자료", Assert.Single(snapshot.Messages).Body); Assert.Contains("읽기 한도", snapshot.Coverage);
    }

    [Fact]
    public void 대화_아닌_화면의_시각과_글을_읽지_않는다() => Assert.Null(ChatWindowParser.Parse(Document("file:///x/windows/userSearch/app.html", [Text("2026년 10월 4일 일"), Text("오전 10:00"), Text("개인정보")]), Now));

    [Fact]
    public void 날짜_전환을_보존한다()
    {
        var snapshot = ChatWindowParser.Parse(ChatTree(Text("2026년 10월 3일 토"), Text("오후 11:59"), Text("어제 글"),
            Text("2026년 10월 4일 일"), Text("오전 12:00"), Text("오늘 글")), Now)!;
        Assert.Equal(new DateOnly(2026, 10, 3), snapshot.Messages[0].Date); Assert.Equal(new DateOnly(2026, 10, 4), snapshot.Messages[1].Date);
    }

    [Fact]
    public void 목록_미리보기와_자기_프로필은_메시지_본문으로_분석하지_않는다()
    {
        var row = new AccNode(AccNode.ListItem, "", "", 0, [Text("예시 방"), Text("3")]) { Path = "1" };
        var root = Document("file:///x/windows/renderer/app.html#/chatrooms", [new AccNode("list", "", "", 0, [row, Text("최근 글")]),
            new AccNode("list", "", "", 0, [new AccNode(AccNode.ListItem, "", "", 0, [Text("나"), Text("주무관")])])]);
        Assert.Equal("최근 글", Assert.Single(ChatWindowParser.ParseList(root)).Preview); Assert.Null(ChatWindowParser.Parse(root, Now));
    }

    [Fact]
    public void 메시지가_추가되면_세션은_유지하고_같은_제목의_다른_범위는_새로_구분한다()
    {
        var tracker = new WindowSessionTracker(); var context = new SidecarContext(OnTalkSurface.Chat);
        var first = new ChatSnapshot("같은 이름", [Message()], Now);
        var id = tracker.Observe(new(1), new(2), 3, context, chat: first);
        Assert.Equal(id, tracker.Observe(new(1), new(2), 3, context, chat: first with { Messages = [Message(), Message("B", "새 글")] }));
        Assert.NotEqual(id, tracker.Observe(new(1), new(2), 3, context, chat: first with { Messages = [Message("C", "다른 방 글")] }));
    }

    [Theory]
    [InlineData("action", 1, "결과표", 1)]
    [InlineData("decision", 1, "없는 내용", 0)]
    [InlineData("question", 2, "결과표", 0)]
    [InlineData("unknown", 1, "결과표", 0)]
    [InlineData("action", 1, "", 0)]
    public void 후보의_메시지_번호와_정확한_원문_근거를_확인한다(string kind, int index, string quote, int count)
    {
        var result = ChatAnalysisValidator.Validate(new("요약", [new(kind, "후보", index, quote)]), [Message()]);
        Assert.Equal(count, result.Points.Count); Assert.Equal(1 - count, result.Dropped);
    }

    [Fact]
    public async Task 대화_AI는_범위와_메시지_근거를_보내고_불일치를_제외한다()
    {
        using var handler = new FakeHandler("{\"summary\":\"흐름\",\"points\":[{\"kind\":\"action\",\"title\":\"결과 확인\",\"message\":1,\"evidence\":\"결과표\"},{\"kind\":\"decision\",\"title\":\"확정\",\"message\":1,\"evidence\":\"없는 문장\"}]}");
        using var ai = new OllamaAnalyzer(handler: handler);
        var result = await ai.AnalyzeChatAsync([Message()], CancellationToken.None);
        Assert.Single(result.Points); Assert.Equal(1, result.Dropped); Assert.Contains("발신자 미확인", handler.Request);
        Assert.Contains("127.0.0.1", handler.Url); Assert.Contains("kind", handler.Request);
    }

    [Fact]
    public async Task 대화_답변은_쪽지_인사말_정책과_분리한다()
    {
        using var handler = new FakeHandler("확인 후 답변드릴게요."); using var writer = new OllamaWriter(handler: handler);
        Assert.Equal("확인 후 답변드릴게요.", await writer.SuggestChatAsync([Message()], "현재 초안", "확인 필요", CancellationToken.None));
        Assert.Contains("인사말이나 서명을 자동으로 붙이지 않는다", handler.Request); Assert.DoesNotContain("인사말로 시작", handler.Request);
    }

    [Fact]
    public async Task 자연어_검색어는_제안만_반환한다()
    {
        using var handler = new FakeHandler("[\"출장\",\"여비\"]"); using var writer = new OllamaWriter(handler: handler);
        Assert.Equal(new[] { "출장", "여비" }, await writer.SuggestContactTermsAsync("출장 여비 담당자는 누구인가요?", CancellationToken.None));
        Assert.Contains("사람이나 담당 부서를 추정하지 않는다", handler.Request);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("담당자는 예시입니다")]
    [InlineData("[\"\"]")]
    public async Task 잘못된_검색어_응답은_후보_검색에_사용하지_않는다(string reply)
    {
        using var handler = new FakeHandler(reply); using var writer = new OllamaWriter(handler: handler);
        await Assert.ThrowsAsync<AnalysisUnavailableException>(() => writer.SuggestContactTermsAsync("담당자?", CancellationToken.None));
    }

    private sealed class FakeHandler(string reply) : HttpMessageHandler
    {
        public string Request { get; private set; } = "";
        public string Url { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = System.Text.RegularExpressions.Regex.Unescape(await request.Content!.ReadAsStringAsync(cancellationToken));
            Url = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { message = new { content = reply } }), Encoding.UTF8, "application/json") };
        }
    }
}
