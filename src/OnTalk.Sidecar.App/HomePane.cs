using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>친구·그룹에 연결한 보관 자료의 읽기 중심 대시보드.</summary>
public sealed class HomePane : UserControl, IDisposable
{
    private readonly ProfileStore _store;
    private readonly Action<StoredNote> _openNote;
    private readonly Action _openTasks;
    private readonly ComboBox _groups = new(), _friends = new();
    private readonly StackPanel _body = new(), _periods = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _coverage = new(), _scope = new();
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private PeopleSnapshot _snapshot = new(MainMenu.Friends, [], [], [], DateTimeOffset.Now);
    private string _signature = "", _nativeSelection = "";
    private string _dataSignature = "";
    private string? _groupKey, _friendIdentity;
    private int? _days = 7;
    private bool _refreshing, _active, _disposed;
    private Window? _detailWindow;
    public HomeDashboard? Dashboard { get; private set; }
    public IReadOnlyList<ContactPerson> FilteredPeople => GroupPeople.Where(p => _friendIdentity is null || p.DisplayIdentity == _friendIdentity).ToList();
    private IReadOnlyList<ContactPerson> GroupPeople => _snapshot.People.Where(p => _groupKey is null ||
        _snapshot.Scopes.Any(s => s.Key == _groupKey && s.MemberKeys.Contains(p.Key))).ToList();

