using System.Net;
using System.Text;
using System.Text.Json;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

public class DueDateParserTests
{
    private static readonly DateOnly Sent = new(2026, 10, 2);

    [Theory]
    [InlineData("10월 15일까지", 2026, 10, 15)]
    [InlineData("10. 15.(목)까지 제출", 2026, 10, 15)]
    [InlineData("10/15 18:00", 2026, 10, 15)]
    [InlineData("2026. 11. 3.까지", 2026, 11, 3)]
    [InlineData("2027-01-05", 2027, 1, 5)]
    [InlineData("10. 5.~10. 16.", 2026, 10, 16)]
    [InlineData("1월 9일까지", 2027, 1, 9)]
    [InlineData("내일 오전까지", 2026, 10, 3)]
    public void 날짜_표현을_해석한다(string text, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), DueDateParser.Parse(text, Sent));
    }

    [Theory]
    [InlineData("연간 56시간 이상")]
    [InlineData("가능한 한 빨리")]
    [InlineData("이번 주 금요일까지")]
    [InlineData("13월 40일")]
    [InlineData("평균 3.5점")]
    [InlineData("")]
    public void 날짜가_아니거나_모호하면_추측하지_않는다(string text)
    {
        Assert.Null(DueDateParser.Parse(text, Sent));
    }
}

public class AnalysisValidatorTests
{
    private const string Body = "감사위원회 직원은 연간 56시간 이상 감사교육 이수해야 하니,\n아래를 참고하여 교육이수를 해주시기 바랍니다.\n결과는 10월 15일까지 제출 바랍니다.";
    private static readonly DateOnly Sent = new(2026, 10, 2);

    private static NoteAnalysis Validate(params RawAction[] actions) =>
        AnalysisValidator.Validate(new RawAnalysis("요약", [.. actions], ["  ", "실제 이수시간"]), Body, Sent);

    [Fact]
    public void 원문에_있는_근거와_기한만_받아들인다()
    {
        var result = Validate(new RawAction("결과 제출", "결과는 10월 15일까지 제출 바랍니다.", "10월 15일까지"));

        var action = Assert.Single(result.Actions);
        Assert.Equal("10월 15일까지", action.DueText);
        Assert.Equal(new DateOnly(2026, 10, 15), action.Due);
        Assert.Equal(["실제 이수시간"], result.Uncertainties);
    }

    [Fact]
    public void 근거가_원문에_없으면_후보를_버린다()
    {
        var result = Validate(new RawAction("보고서 작성", "보고서를 작성해 주세요.", ""));

        Assert.Empty(result.Actions);
        Assert.Equal(1, result.DroppedActions);
    }

    [Fact]
    public void 원문에_없는_기한은_비운다()
    {
        var result = Validate(new RawAction("교육 이수", "아래를 참고하여 교육이수를 해주시기 바랍니다.", "12월 31일까지"));

        var action = Assert.Single(result.Actions);
        Assert.Null(action.DueText);
        Assert.Null(action.Due);
    }

    [Fact]
    public void 기한_표현은_있지만_날짜가_아니면_날짜를_만들지_않는다()
    {
        var result = Validate(new RawAction("교육 이수", "연간 56시간 이상 감사교육 이수해야 하니", "연간 56시간 이상"));

        var action = Assert.Single(result.Actions);
        Assert.Equal("연간 56시간 이상", action.DueText);
        Assert.Null(action.Due);
    }

    [Fact]
    public void 줄바꿈과_띄어쓰기_차이는_같은_문장으로_본다()
    {
        var result = Validate(new RawAction("교육 이수",
            "감사위원회 직원은 연간 56시간 이상 감사교육 이수해야 하니, 아래를 참고하여 교육이수를 해주시기 바랍니다.", ""));

        Assert.Single(result.Actions);
    }
}

public class OllamaAnalyzerTests
{
    private static readonly ReceivedNote Note = new("홍길동", "주무관", "10월 2일 오후 07:51", null, 1,
        "자료를 10월 15일까지 제출해 주세요.", []);

    private sealed class FakeOllama(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { message = new { role = "assistant", content } }), Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task 모델_결과를_원문과_대조해_돌려준다()
    {
        var handler = new FakeOllama(_ => Reply("""
            {"summary":"자료 제출 요청","actions":[
              {"title":"자료 제출","evidence":"자료를 10월 15일까지 제출해 주세요.","due_text":"10월 15일까지"},
              {"title":"회의 참석","evidence":"회의에 참석해 주세요.","due_text":""}],
             "uncertainties":[]}
            """));
        using var analyzer = new OllamaAnalyzer("test-model", handler);

