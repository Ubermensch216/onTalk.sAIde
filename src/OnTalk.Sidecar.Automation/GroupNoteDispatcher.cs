using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

public enum GroupNoteResult
{
    /// <summary>수신자·본문·예약을 온톡 작성 창에 채우고 검증까지 마쳤다. 전송은 하지 않았다.</summary>
    Prepared,
    /// <summary>전송 단추를 눌렀고 작성 창이 닫혔다.</summary>
    Sent,
    /// <summary>전송을 시도하지 않고 멈췄다. 같은 내용으로 다시 시도해도 된다.</summary>
    Stopped,
    /// <summary>전송 단추를 눌렀지만 결과를 확정하지 못했다. 다시 보내면 안 된다.</summary>
    Uncertain,
}

/// <summary>
/// <param name="Attempted">전송 단추를 누르기 직전까지 진행해 발송 기록을 남겼는지. true면 같은 쪽지를 다시 보내지 않는다.</param>
/// </summary>
public sealed record GroupNoteOutcome(GroupNoteResult Result, bool Attempted, int AddedCount, string Message);

/// <summary>
/// 조직도에서 고른 여러 직원에게 같은 쪽지 한 통을 보낸다.
/// 빈 '새 쪽지' 창 확보 → 직원마다 이름 검색·직급·소속 대조 후 수신자 추가 → 본문 반영 → (선택) 예약 → 전송 직전 검증 → (선택) 전송.
/// 같은 쪽지를 모두가 받으므로 한 사람이라도 확정하지 못하면 일부에게만 보내지 않고 멈춘다.
/// </summary>
public static class GroupNoteDispatcher
{
    public static Task<GroupNoteOutcome> RunAsync(
        IReadOnlyList<ContactPerson> people,
        string body,
        DateTime? reservation,
        bool send,
        IDispatchLog log,
        IPersonalizedAutomation automation,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? attachments = null,
        bool sms = false)
    {
        attachments ??= [];
        if (NoteAttachments.Validate(attachments) is { } attachProblem)
            return Task.FromResult(new GroupNoteOutcome(GroupNoteResult.Stopped, false, 0, attachProblem));
        if (people.Count == 0)
            return Task.FromResult(new GroupNoteOutcome(GroupNoteResult.Stopped, false, 0, "받는 사람이 없습니다."));
        if (people.GroupBy(p => p.DisplayIdentity).Any(g => g.Count() > 1))
            return Task.FromResult(new GroupNoteOutcome(GroupNoteResult.Stopped, false, 0,
                "이름·직급·소속이 같은 대상이 여러 번 들어 있습니다. 중복 대상을 제외하세요."));
        var final = body.Replace("\r\n", "\n");
        if (string.IsNullOrWhiteSpace(final) || NoteBytes.Count(final) > NoteBytes.Limit)
            return Task.FromResult(new GroupNoteOutcome(GroupNoteResult.Stopped, false, 0, "본문이 비어 있거나 4000바이트를 넘습니다."));
        if (reservation is { } r) reservation = new DateTime(r.Year, r.Month, r.Day, r.Hour, r.Minute, 0);
        return Task.Run(() => Execute(people, final, reservation, send, log, automation, onProgress, cancellationToken, attachments, sms));
    }

