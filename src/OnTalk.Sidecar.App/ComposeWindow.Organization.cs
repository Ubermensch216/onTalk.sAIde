using System.Windows;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.App;

public partial class ComposeWindow
{
    private IReadOnlyList<ContactPerson> _preparedPeople = [];
    private PersonalizedWindow? _personalizedWindow;
    private PersonalizedWork? _activeWork;
    private PersonalizedWorkItem? _activeItem;
    private Guid _personalizedSession;

    private void OnPersonalize(object sender, RoutedEventArgs e)
    {
        if (_personalizedWindow is not null) { _personalizedWindow.Show(); _personalizedWindow.Activate(); return; }
        _personalizedWindow = new PersonalizedWindow(_store, _writer, _preparedPeople, Editor.Text, PreparePersonalizedAsync) { Owner = this };
        _personalizedWindow.Closed += (_, _) => _personalizedWindow = null;
        _personalizedWindow.Show();
    }

    private async void OnAddPrepared(object sender, RoutedEventArgs e)
    {
        if (_busy || _activeItem is not null || _preparedPeople.Count == 0) return;
        if (_preparedPeople.GroupBy(p => p.DisplayIdentity).Any(g => g.Count() > 1))
        { Result("표시 정보가 같은 대상이 있습니다. 조직도에서 중복 후보를 확인하고 제외하세요.", false); return; }
        if (_target == IntPtr.Zero && !await EnsureAutoTargetAsync()) return;
        var hwnd = _target; var session = _targetSession;
        try
        {
            var result = await RunOnTalk<(List<Recipient> Added, string Error)>("조직도 선택 대상의 이름·직급·소속을 대조하고 수신자를 추가하는 중", () =>
            {
                if (ComposeWriter.Read(hwnd) is not { Recipients.Count: 0 }) return (Added: new List<Recipient>(), Error: "빈 수신자 목록에서 시작하세요. 기존 수신자가 있는 창에는 일괄 추가하지 않습니다.");
                var added = new List<Recipient>();
                foreach (var person in _preparedPeople)
                {
                    if (_closing.IsCancellationRequested) return (added, "창이 닫혀 중단했습니다.");
                    var found = ComposeActions.SearchRecipient(hwnd, person.Name);
                    if (found.Status != ActionStatus.Ok) return (added, person.Name + ": " + Explain(found.Status));
                    var matches = found.Rows.Where(r => PreparedRecipientMatch.Matches(person, r.ToRecipient())).ToList();
                    if (matches.Count != 1) return (added, person.Label + " · 일치하는 수신자를 하나로 확정하지 못했습니다. 아래 검색에서 직접 확인하세요.");
                    var status = ComposeActions.AddRecipient(hwnd, found.SearchWindow, matches[0]);
                    if (status != ActionStatus.Ok) return (added, person.Name + ": " + Explain(status));
                    added.Add(matches[0].ToRecipient());
                }
                return (added, "");
            });
            if (_closed || hwnd != _target || session != _targetSession) return;
            _picked.AddRange(result.Added);
            Result($"{_preparedPeople.Count}명 중 {result.Added.Count}명 추가 확인. " + (result.Error.Length == 0 ? "본문과 예약을 확인한 뒤 전송하세요." : result.Error), result.Error.Length == 0);
        }
        catch (Exception ex) { if (!_closed) Result("수신자 추가 결과를 확인하지 못했습니다. 온톡 창을 확인하세요. " + ex.Message, false); }
    }

