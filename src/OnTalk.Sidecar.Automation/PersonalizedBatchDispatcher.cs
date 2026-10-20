using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

public interface IPersonalizedAutomation
{
    (ActionStatus Status, IntPtr ComposeWindow) EnsureEmptyNewComposeWindow();
    ComposeState? ReadCompose(IntPtr compose);
    SearchOutcome SearchRecipient(IntPtr compose, string name);
    ActionStatus AddRecipient(IntPtr compose, IntPtr search, PersonRow person);
    void CloseUserSearchWindow(IntPtr search);
    /// <summary>이미 열려 있는 빈 새 쪽지 창(없으면 Zero). 검색용으로 새로 연 창만 나중에 닫기 위해 먼저 확인한다.</summary>
    IntPtr FindEmptyNewComposeWindow() => IntPtr.Zero;
    /// <summary>검색용으로 연 빈 새 쪽지 창을 닫는다. 내용이 들어 있으면 닫지 않는다.</summary>
    void CloseEmptyComposeWindow(IntPtr compose) { }
    ApplyResult ApplyBody(IntPtr compose, string body, string? expectedCurrentBody);
    ActionStatus SetReservation(IntPtr compose, DateTime when);
    SendOutcome Send(IntPtr compose, ExpectedCompose expected);
    /// <summary>작성 창에 파일 하나를 붙이고 첨부 목록에 늘었는지 확인한다.</summary>
    ActionStatus AttachFile(IntPtr compose, string path) => ActionStatus.UiNotFound;
    /// <summary>"SMS 발송" 확인란을 맞춘다.</summary>
    ActionStatus SetSms(IntPtr compose, bool on) => ActionStatus.UiNotFound;
}

public sealed class OnTalkPersonalizedAutomation : IPersonalizedAutomation
{
    public static readonly OnTalkPersonalizedAutomation Instance = new();

    public (ActionStatus Status, IntPtr ComposeWindow) EnsureEmptyNewComposeWindow() =>
        ComposeActions.EnsureEmptyNewComposeWindow();

    public ComposeState? ReadCompose(IntPtr compose) =>
        ComposeWriter.Read(compose);

    public SearchOutcome SearchRecipient(IntPtr compose, string name) =>
        ComposeActions.SearchRecipient(compose, name);

    public ActionStatus AddRecipient(IntPtr compose, IntPtr search, PersonRow person) =>
        ComposeActions.AddRecipient(compose, search, person);

    public void CloseUserSearchWindow(IntPtr search) =>
        ComposeActions.CloseUserSearchWindow(search);

    public IntPtr FindEmptyNewComposeWindow() =>
        ComposeActions.FindEmptyNewComposeWindow();

    public void CloseEmptyComposeWindow(IntPtr compose) =>
        ComposeActions.CloseEmptyComposeWindow(compose);

    public ApplyResult ApplyBody(IntPtr compose, string body, string? expectedCurrentBody) =>
        ComposeWriter.ApplyBody(compose, body, expectedCurrentBody);

    public ActionStatus SetReservation(IntPtr compose, DateTime when) =>
        ComposeActions.SetReservation(compose, when);

    public SendOutcome Send(IntPtr compose, ExpectedCompose expected) =>
        ComposeActions.Send(compose, expected);

    public ActionStatus AttachFile(IntPtr compose, string path) =>
        AttachActions.Attach(compose, path);

    public ActionStatus SetSms(IntPtr compose, bool on) =>
        ComposeActions.SetSms(compose, on);
}

public sealed record PersonalizedBatchSummary(
    int TotalCandidates,
    int SentCount,
    int SkippedCount,
    bool StoppedEarly,
    string Message);

/// <summary>
/// 개인별 쪽지 목록의 미발송 대상을 차례로 순회하며
/// 빈 '새 쪽지' 창 확보 → 수신자 1명 검색·직급·소속 대조 → 개인별 본문 반영 → (선택) 예약 설정 → 검증 후 전송까지
/// 자동으로 연속 수행한다.
/// </summary>
public static class PersonalizedBatchDispatcher
{
    private sealed record StepOutcome(
        bool Sent,
        bool Skipped,
        bool StopBatch,
        string ProgressText,
        string StopReason);

