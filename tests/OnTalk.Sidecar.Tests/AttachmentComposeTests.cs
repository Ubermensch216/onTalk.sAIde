using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

/// <summary>쪽지 첨부 파일·SMS 발송: 온톡 작성 창 읽기, 사전 점검, 발송 순서와 전송 전 검증.</summary>
public sealed class AttachmentComposeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.FromHours(9));
    private static readonly ContactPerson Kim = new("1", "김예시", "주무관", ["예시시", "총무과"]);
    private readonly string _dir = Directory.CreateTempSubdirectory("sAIde-attach-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string File(string name, int bytes = 10)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    // ── 작성 창 읽기 ──

    [Fact]
    public void 작성_창의_첨부_목록을_붙인_순서대로_읽는다()
    {
        var content = NoteWindowParser.Parse(NoteFixtures.ComposeWithAttachments("새 쪽지", "보고서.hwp", "표 (최종).xlsx"), Now);

        Assert.Equal(["보고서.hwp", "표 (최종).xlsx"], content.Compose!.Attachments);
    }

    [Fact]
    public void 첨부가_없을_때_나오는_안내_문구는_파일로_읽지_않는다()
    {
        var content = NoteWindowParser.Parse(NoteFixtures.ComposeWithAttachments("새 쪽지"), Now);

        Assert.Empty(content.Compose!.Attachments);
    }

    [Fact]
    public void 첨부_영역이_없는_작성_창은_첨부_없음으로_본다()
    {
        Assert.Empty(NoteWindowParser.Parse(NoteFixtures.Compose("새 쪽지"), Now).Compose!.Attachments);
    }

    [Fact]
    public void 첨부가_붙은_창은_빈_새_쪽지로_보지_않는다()
    {
        // 시험 화면에는 수신자가 한 명 들어 있으므로 수신자만 비워 첨부 여부의 영향만 본다.
        var empty = NoteWindowParser.Parse(NoteFixtures.ComposeWithAttachments("새 쪽지"), Now).Compose! with { Recipients = [] };
        var attached = NoteWindowParser.Parse(NoteFixtures.ComposeWithAttachments("새 쪽지", "a.txt"), Now).Compose! with { Recipients = [] };

        Assert.True(ComposeActions.IsEmptyNewCompose(empty));
        Assert.False(ComposeActions.IsEmptyNewCompose(attached));
    }

    // ── 전송 전 검증 ──

    private static ComposeState State(IReadOnlyList<string> attachments, bool sms = false) =>
        new(["김예시"], "본문", NoteBytes.Count("본문"), 4000, sms, false, default) { Attachments = attachments };

    [Fact]
    public void 첨부가_기대와_다르면_전송하지_않는다()
    {
        var expected = new ExpectedCompose(["김예시"], "본문", null, false, ["a.txt", "b.txt"]);

        Assert.Null(ComposeActions.Difference(State(["b.txt", "a.txt"]), expected));
        Assert.NotNull(ComposeActions.Difference(State(["a.txt"]), expected));
        Assert.NotNull(ComposeActions.Difference(State([]), expected));
        Assert.NotNull(ComposeActions.Difference(State(["a.txt", "b.txt", "c.txt"]), expected));
    }

    [Fact]
    public void 첨부를_기대하지_않았는데_온톡_창에_붙어_있으면_전송하지_않는다()
    {
        var expected = new ExpectedCompose(["김예시"], "본문", null, false);

        Assert.Null(ComposeActions.Difference(State([]), expected));
        Assert.NotNull(ComposeActions.Difference(State(["몰래붙음.zip"]), expected));
    }

    [Fact]
    public void SMS_선택_상태도_기대와_같아야_한다()
    {
        var expected = new ExpectedCompose(["김예시"], "본문", null, true);

        Assert.Null(ComposeActions.Difference(State([], sms: true), expected));
        Assert.NotNull(ComposeActions.Difference(State([], sms: false), expected));
    }

    // ── 사전 점검 ──

    [Fact]
    public void 정상_파일은_통과한다()
    {
        Assert.Null(NoteAttachments.Validate([File("a.hwp"), File("b.pdf")]));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("SETUP.EXE")]
    [InlineData("바로가기.lnk")]
    public void 온톡이_받지_않는_형식은_거른다(string name)
    {
        Assert.Contains("형식", NoteAttachments.Validate([File(name)]));
    }

    [Fact]
    public void 없는_파일_빈_파일_큰_파일은_거른다()
    {
        Assert.Contains("찾을 수 없습니다", NoteAttachments.Validate([Path.Combine(_dir, "없음.txt")]));
        Assert.Contains("빈 파일", NoteAttachments.Validate([File("빈.txt", 0)]));
        var big = Path.Combine(_dir, "큰.bin");
        using (var stream = new FileStream(big, FileMode.Create)) stream.SetLength(NoteAttachments.MaxFileBytes + 1);
        Assert.Contains("20MB", NoteAttachments.Validate([big]));
    }

    [Fact]
    public void 열한_개부터는_거르고_이름이_같은_파일도_거른다()
    {
        var files = Enumerable.Range(1, NoteAttachments.MaxCount + 1).Select(i => File($"f{i}.txt")).ToList();
        Assert.Null(NoteAttachments.Validate(files.Take(NoteAttachments.MaxCount).ToList()));
        Assert.Contains("최대", NoteAttachments.Validate(files));

        Directory.CreateDirectory(Path.Combine(_dir, "다른폴더"));
        var other = Path.Combine(_dir, "다른폴더", "f1.txt");
        System.IO.File.WriteAllBytes(other, new byte[3]);
        Assert.Contains("이름이 같은", NoteAttachments.Validate([files[0], other]));
    }

    [Fact]
    public void 상대_경로는_거른다()
    {
        Assert.NotNull(NoteAttachments.Validate(["a.txt"]));
    }

    // ── 발송 흐름 ──

    private static Task<GroupNoteOutcome> Run(FakeLog log, FakeAutomation fake, IReadOnlyList<string> files, bool sms, bool send = true) =>
        GroupNoteDispatcher.RunAsync([Kim], "안내", null, send, log, fake, attachments: files, sms: sms);

    [Fact]
    public async Task 파일을_차례로_붙이고_SMS를_고른_뒤_검증해서_보낸다()
    {
        var log = new FakeLog();
        var fake = new FakeAutomation();
        var files = new[] { File("보고서.hwp"), File("표.xlsx") };

        var outcome = await Run(log, fake, files, sms: true);

        Assert.Equal(GroupNoteResult.Sent, outcome.Result);
        Assert.Equal(files, fake.AttachedPaths);
        Assert.True(fake.SmsRequested);
        Assert.Contains("첨부 2개", outcome.Message);
        Assert.Equal(1, fake.SendCount);
    }

    [Fact]
    public async Task 첨부가_없고_SMS도_끄면_이전과_같이_동작한다()
    {
        var fake = new FakeAutomation();

        var outcome = await Run(new FakeLog(), fake, [], sms: false);

        Assert.Equal(GroupNoteResult.Sent, outcome.Result);
        Assert.Empty(fake.AttachedPaths);
        Assert.Null(fake.SmsRequested);
        Assert.DoesNotContain("첨부", outcome.Message);
    }

    [Fact]
    public async Task 준비만_할_때도_첨부를_붙이고_전송은_하지_않는다()
    {
        var fake = new FakeAutomation();

        var outcome = await Run(new FakeLog(), fake, [File("a.txt")], sms: false, send: false);

        Assert.Equal(GroupNoteResult.Prepared, outcome.Result);
        Assert.Single(fake.AttachedPaths);
        Assert.Equal(0, fake.SendCount);
    }

    [Fact]
    public async Task 첨부가_온톡에서_거절되면_보내지_않고_멈춘다()
    {
        var log = new FakeLog();
        var fake = new FakeAutomation { AttachResult = ActionStatus.Mismatch };

        var outcome = await Run(log, fake, [File("a.txt")], sms: false);

        Assert.Equal(GroupNoteResult.Stopped, outcome.Result);
        Assert.False(outcome.Attempted);
        Assert.Contains("a.txt", outcome.Message);
        Assert.Equal(0, fake.SendCount);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task 점검에_걸리는_파일이면_온톡을_열지_않고_멈춘다()
    {
        var fake = new FakeAutomation();

        var outcome = await Run(new FakeLog(), fake, [File("setup.exe")], sms: false);

        Assert.Equal(GroupNoteResult.Stopped, outcome.Result);
        Assert.Equal(0, fake.EnsureCount);
    }

    private sealed class FakeLog : IDispatchLog
    {
        public List<string> Entries { get; } = [];
        public long BeginDispatch(IReadOnlyList<string> recipientNames, string body, DateTime? reserveAt, DateTimeOffset at)
        {
            Entries.Add(body);
            return Entries.Count;
        }
        public void FinishDispatch(long id, DispatchStatus status, string detail) { }
    }

    private sealed class FakeAutomation : IPersonalizedAutomation
    {
        private ComposeState _state = new([], "", 0, 4000, false, false, default);
        private static readonly PersonRow Row = new("김예시", "주무관", ["예시시 / 총무과"], new AccNode(AccNode.ListItem, "김예시", "", 0, []));
        public int EnsureCount { get; private set; }
        public int SendCount { get; private set; }
        public List<string> AttachedPaths { get; } = [];
        public ActionStatus AttachResult { get; set; } = ActionStatus.Ok;
        public bool? SmsRequested { get; private set; }

        public (ActionStatus Status, IntPtr ComposeWindow) EnsureEmptyNewComposeWindow()
        {
            EnsureCount++;
            return (ActionStatus.Ok, new IntPtr(100));
        }
        public ComposeState? ReadCompose(IntPtr compose) => _state;
        public SearchOutcome SearchRecipient(IntPtr compose, string name) => new(ActionStatus.Ok, new IntPtr(900), [Row]);
        public ActionStatus AddRecipient(IntPtr compose, IntPtr search, PersonRow person)
        {
            _state = _state with { Recipients = [.. _state.Recipients, person.Name] };
            return ActionStatus.Ok;
        }
        public void CloseUserSearchWindow(IntPtr search) { }
        public ApplyResult ApplyBody(IntPtr compose, string body, string? expectedCurrentBody)
        {
            _state = _state with { Body = body.Replace("\r\n", "\n"), UsedBytes = NoteBytes.Count(body) };
            return new ApplyResult(ApplyStatus.Applied, _state);
        }
        public ActionStatus SetReservation(IntPtr compose, DateTime when) => ActionStatus.Ok;
        public ActionStatus AttachFile(IntPtr compose, string path)
        {
            if (AttachResult != ActionStatus.Ok) return AttachResult;
            AttachedPaths.Add(path);
            _state = _state with { Attachments = [.. _state.Attachments, Path.GetFileName(path)] };
            return ActionStatus.Ok;
        }
        public ActionStatus SetSms(IntPtr compose, bool on)
        {
            SmsRequested = on;
            _state = _state with { SmsChecked = on };
            return ActionStatus.Ok;
        }
        public SendOutcome Send(IntPtr compose, ExpectedCompose expected)
        {
            SendCount++;
            return ComposeActions.Difference(_state, expected) is { } diff
                ? new SendOutcome(SendStatus.Mismatch, diff)
                : new SendOutcome(SendStatus.WindowClosed, "");
        }
    }
}
