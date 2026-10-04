using System.Windows;
using System.Windows.Controls;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>확인된 목록과 명시적 대상 선택을 사용한다. 자동으로 수신자를 추가하지 않는다.</summary>
public sealed class PeoplePane : UserControl
{
    private readonly ProfileStore _store;
    private readonly Action<ContactPreparation> _prepare;
    private readonly Action<StoredNote> _openNote;
    private readonly TextBlock _coverage = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _selection = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _query = new() { Tag = "예: 여비 / 총무과" };
    private readonly StackPanel _rows = new();
    private readonly StackPanel _history = new();
    private readonly ComboBox _scopes = new() { DisplayMemberPath = "Name" };
    private readonly Button _chooseScope = new() { Content = "이 그룹·부서에서 읽은 사람만 고르기", IsEnabled = false };
    private readonly Expander _scopePanel = new() { Header = "온톡에서 확인한 그룹·부서" };
    private readonly Expander _detailPanel = new() { Header = "선택한 사람의 담당업무·연락처" };
    private readonly ComboBox _purpose = new();
    private readonly ComboBox _savedGroups = new() { DisplayMemberPath = "Label" };
    private readonly TextBox _groupName = new() { Tag = "연락 준비 그룹 이름" };
    private readonly Button _start = new() { Content = "고른 대상에게 연락 준비" };
    private readonly Button _save = new() { Content = "선택한 대상을 연락 준비 그룹으로 저장" };
    private PeopleSnapshot _snapshot;
    private List<ContactPerson>? _savedPeople;
    private string _savedSource = "";
    private DateTimeOffset _savedObserved;
    private readonly HashSet<string> _selected = [];
    private readonly Dictionary<string, ContactPerson> _detailCache = [];
    private string _nativeSelection = "";
    private string _renderSignature = "\0";
    private bool _refreshing;
    private bool _native;