    public static async Task<PersonalizedBatchSummary> RunAsync(
        PersonalizedWork work,
        IReadOnlyList<PersonalizedWorkItem> candidates,
        DateTime? reservation,
        IPersonalizedDispatchStore store,
        IPersonalizedAutomation automation,
        Action<PersonalizedWorkItem, int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? attachments = null,
        bool sms = false)
    {
        attachments ??= [];
        if (candidates.Count == 0)
        {
            return new PersonalizedBatchSummary(0, 0, 0, false, "발송할 준비된 미발송 대상이 없습니다.");
        }

        if (NoteAttachments.Validate(attachments) is { } attachProblem)
        {
            return new PersonalizedBatchSummary(candidates.Count, 0, 0, true, attachProblem);
        }

        if (candidates.GroupBy(c => c.Person.DisplayIdentity).Any(g => g.Count() > 1))
        {
            return new PersonalizedBatchSummary(
                candidates.Count,
                0,
                0,
                true,
                "표시 정보(이름·직급·소속)가 같은 대상이 포함되어 있어 자동 연속 발송을 시작하지 않았습니다.");
        }

        if (reservation is { } r)
        {
            reservation = new DateTime(r.Year, r.Month, r.Day, r.Hour, r.Minute, 0);
        }

        var sent = 0;
        var skipped = 0;

        for (var index = 0; index < candidates.Count; index++)
        {
            var item = candidates[index];
            var order = index + 1;

            if (cancellationToken.IsCancellationRequested)
            {
                return new PersonalizedBatchSummary(
                    candidates.Count,
                    sent,
                    skipped,
                    true,
                    $"사용자 요청으로 연속 발송을 중단했습니다. (발송 완료 {sent}명 · 보류 {skipped}명 · 미진행 {candidates.Count - index}명)");
            }

            if (item.Attempted)
            {
                skipped++;
                onProgress?.Invoke(item, order, candidates.Count, $"[{order}/{candidates.Count}] {item.Name}: 이미 발송을 시도한 대상이라 건너뜁니다.");
                continue;
            }

            if (!NotePersonalization.IsValidBody(item.Body))
            {
                item.Status = "보류 · 본문 확인 필요";
                store.SavePersonalizedWork(work);
                skipped++;
                onProgress?.Invoke(item, order, candidates.Count, $"[{order}/{candidates.Count}] {item.Name}: 본문이 비어 있거나 미완성 변수·바이트 초과가 있어 보류했습니다.");
                continue;
            }

            onProgress?.Invoke(item, order, candidates.Count, $"[{order}/{candidates.Count}] {item.Name} ({item.Organization}) · 자동 발송 중...");

            var step = await Task.Run(() => ExecuteOne(
                work,
                item,
                order,
                candidates.Count,
                reservation,
                store,
                automation,
                cancellationToken,
                attachments,
                sms));

            if (step.Sent)
            {
                sent++;
            }
            if (step.Skipped)
            {
                skipped++;
            }

            onProgress?.Invoke(item, order, candidates.Count, step.ProgressText);

            if (step.StopBatch)
            {
                return new PersonalizedBatchSummary(
                    candidates.Count,
                    sent,
                    skipped,
                    true,
                    $"{step.StopReason} (완료 {sent}명 · 보류 {skipped}명 · 남은 인원 {candidates.Count - order}명)");
            }
        }

        var summary = skipped == 0
            ? $"총 {candidates.Count}명 중 {sent}명 자동 발송을 완료했습니다."
            : $"총 {candidates.Count}명 중 {sent}명 발송 완료, {skipped}명 보류했습니다. 보류된 대상의 처리 상태를 확인하세요.";
        return new PersonalizedBatchSummary(candidates.Count, sent, skipped, false, summary);
    }