        var result = await analyzer.AnalyzeAsync(Note, new DateOnly(2026, 10, 2), CancellationToken.None);

        Assert.Equal("자료 제출", Assert.Single(result.Actions).Title);
        Assert.Equal(new DateOnly(2026, 10, 15), result.Actions[0].Due);
        Assert.Equal(1, result.DroppedActions);
    }

    [Fact]
    public async Task 이_PC의_Ollama에만_요청한다()
    {
        var handler = new FakeOllama(_ => Reply("""{"summary":"","actions":[],"uncertainties":[]}"""));
        using var analyzer = new OllamaAnalyzer("test-model", handler);

        await analyzer.AnalyzeAsync(Note, new DateOnly(2026, 10, 2), CancellationToken.None);

        Assert.Equal("127.0.0.1", handler.RequestUri!.Host);
        Assert.Contains("test-model", handler.RequestBody);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    public async Task 해석할_수_없는_답은_분석_불가로_알린다(string content)
    {
        using var analyzer = new OllamaAnalyzer("test-model", new FakeOllama(_ => Reply(content)));

        await Assert.ThrowsAsync<AnalysisUnavailableException>(
            () => analyzer.AnalyzeAsync(Note, new DateOnly(2026, 10, 2), CancellationToken.None));
    }

    [Fact]
    public async Task 연결_실패는_분석_불가로_알린다()
    {
        using var analyzer = new OllamaAnalyzer("test-model",
            new FakeOllama(_ => throw new HttpRequestException("connection refused")));

        await Assert.ThrowsAsync<AnalysisUnavailableException>(
            () => analyzer.AnalyzeAsync(Note, new DateOnly(2026, 10, 2), CancellationToken.None));
    }

    [Fact]
    public async Task 사용자가_취소하면_취소로_끝난다()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var analyzer = new OllamaAnalyzer("test-model",
            new FakeOllama(_ => throw new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(Note, new DateOnly(2026, 10, 2), cancel.Token));
    }
}

public class ReminderRuleTests
{
    private static readonly DateOnly Due = new(2026, 10, 15);

    private static DateTimeOffset Seoul(int day, int hour) => new(2026, 10, day, hour, 0, 0, KoreanNoteTime.SeoulOffset);

    private static TaskItem Task(DateOnly? due, bool done = false, int stage = 0) =>
        new(1, null, "제출", null, null, due, done, Seoul(1, 9), stage);

    [Theory]
    [InlineData(13, 23, 0)]
    [InlineData(14, 8, 0)]
    [InlineData(14, 9, ReminderRule.DayBefore)]
    [InlineData(15, 8, ReminderRule.DayBefore)]
    [InlineData(15, 9, ReminderRule.DueDay)]
    [InlineData(20, 9, ReminderRule.DueDay)]
    public void 전날과_당일_9시에_단계가_오른다(int day, int hour, int expected)
    {
        Assert.Equal(expected, ReminderRule.StageReached(Due, Seoul(day, hour)));
    }

    [Fact]
    public void 이미_알린_단계는_다시_알리지_않는다()
    {
        Assert.True(ReminderRule.IsDue(Task(Due), Seoul(14, 10)));
        Assert.False(ReminderRule.IsDue(Task(Due, stage: ReminderRule.DayBefore), Seoul(14, 10)));
        Assert.True(ReminderRule.IsDue(Task(Due, stage: ReminderRule.DayBefore), Seoul(15, 10)));
    }

    [Fact]
    public void 기한이_없거나_완료한_업무는_알리지_않는다()
    {
        Assert.False(ReminderRule.IsDue(Task(null), Seoul(20, 9)));
        Assert.False(ReminderRule.IsDue(Task(Due, done: true), Seoul(20, 9)));
    }
}

public class NoteBytesTests
{
    // 온톡 5.4.3 새 쪽지 창에 붙여넣어 관측한 값.
    [Theory]
    [InlineData("abc", 3)]
    [InlineData("가나다", 9)]
    [InlineData("a\nb", 3)]
    [InlineData("a\r\nb", 3)]
    [InlineData("ㅇ□", 6)]
    [InlineData("✅", 3)]
    [InlineData("✔️", 6)]
    [InlineData("😀", 4)]
    [InlineData("안녕하세요. Test 123\n둘째 줄 ✅", 40)]
    public void 온톡_표시와_같은_값을_낸다(string text, int expected)
    {
        Assert.Equal(expected, NoteBytes.Count(text));
    }
}