    public PeoplePane(ProfileStore store, MainMenu menu, Action<ContactPreparation> prepare, Action<StoredNote> openNote)
    {
        (_store, _prepare, _openNote) = (store, prepare, openNote);
        _snapshot = new(menu, [], [], [], DateTimeOffset.Now);
        _purpose.ItemsSource = new[] { "문의", "진행 확인", "자료 요청", "감사" }; _purpose.SelectedIndex = 0;
        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(Label(menu == MainMenu.Friends ? "사람·그룹과 연락 준비" : "담당자 후보와 연락 준비", "SectionTitle"));
        body.Children.Add(_coverage);
        var scopeContent = new StackPanel(); scopeContent.Children.Add(_scopes); scopeContent.Children.Add(_chooseScope);
        _scopePanel.Content = scopeContent; body.Children.Add(_scopePanel);
        _chooseScope.Click += (_, _) => SelectScope();
        _query.ToolTip = "이름·소속·직급·확인한 담당업무의 글자 일치 검색. 예: 여비 / 건설과 보상";
        body.Children.Add(Label(menu == MainMenu.Organization ? "담당업무·소속에서 후보 찾기" : "확인한 사람 찾기", "SectionTitle"));
        body.Children.Add(_query);
        _query.TextChanged += (_, _) => RenderRows();
        body.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(_selection); body.Children.Add(_warning);
        _detailPanel.Content = new ScrollViewer { Content = _details, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(_detailPanel);
        var clear = new Button { Content = "대상 선택 비우기" };
        clear.Click += (_, _) => { _selected.Clear(); _native = false; RenderRows(); UpdateSelection(); }; body.Children.Add(clear);
        body.Children.Add(Label("연락 목적", "SectionTitle")); body.Children.Add(_purpose); body.Children.Add(_start);
        _start.Click += (_, _) => { if (Chosen.Count > 0) _prepare(Preparation()); };
        body.Children.Add(Label("연락 준비 그룹 · 이 계정에 저장", "SectionTitle")); body.Children.Add(_groupName); body.Children.Add(_save);
        _save.Click += (_, _) => SaveGroup();
        body.Children.Add(_savedGroups);
        var load = new Button { Content = "저장한 연락 후보 불러오기" }; load.Click += (_, _) => LoadGroup(); body.Children.Add(load);
        var live = new Button { Content = "현재 온톡 목록으로 돌아가기" }; live.Click += (_, _) => { _savedPeople = null; _selected.Clear(); _native = false; _renderSignature = ""; Refresh(_snapshot); }; body.Children.Add(live);
        var delete = new Button { Content = "저장한 연락 준비 그룹 삭제" }; delete.Click += (_, _) => DeleteGroup(); body.Children.Add(delete);
        body.Children.Add(_status);
        body.Children.Add(Label("같은 발신자 이름의 보관 후보", "SectionTitle"));
        body.Children.Add(Label("한 명을 고르면 최근 최대 8건을 표시합니다. 이름 일치만으로 같은 사람의 자료라고 확정하지 않습니다.", "Caption"));
        body.Children.Add(_history);
        body.Children.Add(Label("연락 후보만 작성 도우미로 넘깁니다. 실제 수신자는 온톡 작성창에서 검색하고 소속을 확인한 뒤 추가하세요.", "Caption"));
        foreach (var element in body.Children.OfType<FrameworkElement>()) element.Margin = new Thickness(0, 6, 0, 0);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ReloadGroups(); UpdateSelection();
    }

    private TextBlock Label(string text, string style) => new() { Text = text, Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<ContactPerson> Current => _savedPeople ?? _snapshot.People;
    public IReadOnlyList<ContactPerson> Chosen => Current.Where(p => _selected.Contains(p.SelectionKey)).ToList();
    public string SelectionNotice => _selection.Text;
    private ContactPreparation Preparation() => new(_purpose.SelectedItem as string ?? "문의", Chosen.ToList(),
        _savedPeople is not null ? _savedSource : (_native ? "온톡 선택 신호" : "사이드카에서 직접 선택") + " · " + _snapshot.Coverage,
        _savedPeople is not null ? _savedObserved : _snapshot.ObservedAt);

    public void Refresh(PeopleSnapshot snapshot)
    {
        // 마우스를 패널로 옮기면 온톡의 상세가 사라진다. 같은 화면 행에서 관찰한 상세를 시각과 함께 유지한다.
        var unique = snapshot.People.GroupBy(p => p.DisplayIdentity).Where(g => g.Count() == 1).Select(g => g.Single().SelectionKey).ToHashSet();
        foreach (var gone in _detailCache.Keys.Where(k => !unique.Contains(k)).ToList()) _detailCache.Remove(gone);
        var enriched = snapshot.People.Select(p =>
        {
            if (!unique.Contains(p.SelectionKey)) return p;
            if (p.DetailsObservedAt is not null) _detailCache[p.SelectionKey] = p;
            return p.DetailsObservedAt is null && _detailCache.TryGetValue(p.SelectionKey, out var previous)
                ? p with { Duty = previous.Duty, Phone = previous.Phone, Mail = previous.Mail, DetailsObservedAt = previous.DetailsObservedAt } : p;
        }).ToList();
        snapshot = snapshot with { People = enriched };
        _snapshot = snapshot;
        if (_savedPeople is not null) return;
        _scopePanel.Visibility = snapshot.Scopes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _chooseScope.IsEnabled = snapshot.Scopes.Count > 0;
        _coverage.Text = snapshot.Coverage;
        var native = string.Join("|", snapshot.People.Where(p => snapshot.SelectedKeys.Contains(p.Key)).Select(p => p.SelectionKey).Order());
        if (_nativeSelection != native)
        {
            _nativeSelection = native; _selected.Clear();
            foreach (var person in snapshot.People.Where(p => snapshot.SelectedKeys.Contains(p.Key))) _selected.Add(person.SelectionKey);
            _native = _selected.Count > 0;
        }
        _selected.IntersectWith(snapshot.People.Select(p => p.SelectionKey));
        var signature = string.Join("|", snapshot.People.Select(p => p.SelectionKey + p.Status + p.Duty + p.Phone + p.Mail)) +
            string.Join("|", snapshot.Scopes.Select(s => s.Key + s.Name + string.Join(",", s.MemberKeys) + s.DeclaredCount + s.Selected)) + native;
        if (_renderSignature == signature) return;
        _renderSignature = signature;
        var scopeKey = (_scopes.SelectedItem as ContactScope)?.Key;
        _scopes.ItemsSource = snapshot.Scopes;
        var activeScopes = snapshot.Scopes.Where(s => s.Selected).ToList();
        _scopes.SelectedItem = snapshot.Scopes.FirstOrDefault(s => s.Key == scopeKey) ?? (activeScopes.Count == 1 ? activeScopes[0] : null);
        RenderRows(); UpdateSelection();
    }

    private void RenderRows()
    {
        _refreshing = true; _rows.Children.Clear();
        var matches = _query.Text.Trim().Length > 0 ? ContactSupport.Find(Current, _query.Text) : Current.Select(p => new ContactMatch(p, "")).ToList();
        foreach (var hit in matches)
        {
            var person = hit.Person;
            var row = new StackPanel();
            var label = Label(person.Label + (person.Status.Length > 0 ? $"\n현재 표시 상태: {person.Status}" : ""), "Body");
            var check = new CheckBox { Content = label, IsChecked = _selected.Contains(person.SelectionKey), Margin = new Thickness(0, 7, 0, 7) };
            check.Click += (_, _) =>
            {
                if (_refreshing) return;
                if (check.IsChecked == true) _selected.Add(person.SelectionKey); else _selected.Remove(person.SelectionKey);
                _native = false; UpdateSelection();
            };
            row.Children.Add(check);
            if (hit.Evidence.Length > 0) row.Children.Add(Label("확인한 후보 근거\n" + hit.Evidence, "Caption"));
            _rows.Children.Add(row);
        }
        if (matches.Count == 0) _rows.Children.Add(Label("확인한 범위에 일치하는 사람이 없습니다. 온톡에서 부서를 펼치거나 사람 상세를 표시한 뒤 다시 확인하세요.", "Small"));
        _refreshing = false;
    }

    private void UpdateSelection()
    {
        var chosen = Chosen;
        _selection.Text = chosen.Count == 0 ? "온톡에서 선택된 사람을 확인하지 못했습니다. 위 목록에서 대상을 직접 고르세요."
            : $"{(_savedPeople is not null ? "저장한 연락 후보" : _native ? "온톡 선택 신호 확인" : "사이드카에서 직접 선택")} · {chosen.Count}명\n{string.Join(", ", chosen.Select(p => p.Name))}" +
                (_query.Text.Trim().Length > 0 ? "\n검색 결과 밖의 선택도 유지합니다." : "");
        _warning.Text = ContactSupport.IdentityWarning(chosen.Count > 0 ? Current.Where(p => chosen.Any(c => c.Name == p.Name)).ToList() : Current);
        _details.Text = chosen.Count == 1 ? Detail(chosen[0]) : "";
        _detailPanel.Visibility = chosen.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_native && _snapshot.Scopes.Any(s => s.Selected))
            _selection.Text += "\n선택 그룹·부서: " + string.Join(", ", _snapshot.Scopes.Where(s => s.Selected).Select(s => s.Name)) + " · 읽은 구성원만 포함";
        _start.IsEnabled = _save.IsEnabled = chosen.Count > 0;
        _history.Children.Clear();
        if (chosen.Count != 1) return;
        var notes = _store.GetContactNoteCandidates(chosen[0].Name);
        foreach (var note in notes)
        {
            var button = new Button { Content = Label($"{note.Note.SenderName} · {note.Note.SentAtText}\n{note.Note.SenderDetail}\n{Snippet.Make(note.Note.Body, [], 45)}", "Small"), Margin = new Thickness(0, 4, 0, 0) };
            button.Click += (_, _) => _openNote(note); _history.Children.Add(button);
            foreach (var task in _store.GetTasks(note.Id).Where(t => !t.Done).Take(3))
                _history.Children.Add(Label($"이 후보 쪽지의 열린 업무: {task.Title}", "Caption"));
        }
        if (notes.Count == 0) _history.Children.Add(Label("같은 발신자 이름으로 보관한 쪽지가 없습니다.", "Small"));
    }

    private static string Detail(ContactPerson person) =>
        (person.DetailsObservedAt is { } observed ? $"상세 확인: {observed.ToLocalTime():HH:mm:ss} 당시 표시\n" : "") +
        (person.Duty.Length > 0 ? "확인한 담당업무: " + person.Duty : "담당업무: 아직 확인하지 못했습니다.") +
        (person.Phone.Length > 0 ? "\n직장전화: " + person.Phone : "") + (person.Mail.Length > 0 ? "\n메일: " + person.Mail : "");

    private void SelectScope()
    {
        if (_scopes.SelectedItem is not ContactScope scope) { _status.Text = "그룹·부서를 먼저 고르세요."; return; }
        _savedPeople = null; _selected.Clear(); _native = false;
        foreach (var p in _snapshot.People.Where(p => scope.MemberKeys.Contains(p.Key))) _selected.Add(p.SelectionKey);
        _status.Text = $"'{scope.Name}'에서 읽은 {_selected.Count}명만 골랐습니다." +
            (scope.DeclaredCount is { } count ? $" 온톡 표시 {count}명 · 전체 구성원 여부를 확인하세요." : " 부서 전체를 선택한 것으로 간주하지 않습니다.");
        _coverage.Text = _snapshot.Coverage; RenderRows(); UpdateSelection();
    }

    private void SaveGroup()
    {
        var name = _groupName.Text.Trim();
        if (name.Length == 0 || Chosen.Count == 0) { _status.Text = "그룹 이름과 연락 대상을 고르세요."; return; }
        if (_store.GetContactGroups().Any(g => g.Name == name) && Dialogs.Show(Window.GetWindow(this), $"'{name}'의 연락 후보를 지금 선택한 대상으로 바꿀까요?", "그룹 저장", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _store.SaveContactGroup(name, Preparation(), DateTimeOffset.Now); ReloadGroups();
        _status.Text = "연락 후보를 이 계정에 저장했습니다. 실제 온톡 그룹이나 수신자를 변경하지 않았습니다.";
    }

    private void ReloadGroups() => _savedGroups.ItemsSource = _store.GetContactGroups();
    private void LoadGroup()
    {
        if (_savedGroups.SelectedItem is not ContactPreparationGroup group) return;
        _savedPeople = group.People.Select((p, i) => p with { Key = $"saved:{group.Id}:{i}", Status = "" }).ToList();
        _savedSource = $"저장한 연락 준비 그룹 '{group.Name}' · {group.Source}"; _savedObserved = group.ObservedAt;
        _selected.Clear(); foreach (var p in _savedPeople) _selected.Add(p.SelectionKey);
        _scopes.ItemsSource = null;
        _scopePanel.Visibility = Visibility.Collapsed;
        _coverage.Text = $"저장한 후보 {group.People.Count}명 · {group.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm} 당시 정보 · 현재 재확인 필요";
        _status.Text = "현재 온톡 선택과 별개인 저장 후보입니다. 인사이동·소속을 다시 확인하세요.";
        RenderRows(); UpdateSelection();
    }

    private void DeleteGroup()
    {
        if (_savedGroups.SelectedItem is not ContactPreparationGroup group || Dialogs.Show(Window.GetWindow(this), $"'{group.Name}'의 저장한 연락 후보를 삭제할까요?", "그룹 삭제", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _store.DeleteContactGroup(group.Id); ReloadGroups(); _status.Text = "저장한 연락 준비 그룹을 삭제했습니다.";
    }
}