    private static StepOutcome ExecuteOne(
        PersonalizedWork work,
        PersonalizedWorkItem item,
        int order,
        int total,
        DateTime? reservation,
        IPersonalizedDispatchStore store,
        IPersonalizedAutomation automation,
        CancellationToken cancellationToken,
        IReadOnlyList<string> attachments,
        bool sms)
    {
        var prefix = $"[{order}/{total}] {item.Name}";

        var (ensureStatus, hwnd) = automation.EnsureEmptyNewComposeWindow();
        if (ensureStatus != ActionStatus.Ok || hwnd == IntPtr.Zero)
        {
            return new StepOutcome(
                false,
                false,
                true,
                $"{prefix}: 빈 '새 쪽지' 창 확보 실패",
                $"{prefix}: 온톡에서 빈 '새 쪽지' 창을 열지 못해 연속 발송을 중단했습니다. 온톡 본창이 열려 있는지 확인하세요.");
        }

        var initial = automation.ReadCompose(hwnd);
        if (initial is null || !ComposeActions.IsEmptyNewCompose(initial) || initial.SmsChecked)
        {
            return new StepOutcome(
                false,
                false,
                true,
                $"{prefix}: 새 쪽지 창이 비어 있지 않음",
                $"{prefix}: 준비된 쪽지 창이 빈 새 쪽지 상태가 아니라서 안전을 위해 중단했습니다.");
        }

        var found = automation.SearchRecipient(hwnd, item.Name);
        if (found.Status != ActionStatus.Ok)
        {
            if (found.SearchWindow != IntPtr.Zero)
            {
                automation.CloseUserSearchWindow(found.SearchWindow);
            }
            return new StepOutcome(
                false,
                false,
                true,
                $"{prefix}: 수신자 검색 실패",
                $"{prefix}: 온톡 '받는 사람 추가' 검색 중 멈췄습니다({ExplainAction(found.Status)}).");
        }

        var matches = found.Rows.Where(r => PreparedRecipientMatch.Matches(item.Person, r.ToRecipient())).ToList();
        if (matches.Count != 1)
        {
            // 아직 수신자 추가(확인)를 누르기 전이므로 검색 창만 닫으면 빈 새 쪽지 창은 그대로 다음 사람에게 재사용할 수 있다.
            automation.CloseUserSearchWindow(found.SearchWindow);
            item.Status = matches.Count == 0
                ? "보류 · 일치 수신자 없음"
                : $"보류 · 동명이인 {matches.Count}명 확인 필요";
            store.SavePersonalizedWork(work);
            return new StepOutcome(
                false,
                true,
                false,
                $"{prefix}: {item.Status}",
                "");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            automation.CloseUserSearchWindow(found.SearchWindow);
            return new StepOutcome(
                false,
                false,
                true,
                $"{prefix}: 사용자 중단 요청",
                "사용자 요청으로 연속 발송을 중단했습니다.");
        }

        var addStatus = automation.AddRecipient(hwnd, found.SearchWindow, matches[0]);
        if (addStatus != ActionStatus.Ok)
        {
            item.Status = "보류 · 수신자 추가 실패";
            store.SavePersonalizedWork(work);
            return new StepOutcome(
                false,
                false,
                true,
                $"{prefix}: {item.Status}",
                $"{prefix}: 수신자를 추가한 뒤 온톡 창에서 확인되지 않아 중단했습니다({ExplainAction(addStatus)}). 열려 있는 온톡 창을 확인하세요.");
        }

        var apply = automation.ApplyBody(hwnd, item.Body, "");
        if (apply.Status != ApplyStatus.Applied)
        {
            item.Status = "보류 · 본문 반영 실패";
            store.SavePersonalizedWork(work);
            return new StepOutcome(
                false,
                false,
                true,
                $"{prefix}: {item.Status}",
                $"{prefix}: 본문 반영에 실패해 중단했습니다({ExplainApply(apply.Status)}). 열려 있는 온톡 창을 확인하세요.");
        }

        // 첨부는 온톡의 파일 선택창으로 붙이고, 붙일 때마다 첨부 목록에 이름이 늘었는지 확인한다.
        var attachedNames = new List<string>();
        foreach (var path in attachments)
        {
            var file = Path.GetFileName(path);
            var attach = automation.AttachFile(hwnd, path);
            if (attach != ActionStatus.Ok)
            {
                item.Status = "보류 · 첨부 실패";
                store.SavePersonalizedWork(work);
                return new StepOutcome(
                    false,
                    false,
                    true,
                    $"{prefix}: {item.Status}",
                    $"{prefix}: 첨부 ‘{file}’을(를) 붙이지 못해 중단했습니다({ExplainAction(attach)}). 온톡이 용량·개수·형식을 거절했을 수 있습니다. 열려 있는 온톡 창을 확인하세요.");
            }
            attachedNames.Add(file);
        }

        if (sms)
        {
            var smsStatus = automation.SetSms(hwnd, true);
            if (smsStatus != ActionStatus.Ok)
            {
                item.Status = "보류 · SMS 선택 실패";
                store.SavePersonalizedWork(work);
                return new StepOutcome(
                    false,
                    false,
                    true,
                    $"{prefix}: {item.Status}",
                    $"{prefix}: SMS 발송을 선택하지 못해 중단했습니다({ExplainAction(smsStatus)}). 열려 있는 온톡 창을 확인하세요.");
            }
        }

        if (reservation is { } when)
        {
            var resStatus = automation.SetReservation(hwnd, when);
            if (resStatus != ActionStatus.Ok)
            {
                item.Status = "보류 · 예약 설정 실패";
                store.SavePersonalizedWork(work);
                return new StepOutcome(
                    false,
                    false,
                    true,
                    $"{prefix}: {item.Status}",
                    $"{prefix}: 예약 시각({when:yyyy-MM-dd HH:mm}) 설정에 실패해 중단했습니다({ExplainAction(resStatus)}). 열려 있는 온톡 창을 확인하세요.");
            }
        }

        var state = automation.ReadCompose(hwnd);
        var finalBody = item.Body.Replace("\r\n", "\n");
        var expected = new ExpectedCompose([item.Name], finalBody, reservation, sms, attachedNames);
        var diff = state is null ? "온톡 작성 창을 읽지 못했습니다." : ComposeActions.Difference(state, expected);
        if (state is null || diff is not null)
        {
            item.Status = "보류 · 전송 전 검증 불일치";
            store.SavePersonalizedWork(work);
            return new StepOutcome(
                false,
                false,
                true,
                $"{prefix}: {item.Status}",
                $"{prefix}: 전송 직전 검증이 맞지 않아 보내지 않고 중단했습니다({diff}).");
        }

        // 전송 단추를 누르기 전에 발송 시도를 먼저 기록해 중복 발송을 방지한다.
        item.DispatchRevision++;
        item.DispatchId = -1;
        item.Body = finalBody;
        item.Status = "전송 준비 기록 · 결과 확인 필요";
        store.SavePersonalizedWork(work);

        var dispatchId = store.BeginDispatch(state.Recipients, finalBody, state.Reservation, DateTimeOffset.Now);
        item.DispatchRevision++;
        item.DispatchId = dispatchId;
        item.Status = "전송 시도 · 결과 확인 필요";
        store.SavePersonalizedWork(work);

        var outcome = automation.Send(hwnd, expected);
        item.DispatchRevision++;
        switch (outcome.Status)
        {
            case SendStatus.WindowClosed:
                store.FinishDispatch(dispatchId, DispatchStatus.WindowClosed, "");
                item.Status = reservation is null
                    ? "발송 완료 · 작성창 닫힘"
                    : $"예약 완료({reservation:MM-dd HH:mm}) · 작성창 닫힘";
                store.SavePersonalizedWork(work);
                return new StepOutcome(true, false, false, $"{prefix}: {item.Status}", "");

            case SendStatus.StillOpen:
                store.FinishDispatch(dispatchId, DispatchStatus.StillOpen, outcome.Detail);
                item.Status = "결과 불명 · 직접 확인 필요";
                store.SavePersonalizedWork(work);
                return new StepOutcome(
                    false,
                    false,
                    true,
                    $"{prefix}: {item.Status}",
                    $"{prefix}: 전송 단추를 눌렀지만 작성 창이 닫히지 않아 연속 발송을 중단했습니다. 온톡 보낸 쪽지함을 확인하세요.");

            default:
                store.FinishDispatch(dispatchId, DispatchStatus.NotSent, outcome.Detail);
                item.Status = "미전송 · 직접 확인 필요";
                store.SavePersonalizedWork(work);
                return new StepOutcome(
                    false,
                    false,
                    true,
                    $"{prefix}: {item.Status}",
                    $"{prefix}: 전송하지 못해 연속 발송을 중단했습니다({outcome.Detail}).");
        }
    }

