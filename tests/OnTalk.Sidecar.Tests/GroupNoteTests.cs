using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

public sealed class GroupNoteTests
{
    private static readonly ContactPerson Kim = new("1", "김예시", "주무관", ["예시시", "총무과"]);
    private static readonly ContactPerson Lee = new("2", "이예시", "주무관", ["예시시", "회계과"]);

    [Fact]
    public async Task 같은_쪽지는_한_창에_모두_추가하고_예약까지_검증한_뒤_한번_보낸다()
    {
        var log = new FakeLog();
        var fake = new FakeAutomation(new Dictionary<string, IReadOnlyList<PersonRow>>
        {
            ["김예시"] = [Row("김예시", "주무관", "예시시 / 총무과")],
            ["이예시"] = [Row("이예시", "주무관", "예시시 / 회계과")],
        });
        var at = new DateTime(2026, 10, 20, 9, 30, 0);

        var outcome = await GroupNoteDispatcher.RunAsync([Kim, Lee], "회의 안내\r\n내일 10시", at, true, log, fake);

        Assert.Equal(GroupNoteResult.Sent, outcome.Result);
        Assert.True(outcome.Attempted);
        Assert.Equal(1, fake.EnsureCount);
        Assert.Equal(1, fake.SendCount);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(["김예시", "이예시"], entry.Names);
        Assert.Equal("회의 안내\n내일 10시", entry.Body);
        Assert.Equal(at, entry.ReserveAt);
        Assert.Equal(DispatchStatus.WindowClosed, entry.Status);
    }