    public HomePane(ProfileStore store, Action<StoredNote> openNote, Action openTasks)
    {
        (_store, _openNote, _openTasks) = (store, openNote, openTasks);
        Background = Brush("PageBg"); Foreground = Brush("TextStrong"); FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(Text("업무 소통 한눈에", "PageTitle"));
        root.Children.Add(Text("친구와 주고받은 업무, 다음에 이어갈 이야기", "Small"));
        var filters = new Grid { Margin = new Thickness(0, 10, 0, 8) };
        filters.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _groups.Margin = new Thickness(0, 0, 4, 0); _friends.Margin = new Thickness(4, 0, 0, 0);
        _groups.MinWidth = _friends.MinWidth = 0;
        _groups.ToolTip = "온톡에서 관찰한 친구 그룹"; _friends.ToolTip = "선택한 그룹에서 관찰한 친구";
        Grid.SetColumn(_friends, 1); filters.Children.Add(_groups); filters.Children.Add(_friends); root.Children.Add(filters);
        var periodRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var basis = Text("보관·관찰일 기준", "Caption"); basis.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(basis, Dock.Right); periodRow.Children.Add(basis); periodRow.Children.Add(_periods); root.Children.Add(periodRow);
        _scope.Style = (Style)FindResource("Caption"); root.Children.Add(_scope);
        root.Children.Add(_body);
        _coverage.Style = (Style)FindResource("Caption"); _coverage.Margin = new Thickness(2, 6, 2, 12);
        root.Children.Add(_coverage);
        _scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = _scroll;
        _groups.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _groupKey = (_groups.SelectedItem as Filter)?.Key; _friendIdentity = null;
            PopulateFriends(); Render(); _scroll.ScrollToTop();
        };
        _friends.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _friendIdentity = (_friends.SelectedItem as Filter)?.Key; Render(); _scroll.ScrollToTop();
        };
        _timer.Tick += (_, _) => { if (_active && !_disposed) Render(true); };
        RenderPeriods(); PopulateFilters(); Render();
    }

    public void Refresh(PeopleSnapshot snapshot)
    {
        var signature = string.Join('|', snapshot.People.Select(p => p.SelectionKey)) + string.Join('|', snapshot.Scopes.Select(s =>
            s.Key + s.Name + s.DeclaredCount + s.Selected + string.Join(',', s.MemberKeys))) + string.Join('|', snapshot.SelectedKeys) + snapshot.Truncated;
        if (_signature == signature) return;
        _signature = signature; _snapshot = snapshot;
        _store.SaveHomePeople(snapshot);
        if (_groupKey is not null && !snapshot.Scopes.Any(s => s.Key == _groupKey)) _groupKey = null;
        if (_friendIdentity is not null && !snapshot.People.Any(p => p.DisplayIdentity == _friendIdentity)) _friendIdentity = null;
        var native = string.Join('|', snapshot.People.Where(p => snapshot.SelectedKeys.Contains(p.Key)).Select(p => p.DisplayIdentity)) +
            string.Join('|', snapshot.Scopes.Where(s => s.Selected).Select(s => s.Key));
        if (native != _nativeSelection)
        {
            _nativeSelection = native;
            var group = snapshot.Scopes.Where(s => s.Selected).ToList();
            var people = snapshot.People.Where(p => snapshot.SelectedKeys.Contains(p.Key)).ToList();
            _groupKey = group.Count == 1 ? group[0].Key : null;
            _friendIdentity = people.Count == 1 ? people[0].DisplayIdentity : null;
        }
        PopulateFilters(); Render();
    }

    public void SetActive(bool active)
    {
        var changed = _active != active; _active = active;
        IsEnabled = active && !_disposed;
        if (active && !_disposed) { if (changed) Render(); _timer.Start(); }
        else { _timer.Stop(); _detailWindow?.Close(); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _active = false; _timer.Stop(); _detailWindow?.Close(); IsEnabled = false; }

    private sealed record Filter(string? Key, string Label) { public override string ToString() => Label; }
    private void PopulateFilters()
    {
        _refreshing = true;
        var groups = new List<Filter> { new(null, "전체 그룹") };
        groups.AddRange(_snapshot.Scopes.Where(s => s.Kind == ContactScopeKind.Group).Select(s => new Filter(s.Key, s.Name)));
        _groups.ItemsSource = groups; _groups.SelectedItem = groups.FirstOrDefault(g => g.Key == _groupKey) ?? groups[0];
        _refreshing = false; PopulateFriends();
    }
    private void PopulateFriends()
    {
        _refreshing = true;
        var friends = new List<Filter> { new(null, "전체 친구") };
        var people = GroupPeople.DistinctBy(p => p.DisplayIdentity).ToList();
        friends.AddRange(people.Select(p => new Filter(p.DisplayIdentity, p.Name + (people.Count(x => x.Name == p.Name) > 1 ? " · " + string.Join(" / ", p.Organization) : ""))));
        if (!friends.Any(f => f.Key == _friendIdentity)) _friendIdentity = null;
        _friends.ItemsSource = friends; _friends.SelectedItem = friends.First(f => f.Key == _friendIdentity);
        _refreshing = false;
    }
    private void RenderPeriods()
    {
        _periods.Children.Clear();
        foreach (var (label, days) in new (string, int?)[] { ("7일", 7), ("30일", 30), ("전체", null) })
        {
            var button = new Button { Content = label, MinHeight = 28, Padding = new Thickness(11, 0, 11, 0), Margin = new Thickness(0, 0, 4, 0),
                Style = (Style)FindResource(days == _days ? "PrimaryButton" : "SmallButton") };
            button.Click += (_, _) => { _days = days; RenderPeriods(); Render(); }; _periods.Children.Add(button);
        }
    }
    private void Render(bool skipUnchanged = false)
    {
        if (_disposed) return;
        var now = DateTimeOffset.Now;
        var people = FilteredPeople;
        var data = Dashboard = _store.GetHomeDashboard(people, now, _days);
        var signature = JsonSerializer.Serialize(data) + HomeDashboard.Day(now);
        if (skipUnchanged && signature == _dataSignature) return;
        _dataSignature = signature;
        _body.Children.Clear();
        var scope = _snapshot.Scopes.FirstOrDefault(s => s.Key == _groupKey);
        _scope.Text = _friendIdentity is not null && people.FirstOrDefault() is { } person
            ? person.Name + " · " + string.Join(" / ", person.Organization) + " · 연결한 자료 집계"
            : (scope?.Name ?? "전체 그룹") + $" · 확인한 친구 {people.Select(p => p.DisplayIdentity).Distinct().Count()}명" +
              (scope?.DeclaredCount is { } declared ? $" / 그룹 표시 {declared}명" : "") + " · 연결한 자료 집계";
        _coverage.Text = "온톡에서 관찰한 친구·그룹과 이 계정에 보관한 자료만 표시합니다. 접힌 그룹·화면 밖 친구와 전체 소통량은 포함하지 않습니다.\n" +
            $"쪽지: 최초 보관일 / 대화: 원문 관찰일 기준 · {now.ToOffset(KoreanNoteTime.SeoulOffset):HH:mm} 갱신";
        RenderMetrics(data);
        if (people.Count == 0)
        {
            _body.Children.Add(Card(Text("친구 그룹을 펼쳐 주세요", "SectionTitle"), Text("온톡 Home에서 친구가 표시되면 해당 그룹의 업무 소통을 모아 보여드립니다.", "Small")));
            return;
        }
        RenderAttention(data, now);
        RenderChats(data, people);
        RenderNotes(data, people);
        RenderActivity(data);
        RenderContacts(data);
    }
    private void RenderMetrics(HomeDashboard data)
    {
        var grid = new Grid();
        var values = new[] { (data.MessageCount, "대화 원문"), (data.Notes.Count, "받은 쪽지"), (data.OpenTasks.Count, "열린 업무") };
        for (var i = 0; i < values.Length; i++)
        {
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var stack = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 12, 0, 0, 0) };
            var number = Text(values[i].Item1.ToString(), "PageTitle"); number.FontSize = 26; number.Foreground = Brush("Navy");
            stack.Children.Add(number); stack.Children.Add(Text(values[i].Item2, "Caption"));
            Grid.SetColumn(stack, i); grid.Children.Add(stack);
        }
        _body.Children.Add(Card(grid, Text("연결한 선택 원문 수 · 열린 업무는 기간과 무관", "Caption")));
    }
    private void RenderAttention(HomeDashboard data, DateTimeOffset now)
    {
        var content = new StackPanel();
        var top = new DockPanel();
        var all = Link("업무 보기 ›", _openTasks); DockPanel.SetDock(all, Dock.Right); top.Children.Add(all);
        top.Children.Add(Text("이어서 확인할 업무", "SectionTitle")); content.Children.Add(top);
        if (data.OpenTasks.Count == 0)
            content.Children.Add(Text("연결한 자료에 등록된 열린 업무가 없습니다.", "Small"));
        foreach (var task in data.OpenTasks.Take(3))
        {
            var row = new StackPanel { Margin = new Thickness(0, 3, 0, 6) };
            row.Children.Add(Text(task.Title, "Body"));
            var sender = task.NoteId is { } id ? _store.GetNote(id)?.Note.SenderName : _store.GetChatTaskSource(task.Id)?.Sender;
            var description = (sender ?? "발신자 미확인") + " · " + (task.NoteId is null ? "대화" : "쪽지") + " · " +
                (task.Due is { } due ? ReminderRule.Describe(due, now) : "기한 미등록");
            var meta = Text(description, "Caption");
            if (task.Due is { } date && date <= HomeDashboard.Day(now)) meta.Foreground = Brush("Warn");
            row.Children.Add(meta); content.Children.Add(row);
        }
        if (data.OpenTasks.Count > 3) content.Children.Add(Text($"외 {data.OpenTasks.Count - 3}건은 업무 목록에서 확인", "Caption"));
        _body.Children.Add(Card(content));
    }
    private void RenderChats(HomeDashboard data, IReadOnlyList<ContactPerson> people)
    {
        var content = new StackPanel(); content.Children.Add(Section("최근 대화 정리", data.Chats.Count));
        if (data.Chats.Count == 0)
        {
            content.Children.Add(Text("최근에 어떤 이야기를 나눴나요?", "Body"));
            content.Children.Add(Text("대화창의 ‘정리’에서 원문을 요약하고 친구에게 연결하면, 다음 연락 때 이어갈 맥락이 여기에 남습니다.", "Small"));
        }
        foreach (var chat in data.Chats.Take(1))
        {
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
            row.Children.Add(Text(ContactNames(chat.ContactIdentities, people), "SectionTitle"));
            row.Children.Add(Text(chat.Analysis.Summary, "Body"));
            row.Children.Add(Text($"AI 요약 · 선택 원문 {chat.Source.Messages.Count}건 · {chat.Source.ObservedAt.ToOffset(KoreanNoteTime.SeoulOffset):MM.dd HH:mm} 관찰", "Caption"));
            foreach (var point in chat.Analysis.Points.Take(1))
                row.Children.Add(Text((point.Kind switch { "decision" => "결정 후보", "question" => "질문 후보", _ => "요청 후보" }) + " · " + point.Title, "Small"));
            row.Children.Add(Link("요약 근거 보기 ›", () => ShowChat(chat, people))); content.Children.Add(row);
        }
        AddMore(content, data.Chats.Skip(1).Select(chat => Link(ContactNames(chat.ContactIdentities, people) + " · " + chat.Source.RoomHint,
            () => ShowChat(chat, people))), "이전 대화 정리");
        _body.Children.Add(Card(content));
    }
    private void RenderNotes(HomeDashboard data, IReadOnlyList<ContactPerson> people)
    {
        var content = new StackPanel(); content.Children.Add(Section("받은 업무 연락", data.Notes.Count));
        if (data.Notes.Count == 0) content.Children.Add(Text("친구에게 연결한 받은 쪽지가 없습니다.", "Small"));
        foreach (var note in data.Notes.Take(2)) content.Children.Add(NoteRow(note.Source, note.Analysis, people, false));
        AddMore(content, data.Notes.Skip(2).Select(note => NoteRow(note.Source, note.Analysis, people, false)), "이전 업무 연락");
        if (data.Candidates.Count > 0)
        {
            var candidates = new StackPanel();
            candidates.Children.Add(Text("발신자 이름이 같은 보관 쪽지입니다. 소속을 확인해 친구에게 연결하면 위 통계에 포함됩니다.", "Caption"));
            foreach (var candidate in data.Candidates) candidates.Children.Add(NoteRow(candidate, null, people, true));
            content.Children.Add(new Expander { Header = $"친구 연결 확인 · {data.Candidates.Count}건", Content = candidates,
                IsExpanded = data.Notes.Count == 0, Margin = new Thickness(0, 6, 0, 0) });
        }
        else if (data.Notes.Count == 0) content.Children.Add(Text("쪽지를 열어 읽으면 보관됩니다. 이후 이 화면에서 발신자와 친구를 연결하세요.", "Caption"));
        _body.Children.Add(Card(content));
    }
    private UIElement NoteRow(StoredNote source, NoteAnalysis? analysis, IReadOnlyList<ContactPerson> people, bool candidate)
    {
        var row = new StackPanel { Margin = new Thickness(0, 3, 0, 9) };
        row.Children.Add(Text(source.Note.SenderName + " · " + source.Note.SenderDetail, "SectionTitle"));
        row.Children.Add(Text(analysis?.Summary is { Length: > 0 } summary ? summary : Snippet.Make(source.Note.Body, [], 55), "Body"));
        row.Children.Add(Text((analysis is { Summary.Length: > 0 } ? "AI 요약" : "원문 발췌") + " · " + source.Note.SentAtText +
            (candidate ? " · 친구 미연결" : ""), "Caption"));
        if (analysis is not null)
            foreach (var action in analysis.Actions.Take(2)) row.Children.Add(Text("요청 · " + action.Title + (action.DueText is { } due ? " / " + due : ""), "Small"));
        row.Children.Add(Link(candidate ? "발신자 확인·친구 연결 ›" : "원문·연결 보기 ›", () => ShowNote(source, people)));
        return row;
    }
    private void RenderActivity(HomeDashboard data)
    {
        var content = new StackPanel(); content.Children.Add(Text("소통 흐름", "SectionTitle"));
        content.Children.Add(Text("최근 7일 · 쪽지 보관 / 대화 원문 관찰", "Caption"));
        var chart = new Grid { Height = 95, Margin = new Thickness(0, 6, 0, 0) };
        var max = Math.Max(1, data.Activity.Max(a => a.Notes + a.Messages));
        for (var i = 0; i < data.Activity.Count; i++)
        {
            chart.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var day = data.Activity[i]; var column = new DockPanel { Margin = new Thickness(3, 0, 3, 0), LastChildFill = true,
                ToolTip = $"{day.Day:MM.dd} · 받은 쪽지 {day.Notes}건 · 대화 원문 {day.Messages}건" };
            var label = Text(day.Day.ToString("MM.dd"), "Caption"); label.HorizontalAlignment = HorizontalAlignment.Center;
            DockPanel.SetDock(label, Dock.Bottom); column.Children.Add(label);
            var bars = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5, 0, 5, 5) };
            bars.Children.Add(new Border { Height = 65.0 * day.Notes / max, Background = Brush("Navy"), CornerRadius = new CornerRadius(3, 3, 0, 0) });
            bars.Children.Add(new Border { Height = 65.0 * day.Messages / max, Background = Brush("Accent"), CornerRadius = new CornerRadius(0, 0, 3, 3) });
            if (day.Notes + day.Messages == 0) bars.Children.Add(new Border { Height = 2, Background = Brush("Line") });
            column.Children.Add(bars); Grid.SetColumn(column, i); chart.Children.Add(column);
        }
        content.Children.Add(chart);
        var legend = new StackPanel { Orientation = Orientation.Horizontal };
        var notes = Text("● 쪽지   ", "Caption"); notes.Foreground = Brush("Navy");
        var messages = Text("● 대화 원문", "Caption"); messages.Foreground = Brush("Accent");
        legend.Children.Add(notes); legend.Children.Add(messages); content.Children.Add(legend);
        _body.Children.Add(Card(content));
    }
    private void RenderContacts(HomeDashboard data)
    {
        if (_friendIdentity is not null) return;
        var content = new StackPanel(); content.Children.Add(Text("친구별 소통", "SectionTitle"));
        foreach (var contact in data.Contacts.Where(c => c.Notes + c.Messages > 0).Take(5))
        {
            var button = Link($"{contact.Person.Name}  ·  대화 {contact.Messages} / 쪽지 {contact.Notes} ›", () =>
            { _friendIdentity = contact.Person.DisplayIdentity; PopulateFriends(); Render(); _scroll.ScrollToTop(); });
            content.Children.Add(button);
        }
        if (!data.Contacts.Any(c => c.Notes + c.Messages > 0)) content.Children.Add(Text("연결한 자료가 쌓이면 친구별 소통량을 보여드립니다.", "Small"));
        _body.Children.Add(Card(content));
    }
    private void ShowNote(StoredNote source, IReadOnlyList<ContactPerson> people)
    {
        var body = new StackPanel();
        body.Children.Add(Text(source.Note.SenderName + " · " + source.Note.SenderDetail, "SectionTitle"));
        body.Children.Add(Text(source.Note.SentAtText, "Caption"));
        body.Children.Add(Text(source.Note.Body, "Body"));
        body.Children.Add(Link("쪽지 상세에서 보기 ›", () => _openNote(source)));
        var eligible = people.Where(p => p.Name == source.Note.SenderName || _store.ContactNoteLinkedAt(p, source.Id) is not null)
            .GroupBy(p => p.DisplayIdentity).Where(g => g.Count() == 1).Select(g => g.Single()).ToList();
        body.Children.Add(Text("친구 연결 · 이름·직급·소속을 원문과 비교하세요", "SectionTitle"));
        if (eligible.Count == 0) body.Children.Add(Text("현재 범위에는 연결할 친구가 없습니다. 동일한 표시 정보의 행이 여러 개이면 구분 가능한 화면에서 다시 확인하세요.", "Small"));
        foreach (var person in eligible)
        {
            var button = new Button { Margin = new Thickness(0, 6, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Left };
            void Update() => button.Content = Text(person.Label + "\n" + (_store.ContactNoteLinkedAt(person, source.Id) is null ? "이 친구에게 연결" : "연결 해제"), "Small");
            Update();
            button.Click += (_, _) =>
            {
                if (_disposed || !_active) return;
                if (_store.ContactNoteLinkedAt(person, source.Id) is null) _store.LinkContactNote(person, source.Id, DateTimeOffset.Now);
                else _store.UnlinkContactNote(person, source.Id);
                Update(); Render();
            };
            body.Children.Add(button);
        }
        ShowDetail("받은 업무 연락 · 보관 원문", body);
    }
    private void ShowChat(HomeChatDigest chat, IReadOnlyList<ContactPerson> people)
    {
        var body = new StackPanel();
        body.Children.Add(Text(ContactNames(chat.ContactIdentities, people), "SectionTitle"));
        body.Children.Add(Text(chat.Source.RoomHint, "Small"));
        body.Children.Add(Text(chat.Analysis.Summary, "Body"));
        body.Children.Add(Text("AI 요약 · 선택한 원문 범위의 정리", "Caption"));
        foreach (var point in chat.Analysis.Points)
            body.Children.Add(Text($"{point.Title}\n근거: {point.Evidence}", "Body"));
        body.Children.Add(Text("분석한 원문", "SectionTitle"));
        foreach (var message in chat.Source.Messages) body.Children.Add(Text(message.Label, "Body"));
        body.Children.Add(Text(chat.Source.Coverage, "Caption"));
        foreach (var person in people.Where(p => chat.ContactIdentities.Contains(p.DisplayIdentity)).DistinctBy(p => p.DisplayIdentity))
            body.Children.Add(Link(person.Name + "에게 연결 해제", () =>
            { if (_disposed || !_active) return; _store.UnlinkHomeChat(person, chat.Id); Render(); Window.GetWindow(body)?.Close(); }));
        ShowDetail("최근 대화 정리 · 보관 근거", body);
    }
    private void ShowDetail(string title, StackPanel body)
    {
        body.Margin = new Thickness(18);
        var window = new Window { Title = title, Width = 420, Height = 600, MinWidth = 320, MinHeight = 300,
            Style = (Style)FindResource("AppWindow"), Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        _detailWindow = window;
        try { window.ShowDialog(); } finally { if (ReferenceEquals(_detailWindow, window)) _detailWindow = null; }
    }
    private static string ContactNames(IReadOnlyList<string> identities, IReadOnlyList<ContactPerson> people) =>
        string.Join(", ", people.Where(p => identities.Contains(p.DisplayIdentity)).DistinctBy(p => p.DisplayIdentity).Select(p => p.Name));
    private void AddMore(StackPanel body, IEnumerable<UIElement> rows, string label)
    {
        var items = rows.ToList(); if (items.Count == 0) return;
        var list = new StackPanel(); foreach (var row in items) list.Children.Add(row);
        body.Children.Add(new Expander { Header = $"{label} · {items.Count}건", Content = list });
    }
    private TextBlock Section(string label, int count) => Text(label + (count > 0 ? $"  {count}" : ""), "SectionTitle");
    private TextBlock Text(string value, string style) => new() { Text = value, Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 3) };
    private Brush Brush(string key) => (Brush)FindResource(key);
    private Border Card(params UIElement[] children)
    {
        var stack = new StackPanel(); foreach (var child in children) stack.Children.Add(child);
        return new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 10, 0, 0), Child = stack };
    }
    private Button Link(string text, Action action)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("LinkButton"), Foreground = Brush("AccentText"),
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 0, 4, 0), Margin = new Thickness(0, 2, 0, 2), MinHeight = 28 };
        button.Click += (_, _) => { if (!_disposed && _active) action(); }; return button;
    }
}
