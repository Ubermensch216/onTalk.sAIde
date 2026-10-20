using System.Windows;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.App;

/// <summary>"받는 사람 찾기" 결과 한 줄.</summary>
public sealed class CandidateItem(PersonRow row)
{
    public PersonRow Row { get; } = row;
    public string Headline { get; } = $"{row.Name} · {row.Title}";
    public string DetailLine { get; } = string.Join(" · ", row.Details);
}

/// <summary>그룹 선택 상자의 한 줄.</summary>
public sealed class GroupItem(RecipientGroup group)
{
    public RecipientGroup Group { get; } = group;
    public string Label { get; } = $"{group.Name} ({group.Members.Count}명)";
}

// 수신자 추가, 예약 설정, 전송. 온톡의 단추를 누르는 조작이다.
public partial class ComposeWindow
{
    // 이 창에서 사이드카가 직접 골라 넣은 수신자. 넣을 때 본 소속·부서를 전송 확인 창에 보여 준다.
    private readonly List<Recipient> _picked = [];
    private IntPtr _searchWindow;

    private void InitializeDispatchControls()
    {
        ReserveHour.ItemsSource = Enumerable.Range(0, 24).ToList();
        ReserveMinute.ItemsSource = Enumerable.Range(0, 60).ToList();
        var next = DateTime.Now.AddHours(1);
        ReserveDate.SelectedDate = next.Date;
        ReserveHour.SelectedItem = next.Hour;
        ReserveMinute.SelectedItem = 0;
        ReloadGroups();
    }

    private void EnableControls()
    {
        var ready = !_closed && !_busy && _target != IntPtr.Zero && _state?.DialogOpen == false;
        TargetBox.IsEnabled = !_busy && _lockedTarget is null;
        PersonalizeButton.IsEnabled = !_busy;
        PreparedContacts.IsEnabled = !_busy;
        AddPreparedButton.IsEnabled = !_closed && !_busy && _activeItem is null && _preparedPeople.Count > 0;
        ApplyButton.IsEnabled = ready;
        SendButton.IsEnabled = ready;
        OnTalkControls.IsEnabled = ready;
        UnreserveButton.IsEnabled = _state?.Reservation is not null;
    }

    private void RefreshHint()
    {
        if (_state is not { } state)
        {
            return;
        }
        var names = state.Recipients.Count > 0 ? string.Join(", ", state.Recipients) : "없음";
        TargetHint.Text = $"수신 {state.Recipients.Count}명: {names}" +
                          (state.Reservation is { } r ? $" · 예약 {r:yyyy-MM-dd HH:mm}" : " · 즉시 전송") +
                          (state.SmsChecked ? " · SMS 발송 선택됨" : "") + _conflictNote;
    }

    // 온톡 조작은 1~5초 걸리므로 화면이 멈추지 않게 따로 돌린다. 도는 동안 다른 단추는 막는다.
    private async Task<T> RunOnTalk<T>(string doing, Func<T> action)
    {
        _busy = true;
        EnableControls();
        ControlStatus.Text = doing;
        try
        {
            return await Task.Run(action);
        }
        finally
        {
            _busy = false;
            EnableControls();
        }
    }

    private static string Explain(ActionStatus status) => status switch
    {
        ActionStatus.Ok => "완료",
        ActionStatus.NotCompose => "온톡 작성 창을 찾지 못했습니다.",
        ActionStatus.UiNotFound => "온톡 화면에서 눌러야 할 곳을 찾지 못했습니다. 온톡 화면 구성이 달라졌을 수 있습니다.",
        ActionStatus.InputBlocked => "온톡 입력창에 글자를 넣지 못했습니다. 창이 가려졌거나 키보드를 누르고 있었습니다.",
        _ => "조작 뒤 다시 읽은 결과가 기대와 달라 더 진행하지 않았습니다. 온톡 창을 직접 확인하세요.",
    };

