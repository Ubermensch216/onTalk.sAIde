using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.Tests;

public sealed class PersonalizedNoteTests
{
    private static ContactPerson Person(string name = "김예시") => new("row", name, "주무관", ["예시시", "총무과"]);

    [Fact]
    public void 사용자_정의_헤더_변수를_치환하고_이름과_소속은_덮어쓰지_못한다()
    {
        var a = NotePersonalization.Render(Person(), "{소속} {이름} ({직급}): {과정명} / {교육장소}", new Dictionary<string, string> { ["이름"] = "다른사람", ["과정명"] = "정보보안", ["교육장소"] = "대강당" });
        var b = NotePersonalization.Render(Person("이예시"), "{소속} {이름} ({직급}): {과정명} / {교육장소}", new Dictionary<string, string> { ["과정명"] = "개인정보", ["교육장소"] = "세미나실" });
        Assert.True(a.Ready); Assert.True(b.Ready);
        Assert.Equal("예시시 · 총무과 김예시 (주무관): 정보보안 / 대강당", a.Body);
        Assert.Equal("예시시 · 총무과 이예시 (주무관): 개인정보 / 세미나실", b.Body);
    }

    [Theory]
    [InlineData("{과정명}")]
    [InlineData("{없는헤더}")]
    [InlineData("")]
    public void 누락값과_없는헤더와_빈본문을_차단한다(string template) =>
        Assert.False(NotePersonalization.Render(Person(), template, new Dictionary<string, string> { ["과정명"] = "   " }).Ready);

    [Fact]
    public void 입력값의_변수를_재귀적으로_치환하지_않는다()
    {
        var result = NotePersonalization.Render(Person(), "{안내문구}", new Dictionary<string, string> { ["안내문구"] = "{이름}" });
        Assert.Equal("{이름}", result.Body); Assert.False(result.Ready);
    }

    [Fact]
    public void 치환후_바이트_한도를_검사한다() => Assert.False(NotePersonalization.Render(Person(), "{안내문구}",
        new Dictionary<string, string> { ["안내문구"] = new string('한', 1400) }).Ready);

    [Theory]
    [InlineData("예시시 / 총무과", true)]
    [InlineData("예시시 / 총무과지원팀", false)]
    [InlineData("다른시 / 총무과", false)]
    public void 조직_전체_이름으로_수신자를_대조한다(string detail, bool expected) =>
        Assert.Equal(expected, PreparedRecipientMatch.Matches(Person(), new("김예시", "주무관", [detail])));

    [Theory]
    [InlineData("부산광역시 / 사하구 / 자치행정국 / 민원여권과", true)]
    [InlineData("부산광역시 / 영도구 / 자치행정국 / 민원여권과", false)]
    [InlineData("부산광역시 / 사하구 / 자치행정국 / 민원여권과지원팀", false)]
    public void 공백으로_이어_붙인_친구목록_소속도_검색결과의_단계별_소속과_대조한다(string detail, bool expected) =>
        Assert.Equal(expected, PreparedRecipientMatch.Matches(
            new ContactPerson("k", "박경수", "지방행정서기(일반임기제)/주무관", ["부산광역시 사하구", "민원여권과"]),
            new("박경수", "지방행정서기(일반임기제)/주무관", ["부산광역시", detail, "051-220-4292"])));