    private static string ExplainAction(ActionStatus status) => status switch
    {
        ActionStatus.Ok => "완료",
        ActionStatus.NotCompose => "온톡 작성 창을 찾지 못했습니다",
        ActionStatus.UiNotFound => "온톡 화면 요소를 찾지 못했습니다",
        ActionStatus.InputBlocked => "온톡 입력창 포커스가 가려졌거나 막혔습니다",
        _ => "조작 뒤 다시 읽은 값이 기대와 다릅니다",
    };

    private static string ExplainApply(ApplyStatus status) => status switch
    {
        ApplyStatus.Applied => "완료",
        ApplyStatus.NotCompose => "온톡 작성 창을 찾지 못했습니다",
        ApplyStatus.DialogOpen => "온톡 창에 대화상자가 열려 있습니다",
        ApplyStatus.Conflict => "온톡 창에 이미 다른 본문이 들어 있습니다",
        ApplyStatus.TooLong => "바이트 한도를 초과했습니다",
        ApplyStatus.Covered => "온톡 창이 다른 창에 가려져 있습니다",
        ApplyStatus.FocusFailed => "온톡 입력창을 앞으로 가져오지 못했습니다",
        ApplyStatus.ModifierDown => "키보드 조합 키가 눌려 있습니다",
        ApplyStatus.ClipboardBusy => "클립보드를 사용할 수 없습니다",
        _ => "붙여넣은 본문이 기대와 다릅니다",
    };
}