    private async void OnFindRecipient(object sender, RoutedEventArgs e)
    {
        var name = RecipientName.Text.Trim();
        if (name.Length == 0)
        {
            ControlStatus.Text = "찾을 이름을 적어 주세요.";
            return;
        }
        var hwnd = _target;
        var found = await RunOnTalk("온톡 '받는 사람 추가' 창에서 찾는 중", () => ComposeActions.SearchRecipient(hwnd, name));
        _searchWindow = found.SearchWindow;
        CandidateList.ItemsSource = found.Rows.Select(r => new CandidateItem(r)).ToList();
        CandidatePanel.Visibility = found.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ControlStatus.Text = found.Status != ActionStatus.Ok
            ? Explain(found.Status)
            : found.Rows.Count switch
            {
                0 => $"'{name}'(이)라는 이름을 찾지 못했습니다. 온톡 검색 창은 열어 두었습니다.",
                1 => "한 명을 찾았습니다. 소속을 확인하고 추가하세요.",
                var n => $"같은 이름이 {n}명입니다. 소속·부서를 보고 맞는 사람을 고르세요.",
            };
        if (found.Rows.Count == 1)
        {
            CandidateList.SelectedIndex = 0;
        }
    }

    private async void OnAddRecipient(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not CandidateItem item)
        {
            ControlStatus.Text = "추가할 사람을 목록에서 고르세요.";
            return;
        }
        var (hwnd, search) = (_target, _searchWindow);
        var status = await RunOnTalk("온톡에 수신자를 추가하는 중", () => ComposeActions.AddRecipient(hwnd, search, item.Row));
        if (status == ActionStatus.Ok)
        {
            _picked.Add(item.Row.ToRecipient());
            CandidatePanel.Visibility = Visibility.Collapsed;
            RecipientName.Clear();
            ControlStatus.Text = $"{item.Headline}을(를) 수신자로 추가했고 온톡 창에서 확인했습니다.";
        }
        else
        {
            ControlStatus.Text = "수신자를 추가하지 못했습니다. " + Explain(status);
        }
    }

    private void ReloadGroups() =>
        GroupBox.ItemsSource = _store.GetGroups().Select(g => new GroupItem(g)).ToList();

    private async void OnAddGroup(object sender, RoutedEventArgs e)
    {
        if (GroupBox.SelectedItem is not GroupItem { Group: var group })
        {
            ControlStatus.Text = "넣을 그룹을 고르세요.";
            return;
        }
        var hwnd = _target;
        var (added, failed, status) = await RunOnTalk($"그룹 '{group.Name}'의 {group.Members.Count}명을 온톡에 넣는 중",
            () => ComposeActions.AddRecipients(hwnd, group.Members));
        _picked.AddRange(group.Members.Take(added));
        ControlStatus.Text = failed is null
            ? $"그룹 '{group.Name}'의 {added}명을 모두 넣었고 온톡 창에서 확인했습니다."
            : $"{group.Members.Count}명 중 {added}명을 넣고 '{failed.Name}'에서 멈췄습니다. " +
              (status == ActionStatus.Mismatch
                  ? "저장할 때와 직급·소속이 같은 사람을 찾지 못했습니다. 인사이동이 있었다면 직접 찾아 넣고 그룹을 다시 저장하세요."
                  : Explain(status));
    }

    private void OnSaveGroup(object sender, RoutedEventArgs e)
    {
        var name = GroupBox.Text.Trim();
        // 표시용 꼬리표("이름 (3명)")가 붙은 채로 저장하지 않는다.
        if (GroupBox.SelectedItem is GroupItem selected && name == selected.Label)
        {
            name = selected.Group.Name;
        }
        if (name.Length == 0 || _picked.Count == 0)
        {
            ControlStatus.Text = "그룹 칸에 이름을 적고, 이 창에서 '온톡에서 찾기'로 넣은 수신자가 한 명 이상 있어야 합니다. " +
                                 "온톡에서 직접 넣은 수신자는 소속을 알 수 없어 그룹에 담지 못합니다.";
            return;
        }
        var members = _picked.Distinct().ToList();
        if (_store.GetGroups().Any(g => g.Name == name) &&
            Dialogs.Show(this, $"그룹 '{name}'의 구성원을 지금 넣은 {members.Count}명으로 바꿀까요?", "그룹 덮어쓰기",
                MessageBoxButton.OKCancel) != MessageBoxResult.OK)
        {
            return;
        }
        _store.SaveGroup(name, members);
        ReloadGroups();
        ControlStatus.Text = $"그룹 '{name}'에 {members.Count}명을 저장했습니다: {string.Join(", ", members.Select(m => m.Name))}";
    }

    private void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        if (GroupBox.SelectedItem is GroupItem { Group: var group } &&
            Dialogs.Show(this, $"그룹 '{group.Name}'을(를) 삭제할까요?", "그룹 삭제", MessageBoxButton.OKCancel) ==
            MessageBoxResult.OK)
        {
            _store.DeleteGroup(group.Id);
            ReloadGroups();
        }
    }

    private async void OnSetReservation(object sender, RoutedEventArgs e)
    {
        if (ReserveDate.SelectedDate is not { } date || ReserveHour.SelectedItem is not int hour ||
            ReserveMinute.SelectedItem is not int minute)
        {
            ControlStatus.Text = "예약 날짜와 시각을 정해 주세요.";
            return;
        }
        var when = date.Date.AddHours(hour).AddMinutes(minute);
        if (when <= DateTime.Now.AddMinutes(2))
        {
            ControlStatus.Text = "예약 시각은 지금보다 뒤여야 합니다.";
            return;
        }
        var hwnd = _target;
        var status = await RunOnTalk("온톡에 예약 시각을 설정하는 중", () => ComposeActions.SetReservation(hwnd, when));
        ControlStatus.Text = status == ActionStatus.Ok
            ? $"온톡 창에 예약 {when:yyyy-MM-dd HH:mm}이(가) 표시된 것을 확인했습니다. 아직 등록된 것은 아니고, 전송해야 예약이 등록됩니다."
            : "예약을 설정하지 못했습니다. " + Explain(status);
    }

    private async void OnCancelReservation(object sender, RoutedEventArgs e)
    {
        var hwnd = _target;
        var status = await RunOnTalk("온톡의 예약을 푸는 중", () => ComposeActions.CancelReservation(hwnd));
        ControlStatus.Text = status == ActionStatus.Ok ? "예약을 풀었습니다. 즉시 전송 상태입니다." : "예약을 풀지 못했습니다. " + Explain(status);
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed) return;
        var final = Final;
        if (DraftText.UnfilledPlaceholders(final) is { Count: > 0 } unfilled)
        {
            Result($"채우지 않은 자리가 있어 보내지 않았습니다: {string.Join(" ", unfilled)}", ok: false);
            return;
        }
        if (final.Trim().Length == 0)
        {
            Result("본문이 비어 있어 보내지 않았습니다.", ok: false);
            return;
        }
        if (NoteBytes.Count(final) > NoteBytes.Limit)
        {
            Result($"{NoteBytes.Limit}바이트를 넘어 보내지 않았습니다.", ok: false);
            return;
        }
        // 먼저 초안을 온톡 창에 맞춘다. 맞추지 못하면 보내지 않는다.
        if (!await ApplyAsync(final, _synced, askOnConflict: true))
        {
            return;
        }

        var hwnd = _target;
        var state = await RunOnTalk("전송 전에 온톡 창을 다시 읽는 중", () => ComposeWriter.Read(hwnd));
        ControlStatus.Text = "";
        if (state is null)
        {
            Result("온톡 작성 창을 찾지 못해 보내지 않았습니다.", ok: false);
            return;
        }
        if (_closed || !CheckPersonalizedTarget(state)) return;
        // 확인 창에는 초안이 아니라 지금 온톡 창에 실제로 들어 있는 내용을 보여 준다.
        var expected = new ExpectedCompose(state.Recipients, final, state.Reservation, state.SmsChecked, state.Attachments);
        if (ComposeActions.Difference(state, expected) is { } problem)
        {
            Result($"보내지 않았습니다. {problem}", ok: false);
            return;
        }
        if (Dialogs.Show(this, Confirmation(state, final), "전송 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            Result("보내지 않았습니다.", ok: false);
            return;
        }

        // 누르기 전에 기록한다. 누른 뒤 앱이 죽어도 "보냈을 수 있다"는 기록이 남는다.
        RecordPersonalizedAttempt(-1, final, "전송 준비 기록 · 결과 확인 필요");
        var id = _store.BeginDispatch(state.Recipients, final, state.Reservation, DateTimeOffset.Now);
        RecordPersonalizedAttempt(id, final, "전송 시도 · 결과 확인 필요");
        SendOutcome outcome;
        try { outcome = await RunOnTalk("온톡의 전송 단추를 누르는 중", () => ComposeActions.Send(hwnd, expected)); }
        catch (Exception ex) { if (!_closed) Result("전송 결과를 확인하지 못했습니다. 자동 재전송하지 않습니다. " + ex.Message, false); return; }
        if (_closed) return;
        RecordPersonalizedAttempt(id, final, outcome.Status switch { SendStatus.WindowClosed => state.Reservation is null ? "작성창 닫힘 · 도착 미확인" : "예약 시도 후 작성창 닫힘 · 등록 확인 필요", SendStatus.StillOpen => "결과 불명 · 직접 확인 필요", _ => "미전송 · 직접 확인 필요" });
        ControlStatus.Text = "";
        var kind = state.Reservation is { } at ? $"{at:yyyy-MM-dd HH:mm} 예약 등록" : "전송";
        switch (outcome.Status)
        {
            case SendStatus.WindowClosed:
                _store.FinishDispatch(id, DispatchStatus.WindowClosed, "");
                // 보낸 초안이 남아 있으면 모르고 다시 보낼 수 있다. 본문은 발송 기록에 남아 있다.
                Editor.Clear();
                SaveDraft();
                _picked.Clear();
                Result($"전송 단추를 눌렀고 온톡 작성 창이 닫혔습니다({kind}). 상대에게 도착했는지는 여기서 확인하지 못하니 " +
                       "온톡 보낸 쪽지함에서 확인하세요. 보낸 초안은 비웠습니다.", ok: true);
                if (_activeItem is not null) _personalizedPane?.SelectNextPending();
                break;
            case SendStatus.StillOpen:
                _store.FinishDispatch(id, DispatchStatus.StillOpen, outcome.Detail);
                Result("전송 단추를 눌렀지만 온톡 작성 창이 닫히지 않았습니다. 보내졌는지 알 수 없습니다. " +
                       "다시 누르지 않았으니 온톡 창과 보낸 쪽지함을 직접 확인하세요.", ok: false);
                break;
            default:
                _store.FinishDispatch(id, DispatchStatus.NotSent, outcome.Detail);
                Result($"보내지 않았습니다. {outcome.Detail}", ok: false);
                break;
        }
    }

    private string Confirmation(ComposeState state, string body)
    {
        var known = _picked.ToList();
        var lines = state.Recipients.Select(name =>
        {
            // 같은 이름을 여러 번 골랐을 수 있으므로 한 번 쓴 기록은 뺀다.
            var picked = known.FirstOrDefault(p => p.Name == name);
            if (picked is null)
            {
                return $"  · {name} — 온톡에서 직접 넣은 수신자라 이름만 확인했습니다";
            }
            known.Remove(picked);
            return $"  · {name} — {picked.Title}, {string.Join(", ", picked.Details.Take(2))}";
        });
        var preview = body.Length > 200 ? body[..200] + "…" : body;
        return (state.Reservation is { } at
                   ? $"온톡에서 {at:yyyy-MM-dd HH:mm}에 보내도록 예약합니다.\n\n"
                   : "온톡에서 지금 바로 보냅니다.\n\n") +
               $"받는 사람 {state.Recipients.Count}명\n{string.Join("\n", lines)}\n\n" +
               $"SMS 발송: {(state.SmsChecked ? "선택됨" : "선택 안 됨")}\n" +
               $"본문 {state.UsedBytes} Bytes\n{preview}\n\n" +
               "보낸 뒤에는 되돌릴 수 없습니다. 보낼까요?";
    }
}