    [Fact]
    public void 저장후_복구와_계정격리와_오래된_편집기의_발송기록_보존()
    {
        var root = Path.Combine(Path.GetTempPath(), "ontalk-personalized", Guid.NewGuid().ToString("N"));
        try
        {
            var work = new PersonalizedWork
            {
                Template = "{이름} {지급액}",
                Headers = ["지급액"],
                Items = [new() { Person = Person(), Values = new() { ["지급액"] = "100,000원" }, Body = "확정본문" }]
            };
            using (var store = ProfileStore.Open(root, "a"))
            {
                store.SavePersonalizedWork(work);
                var old = Assert.Single(store.GetPersonalizedWorks());
                work.Items[0].DispatchId = 12; work.Items[0].DispatchRevision = 2; work.Items[0].Status = "결과 불명";
                store.SavePersonalizedWork(work);
                old.Items[0].Values["지급액"] = "오래된 값"; old.Items[0].Body = "오래된 본문"; store.SavePersonalizedWork(old);
            }
            using (var store = ProfileStore.Open(root, "a"))
            {
                var saved = Assert.Single(store.GetPersonalizedWorks());
                Assert.Equal(["지급액"], saved.Headers);
                Assert.Equal("100,000원", saved.Items[0]["지급액"]); Assert.Equal("확정본문", saved.Items[0].Body);
                Assert.Equal(12, saved.Items[0].DispatchId); Assert.Equal("결과 불명", saved.Items[0].Status);
                store.DeletePersonalizedWork(saved.Id); Assert.Empty(store.GetPersonalizedWorks());
            }
            using var other = ProfileStore.Open(root, "b"); Assert.Empty(other.GetPersonalizedWorks());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 자동_연속_발송은_빈창을_자동확보해_순차발송하고_미확정_수신자는_보류한뒤_계속진행한다()
    {
        var root = Path.Combine(Path.GetTempPath(), "ontalk-personalized-batch", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = ProfileStore.Open(root, "batch-user");
            var personA = new ContactPerson("1", "김예시", "주무관", ["예시시", "총무과"]);
            var personB = new ContactPerson("2", "이예시", "주무관", ["예시시", "회계과"]);
            var personC = new ContactPerson("3", "박예시", "팀장", ["예시시", "기획과"]);
            var work = new PersonalizedWork
            {
                Template = "{이름} 안내",
                Headers = ["개별안내"],
                Items =
                [
                    new() { Person = personA, Body = "김예시 맞춤 본문" },
                    new() { Person = personB, Body = "이예시 맞춤 본문" },
                    new() { Person = personC, Body = "박예시 맞춤 본문" },
                ]
            };
            var reserveAt = new DateTime(2026, 10, 20, 14, 30, 0);
            var fake = new FakeBatchAutomation(new Dictionary<string, IReadOnlyList<PersonRow>>
            {
                ["김예시"] = [Row("김예시", "주무관", "예시시 / 총무과")],
                // 동명이인 2명이 모두 같은 부서·직급으로 나와 하나로 확정할 수 없는 경우 -> 보류 후 다음 사람 진행
                ["이예시"] = [Row("이예시", "주무관", "예시시 / 회계과"), Row("이예시", "주무관", "예시시 / 회계과")],
                ["박예시"] = [Row("박예시", "팀장", "예시시 / 기획과")],
            });

            var summary = await PersonalizedBatchDispatcher.RunAsync(work, work.Items, reserveAt, store, fake);

            Assert.False(summary.StoppedEarly);
            Assert.Equal(3, summary.TotalCandidates);
            Assert.Equal(2, summary.SentCount);
            Assert.Equal(1, summary.SkippedCount);
            Assert.Equal(1, fake.ClosedSearchWindowCount);
            Assert.True(work.Items[0].Attempted);
            Assert.False(work.Items[1].Attempted);
            Assert.True(work.Items[2].Attempted);
            Assert.Contains("예약 완료", work.Items[0].Status);
            Assert.Contains("동명이인 2명", work.Items[1].Status);
            Assert.Contains("예약 완료", work.Items[2].Status);

            var dispatches = store.GetDispatches();
            Assert.Equal(2, dispatches.Count);
            Assert.All(dispatches, d =>
            {
                Assert.Equal(DispatchStatus.WindowClosed, d.Status);
                Assert.Equal(reserveAt, d.ReserveAt);
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 자동_연속_발송은_전송후_창이_닫히지않으면_즉시중단하고_재발송하지않는다()
    {
        var root = Path.Combine(Path.GetTempPath(), "ontalk-personalized-stop", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = ProfileStore.Open(root, "stop-user");
            var personA = new ContactPerson("1", "김예시", "주무관", ["예시시", "총무과"]);
            var personB = new ContactPerson("2", "박예시", "팀장", ["예시시", "기획과"]);
            var work = new PersonalizedWork
            {
                Items =
                [
                    new() { Person = personA, Body = "첫 번째 본문" },
                    new() { Person = personB, Body = "두 번째 본문" },
                ]
            };
            var fake = new FakeBatchAutomation(new Dictionary<string, IReadOnlyList<PersonRow>>
            {
                ["김예시"] = [Row("김예시", "주무관", "예시시 / 총무과")],
                ["박예시"] = [Row("박예시", "팀장", "예시시 / 기획과")],
            })
            {
                SendResult = new SendOutcome(SendStatus.StillOpen, "창이 닫히지 않음")
            };

            var firstRun = await PersonalizedBatchDispatcher.RunAsync(work, work.Items, null, store, fake);
            Assert.True(firstRun.StoppedEarly);
            Assert.Equal(0, firstRun.SentCount);
            Assert.True(work.Items[0].Attempted);
            Assert.False(work.Items[1].Attempted);
            Assert.Equal("결과 불명 · 직접 확인 필요", work.Items[0].Status);

            // 다시 실행해도 첫 번째 사람(Attempted == true)은 절대 재발송하지 않고 두 번째 사람만 발송한다.
            fake.SendResult = new SendOutcome(SendStatus.WindowClosed, "");
            var secondRun = await PersonalizedBatchDispatcher.RunAsync(work, work.Items, null, store, fake);
            Assert.False(secondRun.StoppedEarly);
            Assert.Equal(1, secondRun.SentCount);
            Assert.Equal(1, secondRun.SkippedCount);
            Assert.Equal("발송 완료 · 작성창 닫힘", work.Items[1].Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 자동_연속_발송은_모든_대상에게_같은_첨부와_SMS를_적용한다()
    {
        var root = Path.Combine(Path.GetTempPath(), "ontalk-personalized-attach", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = ProfileStore.Open(root, "attach-user");
            var file = Path.Combine(root, "안내문.txt");
            File.WriteAllText(file, "첨부 내용");
            var work = new PersonalizedWork
            {
                Items =
                [
                    new() { Person = new ContactPerson("1", "김예시", "주무관", ["예시시", "총무과"]), Body = "첫 번째 본문" },
                    new() { Person = new ContactPerson("2", "박예시", "팀장", ["예시시", "기획과"]), Body = "두 번째 본문" },
                ]
            };
            var fake = new FakeBatchAutomation(new Dictionary<string, IReadOnlyList<PersonRow>>
            {
                ["김예시"] = [Row("김예시", "주무관", "예시시 / 총무과")],
                ["박예시"] = [Row("박예시", "팀장", "예시시 / 기획과")],
            });

            var summary = await PersonalizedBatchDispatcher.RunAsync(work, work.Items, null, store, fake, attachments: [file], sms: true);

            Assert.False(summary.StoppedEarly);
            Assert.Equal(2, summary.SentCount);
            Assert.Equal([file, file], fake.AttachedPaths);
            Assert.Equal(2, fake.SmsOnCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 첨부를_붙이지_못하면_전송하지_않고_중단한다()
    {
        var root = Path.Combine(Path.GetTempPath(), "ontalk-personalized-attachfail", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = ProfileStore.Open(root, "attach-fail-user");
            var file = Path.Combine(root, "안내문.txt");
            File.WriteAllText(file, "첨부 내용");
            var work = new PersonalizedWork
            {
                Items = [new() { Person = new ContactPerson("1", "김예시", "주무관", ["예시시", "총무과"]), Body = "본문" }]
            };
            var fake = new FakeBatchAutomation(new Dictionary<string, IReadOnlyList<PersonRow>>
            {
                ["김예시"] = [Row("김예시", "주무관", "예시시 / 총무과")],
            })
            { AttachResult = ActionStatus.UiNotFound };

            var summary = await PersonalizedBatchDispatcher.RunAsync(work, work.Items, null, store, fake, attachments: [file]);

            Assert.True(summary.StoppedEarly);
            Assert.Equal(0, summary.SentCount);
            Assert.False(work.Items[0].Attempted);
            Assert.Equal("보류 · 첨부 실패", work.Items[0].Status);
            Assert.Empty(store.GetDispatches());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 첨부_점검에_걸리면_온톡을_건드리지_않고_멈춘다()
    {
        var work = new PersonalizedWork { Items = [new() { Person = new ContactPerson("1", "김예시", "주무관", ["예시시"]), Body = "본문" }] };
        var fake = new FakeBatchAutomation(new Dictionary<string, IReadOnlyList<PersonRow>>());
        var root = Path.Combine(Path.GetTempPath(), "ontalk-personalized-attachbad", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = ProfileStore.Open(root, "attach-bad-user");
            var summary = await PersonalizedBatchDispatcher.RunAsync(work, work.Items, null, store, fake,
                attachments: [Path.Combine(root, "없는파일.txt")]);
            Assert.True(summary.StoppedEarly);
            Assert.Empty(fake.AttachedPaths);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static PersonRow Row(string name, string title, string detail) =>
        new(name, title, [detail], new AccNode(AccNode.ListItem, name, "", 0, []));

    private sealed class FakeBatchAutomation(IReadOnlyDictionary<string, IReadOnlyList<PersonRow>> directory) : IPersonalizedAutomation
    {
        private ComposeState _state = new([], "", 0, 4000, false, false, default);
        private int _nextHwnd = 100;
        public int ClosedSearchWindowCount { get; private set; }
        public SendOutcome SendResult { get; set; } = new(SendStatus.WindowClosed, "");

        public (ActionStatus Status, IntPtr ComposeWindow) EnsureEmptyNewComposeWindow()
        {
            _state = new([], "", 0, 4000, false, false, default);
            return (ActionStatus.Ok, new IntPtr(++_nextHwnd));
        }

        public ComposeState? ReadCompose(IntPtr compose) => _state;

        public SearchOutcome SearchRecipient(IntPtr compose, string name) =>
            new(ActionStatus.Ok, new IntPtr(900), directory.GetValueOrDefault(name, []));

        public ActionStatus AddRecipient(IntPtr compose, IntPtr search, PersonRow person)
        {
            _state = _state with { Recipients = [.. _state.Recipients, person.Name] };
            return ActionStatus.Ok;
        }

        public void CloseUserSearchWindow(IntPtr search) => ClosedSearchWindowCount++;

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

        public List<string> AttachedPaths { get; } = [];
        public ActionStatus AttachResult { get; set; } = ActionStatus.Ok;
        public int SmsOnCount { get; private set; }

        public ActionStatus AttachFile(IntPtr compose, string path)
        {
            if (AttachResult != ActionStatus.Ok) return AttachResult;
            AttachedPaths.Add(path);
            _state = _state with { Attachments = [.. _state.Attachments, Path.GetFileName(path)] };
            return ActionStatus.Ok;
        }

        public ActionStatus SetSms(IntPtr compose, bool on)
        {
            if (on) SmsOnCount++;
            _state = _state with { SmsChecked = on };
            return ActionStatus.Ok;
        }

        public SendOutcome Send(IntPtr compose, ExpectedCompose expected)
        {
            if (ComposeActions.Difference(_state, expected) is { } diff)
            {
                return new SendOutcome(SendStatus.Mismatch, diff);
            }
            return SendResult;
        }
    }
}