    private static GroupNoteOutcome Execute(
        IReadOnlyList<ContactPerson> people,
        string body,
        DateTime? reservation,
        bool send,
        IDispatchLog log,
        IPersonalizedAutomation automation,
        Action<int, int, string>? onProgress,
        CancellationToken cancellationToken,
        IReadOnlyList<string> attachments,
        bool sms)
    {
        var total = people.Count;
        GroupNoteOutcome Stop(int added, string message) => new(GroupNoteResult.Stopped, false, added, message);

        onProgress?.Invoke(0, total, "온톡에서 빈 '새 쪽지' 창을 준비하는 중...");
        var (ensureStatus, hwnd) = automation.EnsureEmptyNewComposeWindow();
        if (ensureStatus != ActionStatus.Ok || hwnd == IntPtr.Zero)
            return Stop(0, "온톡에서 빈 '새 쪽지' 창을 열지 못했습니다. 온톡 본창이 열려 있는지 확인하세요.");
        var initial = automation.ReadCompose(hwnd);
        if (initial is null || !ComposeActions.IsEmptyNewCompose(initial) || initial.SmsChecked)
            return Stop(0, "준비된 쪽지 창이 빈 새 쪽지 상태가 아니라서 멈췄습니다.");

        var names = new List<string>();
        for (var index = 0; index < total; index++)
        {
            var person = people[index];
            var prefix = $"[{index + 1}/{total}] {person.Name}";
            if (cancellationToken.IsCancellationRequested)
                return Stop(names.Count, $"사용자 요청으로 멈췄습니다. 온톡 쪽지 창에 {names.Count}명까지 추가되어 있고 보내지 않았습니다.");
            onProgress?.Invoke(index, total, $"{prefix} · 수신자 확인 중...");

            var found = automation.SearchRecipient(hwnd, person.Name);
            if (found.Status != ActionStatus.Ok)
            {
                if (found.SearchWindow != IntPtr.Zero) automation.CloseUserSearchWindow(found.SearchWindow);
                return Stop(names.Count, $"{prefix}: 수신자 검색 중 멈췄습니다({ExplainAction(found.Status)}). 보내지 않았습니다.");
            }
            var matches = found.Rows.Where(row => PreparedRecipientMatch.Matches(person, row.ToRecipient())).ToList();
            if (matches.Count != 1)
            {
                automation.CloseUserSearchWindow(found.SearchWindow);
                return Stop(names.Count, matches.Count == 0
                    ? $"{prefix}: 이름·직급·소속이 일치하는 사람을 찾지 못해 멈췄습니다. 이 대상을 제외하거나 온톡 창에서 직접 추가하세요."
                    : $"{prefix}: 일치하는 사람이 {matches.Count}명이라 확정하지 못해 멈췄습니다. 온톡 창에서 직접 고르세요.");
            }
            var addStatus = automation.AddRecipient(hwnd, found.SearchWindow, matches[0]);
            if (addStatus != ActionStatus.Ok)
                return Stop(names.Count, $"{prefix}: 수신자를 추가하지 못해 멈췄습니다({ExplainAction(addStatus)}). 보내지 않았습니다.");
            names.Add(matches[0].Name);
        }

        onProgress?.Invoke(total, total, "본문을 반영하는 중...");
        var apply = automation.ApplyBody(hwnd, body, "");
        if (apply.Status != ApplyStatus.Applied)
            return Stop(names.Count, $"본문을 반영하지 못해 멈췄습니다({ExplainApply(apply.Status)}). 보내지 않았습니다.");

        // 파일은 온톡의 파일 선택창으로 직접 붙이고, 붙일 때마다 첨부 목록에 그 이름이 늘었는지 확인한다.
        var attachedNames = new List<string>();
        for (var i = 0; i < attachments.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                return Stop(names.Count, $"사용자 요청으로 멈췄습니다. 온톡 쪽지 창에 첨부 {attachedNames.Count}개까지 붙어 있고 보내지 않았습니다.");
            var file = Path.GetFileName(attachments[i]);
            onProgress?.Invoke(total, total, $"첨부 {i + 1}/{attachments.Count} · {file} 붙이는 중...");
            var attach = automation.AttachFile(hwnd, attachments[i]);
            if (attach != ActionStatus.Ok)
                return Stop(names.Count, $"첨부 ‘{file}’을(를) 붙이지 못해 멈췄습니다({ExplainAction(attach)}). 온톡이 용량·개수·형식을 거절했을 수 있습니다. 보내지 않았습니다.");
            attachedNames.Add(file);
        }

        if (sms)
        {
            onProgress?.Invoke(total, total, "SMS 발송을 선택하는 중...");
            var smsStatus = automation.SetSms(hwnd, true);
            if (smsStatus != ActionStatus.Ok)
                return Stop(names.Count, $"SMS 발송을 선택하지 못해 멈췄습니다({ExplainAction(smsStatus)}). 보내지 않았습니다.");
        }

        if (reservation is { } when)
        {
            var reserve = automation.SetReservation(hwnd, when);
            if (reserve != ActionStatus.Ok)
                return Stop(names.Count, $"예약 시각({when:yyyy-MM-dd HH:mm})을 설정하지 못해 멈췄습니다({ExplainAction(reserve)}). 보내지 않았습니다.");
        }

        var state = automation.ReadCompose(hwnd);
        var expected = new ExpectedCompose(names, body, reservation, sms, attachedNames);
        var diff = state is null ? "온톡 작성 창을 읽지 못했습니다." : ComposeActions.Difference(state, expected);
        if (state is null || diff is not null)
            return Stop(names.Count, $"전송 전 검증이 맞지 않아 멈췄습니다({diff}). 보내지 않았습니다.");

        if (!send)
            return new GroupNoteOutcome(GroupNoteResult.Prepared, false, names.Count,
                $"온톡 쪽지 창에 {names.Count}명과 본문{Extras(attachedNames.Count, sms)}{(reservation is { } at ? $"·예약({at:MM-dd HH:mm})" : "")}을 채웠습니다. 온톡 창에서 확인한 뒤 직접 전송하세요.");
        if (cancellationToken.IsCancellationRequested)
            return Stop(names.Count, "사용자 요청으로 전송 직전에 멈췄습니다. 온톡 쪽지 창은 채워진 채로 남아 있습니다.");

        // 전송 단추를 누르기 전에 기록해 두어, 결과가 불확실해도 다시 보내지 않게 한다.
        var dispatchId = log.BeginDispatch(state.Recipients, body, state.Reservation, DateTimeOffset.Now);
        onProgress?.Invoke(total, total, "전송하는 중...");
        var outcome = automation.Send(hwnd, expected);
        switch (outcome.Status)
        {
            case SendStatus.WindowClosed:
                log.FinishDispatch(dispatchId, DispatchStatus.WindowClosed, "");
                return new GroupNoteOutcome(GroupNoteResult.Sent, true, names.Count, reservation is { } at
                    ? $"{names.Count}명에게 {at:yyyy-MM-dd HH:mm} 예약 발송을 설정했습니다{Extras(attachedNames.Count, sms, " (", ")")}."
                    : $"{names.Count}명에게 쪽지를 보냈습니다{Extras(attachedNames.Count, sms, " (", ")")}.");
            case SendStatus.StillOpen:
                log.FinishDispatch(dispatchId, DispatchStatus.StillOpen, outcome.Detail);
                return new GroupNoteOutcome(GroupNoteResult.Uncertain, true, names.Count,
                    "전송 단추를 눌렀지만 작성 창이 닫히지 않았습니다. 다시 보내지 말고 온톡 보낸 쪽지함을 확인하세요.");
            default:
                log.FinishDispatch(dispatchId, DispatchStatus.NotSent, outcome.Detail);
                return new GroupNoteOutcome(GroupNoteResult.Uncertain, true, names.Count,
                    $"전송하지 못했습니다({outcome.Detail}). 온톡 쪽지 창과 보낸 쪽지함을 확인하세요.");
        }
    }

    // 첨부·SMS를 쓴 경우에만 결과 문장에 덧붙인다.
    private static string Extras(int attachments, bool sms, string open = "·", string close = "")
    {
        var parts = new List<string>();
        if (attachments > 0) parts.Add($"첨부 {attachments}개");
        if (sms) parts.Add("SMS 발송");
        return parts.Count == 0 ? "" : open + string.Join("·", parts) + close;
    }

    private static string ExplainAction(ActionStatus status) => status switch
    {
        ActionStatus.NotCompose => "온톡 작성 창을 찾지 못했습니다",
        ActionStatus.UiNotFound => "온톡 화면 요소를 찾지 못했습니다",
        ActionStatus.InputBlocked => "온톡 입력창 포커스가 가려졌거나 막혔습니다",
        _ => "조작 뒤 다시 읽은 값이 기대와 다릅니다",
    };

    private static string ExplainApply(ApplyStatus status) => status switch
    {
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