    [Fact]
    public async Task 한_사람이라도_확정하지_못하면_일부에게_보내지_않고_멈춘다()
    {
        var log = new FakeLog();
        var fake = new FakeAutomation(new Dictionary<string, IReadOnlyList<PersonRow>>
        {
            ["김예시"] = [Row("김예시", "주무관", "예시시 / 총무과")],
            ["이예시"] = [Row("이예시", "주무관", "예시시 / 회계과"), Row("이예시", "주무관", "예시시 / 회계과")],
        });

        var outcome = await GroupNoteDispatcher.RunAsync([Kim, Lee], "안내", null, true, log, fake);

        Assert.Equal(GroupNoteResult.Stopped, outcome.Result);
        Assert.False(outcome.Attempted);
        Assert.Equal(1, outcome.AddedCount);
        Assert.Equal(1, fake.ClosedSearchCount);
        Assert.Equal(0, fake.SendCount);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task 준비만_하면_온톡_창을_채우고_전송하지_않는다()
    {
        var log = new FakeLog();
        var fake = new FakeAutomation(new Dictionary<string, IReadOnlyList<PersonRow>>
        {
            ["김예시"] = [Row("김예시", "주무관", "예시시 / 총무과")],
        });

        var outcome = await GroupNoteDispatcher.RunAsync([Kim], "안내", null, false, log, fake);

        Assert.Equal(GroupNoteResult.Prepared, outcome.Result);
        Assert.False(outcome.Attempted);
        Assert.Equal(0, fake.SendCount);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task 창이_닫히지_않으면_결과_불명으로_남기고_시도한_것으로_본다()
    {
        var log = new FakeLog();
        var fake = new FakeAutomation(new Dictionary<string, IReadOnlyList<PersonRow>>
        {
            ["김예시"] = [Row("김예시", "주무관", "예시시 / 총무과")],
        })
        { SendResult = new SendOutcome(SendStatus.StillOpen, "창이 닫히지 않음") };

        var outcome = await GroupNoteDispatcher.RunAsync([Kim], "안내", null, true, log, fake);

        Assert.Equal(GroupNoteResult.Uncertain, outcome.Result);
        Assert.True(outcome.Attempted);
        Assert.Equal(DispatchStatus.StillOpen, Assert.Single(log.Entries).Status);
    }

    [Fact]
    public async Task 표시_정보가_같은_대상이_겹치면_시작하지_않는다()
    {
        var fake = new FakeAutomation(new Dictionary<string, IReadOnlyList<PersonRow>>());
        var outcome = await GroupNoteDispatcher.RunAsync([Kim, Kim with { Key = "9" }], "안내", null, true, new FakeLog(), fake);
        Assert.Equal(GroupNoteResult.Stopped, outcome.Result);
        Assert.Equal(0, fake.EnsureCount);
    }

    private static PersonRow Row(string name, string title, string detail) =>
        new(name, title, [detail], new AccNode(AccNode.ListItem, name, "", 0, []));

    private sealed class FakeLog : IDispatchLog
    {
        public List<(IReadOnlyList<string> Names, string Body, DateTime? ReserveAt, DispatchStatus? Status)> Entries { get; } = [];

        public long BeginDispatch(IReadOnlyList<string> recipientNames, string body, DateTime? reserveAt, DateTimeOffset at)
        {
            Entries.Add((recipientNames.ToList(), body, reserveAt, null));
            return Entries.Count;
        }

        public void FinishDispatch(long id, DispatchStatus status, string detail) =>
            Entries[(int)id - 1] = Entries[(int)id - 1] with { Status = status };
    }

    private sealed class FakeAutomation(IReadOnlyDictionary<string, IReadOnlyList<PersonRow>> directory) : IPersonalizedAutomation
    {
        private ComposeState _state = new([], "", 0, 4000, false, false, default);
        public int EnsureCount { get; private set; }
        public int SendCount { get; private set; }
        public int ClosedSearchCount { get; private set; }
        public SendOutcome SendResult { get; set; } = new(SendStatus.WindowClosed, "");

        public (ActionStatus Status, IntPtr ComposeWindow) EnsureEmptyNewComposeWindow()
        {
            EnsureCount++;
            _state = new([], "", 0, 4000, false, false, default);
            return (ActionStatus.Ok, new IntPtr(100));
        }

        public ComposeState? ReadCompose(IntPtr compose) => _state;

        public SearchOutcome SearchRecipient(IntPtr compose, string name) =>
            new(ActionStatus.Ok, new IntPtr(900), directory.GetValueOrDefault(name, []));

        public ActionStatus AddRecipient(IntPtr compose, IntPtr search, PersonRow person)
        {
            _state = _state with { Recipients = [.. _state.Recipients, person.Name] };
            return ActionStatus.Ok;
        }

        public void CloseUserSearchWindow(IntPtr search) => ClosedSearchCount++;

        public ApplyResult ApplyBody(IntPtr compose, string body, string? expectedCurrentBody)
        {
            var normalized = body.Replace("\r\n", "\n");
            _state = _state with { Body = normalized, UsedBytes = NoteBytes.Count(normalized) };
            return new ApplyResult(ApplyStatus.Applied, _state);
        }

        public ActionStatus SetReservation(IntPtr compose, DateTime when)
        {
            _state = _state with { Reservation = when };
            return ActionStatus.Ok;
        }

        public SendOutcome Send(IntPtr compose, ExpectedCompose expected)
        {
            SendCount++;
            return ComposeActions.Difference(_state, expected) is { } diff ? new SendOutcome(SendStatus.Mismatch, diff) : SendResult;
        }
    }
}

public sealed class SnippetBriefTests
{
    [Fact]
    public void 목록용_요약은_공백을_줄이고_50자에서_자른다()
    {
        Assert.Equal("짧은 글", Snippet.Brief("짧은\r\n\r\n   글"));
        var brief = Snippet.Brief(string.Concat(Enumerable.Repeat("가나다라마", 20)));
        Assert.Equal(51, brief.Length);
        Assert.EndsWith("…", brief);
    }

    [Fact]
    public void 이모지_가운데를_자르지_않는다()
    {
        var text = new string('가', 49) + "😀뒤";
        var brief = Snippet.Brief(text);
        Assert.Equal(new string('가', 49) + "…", brief);
    }
}