    private async Task PreparePersonalizedAsync(PersonalizedWork work, PersonalizedWorkItem item)
    {
        if (_closed || _busy || item.Attempted) return;
        if ((_target == IntPtr.Zero || TargetBox.SelectedItem is not ComposeTarget { Kind: NoteComposeKind.New }) &&
            !await EnsureAutoTargetAsync())
        {
            return;
        }
        var hwnd = _target; var session = _targetSession;
        try
        {
            var outcome = await RunOnTalk<(Recipient? Recipient, string Error)>(item.Name + "의 수신자 확인 중", () =>
            {
                var current = ComposeWriter.Read(hwnd);
                if (current is null || current.Recipients.Count != 0 || current.Body.Length != 0 || current.Reservation is not null || current.SmsChecked)
                    return (Recipient: (Recipient?)null, Error: "수신자·본문·예약·SMS가 없는 빈 새 쪽지 창이 필요합니다.");
                var found = ComposeActions.SearchRecipient(hwnd, item.Name);
                if (found.Status != ActionStatus.Ok) return (null, Explain(found.Status));
                var matches = found.Rows.Where(r => PreparedRecipientMatch.Matches(item.Person, r.ToRecipient())).ToList();
                if (matches.Count != 1) return (null, "이름·직급·소속이 일치하는 한 사람을 확정하지 못했습니다. 조직도 정보와 온톡 검색 결과를 확인하세요.");
                if (_closing.IsCancellationRequested) return (null, "창이 닫혀 중단했습니다.");
                var status = ComposeActions.AddRecipient(hwnd, found.SearchWindow, matches[0]);
                return status == ActionStatus.Ok ? (matches[0].ToRecipient(), "") : (null, Explain(status));
            });
            if (_closed || hwnd != _target || session != _targetSession) return;
            if (outcome.Recipient is null) { Result(outcome.Error, false); return; }
            _activeWork = work; _activeItem = item; _personalizedSession = session;
            _picked.Clear(); _picked.Add(outcome.Recipient);
            Remember(); _quote = ""; IncludeQuote.IsChecked = false; _synced = "";
            Editor.Text = item.Body;
            item.Status = "작성 중 · 미발송";
            SourceText.Text = "개인별 쪽지 · " + item.Person.Label;
            Result("수신자 1명을 확인했습니다. 본문을 확인하고 예약 설정 또는 전송을 진행하세요.", true);
            Activate(); EnableControls();
        }
        catch (Exception ex) { if (!_closed) Result("개인 쪽지 준비를 중단했습니다. 온톡 창을 확인하세요. " + ex.Message, false); }
    }

    private async Task<bool> EnsureAutoTargetAsync()
    {
        var acquired = await RunOnTalk<(IntPtr Hwnd, ComposeState? State)>("온톡에서 빈 '새 쪽지' 창을 찾거나 새로 여는 중", () =>
        {
            var (status, hwnd) = ComposeActions.EnsureEmptyNewComposeWindow();
            return status == ActionStatus.Ok && hwnd != IntPtr.Zero
                ? (hwnd, ComposeWriter.Read(hwnd))
                : (IntPtr.Zero, null);
        });
        if (_closed) return false;
        if (acquired.Hwnd == IntPtr.Zero || acquired.State is null)
        {
            Result("온톡에서 빈 '새 쪽지' 창을 찾거나 열지 못했습니다. 온톡 본창이 열려 있는지 확인하세요.", false);
            return false;
        }
        var current = TargetBox.ItemsSource as IReadOnlyList<ComposeTarget> ?? [];
        var target = current.FirstOrDefault(t => t.Hwnd == acquired.Hwnd);
        if (target is null)
        {
            target = new ComposeTarget(acquired.Hwnd, $"새 쪽지 · 수신 없음 · 0/{acquired.State.LimitBytes}", acquired.State)
            {
                SessionId = Guid.NewGuid(),
                Kind = NoteComposeKind.New
            };
            TargetBox.ItemsSource = current.Concat([target]).ToList();
        }
        TargetBox.SelectedItem = target;
        return _target != IntPtr.Zero;
    }

    private bool CheckPersonalizedTarget(ComposeState? state)
    {
        if (_activeItem is null) return true;
        if (!NotePersonalization.IsValidBody(Final))
        {
            Result("개인 쪽지의 빈 본문·미완성 변수·바이트 초과를 먼저 수정하세요.", false);
            return false;
        }
        if (_activeItem.Attempted || _personalizedSession != _targetSession || state is null ||
            state.Recipients.Count != 1 || state.Recipients[0] != _activeItem.Name || _picked.Count != 1 ||
            !PreparedRecipientMatch.Matches(_activeItem.Person, _picked[0]) || state.SmsChecked)
        {
            Result("개인 쪽지의 확인한 수신자·창이 달라졌거나 이미 전송을 시도했습니다. 다른 사람에게 반영하지 않았습니다. 개인별 쪽지 준비에서 다시 확인하세요.", false);
            return false;
        }
        return true;
    }

    private void RecordPersonalizedAttempt(long id, string body, string status)
    {
        if (_activeWork is null || _activeItem is null) return;
        _activeItem.DispatchRevision++; _activeItem.DispatchId = id; _activeItem.Body = body; _activeItem.Status = status;
        _store.SavePersonalizedWork(_activeWork);
    }
}
