using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>친구·그룹에 연결한 보관 자료의 읽기 중심 대시보드.</summary>
public sealed class HomePane : UserControl, IDisposable
{
    private readonly ProfileStore _store;
    private readonly Action _openTasks;
    private readonly ComboBox _groups = new(), _friends = new();
    private readonly StackPanel _body = new(), _periods = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _coverage = new(), _scope = new(), _chosen = new(), _groupStatus = new();
    private readonly Button _groupChat = new() { Content = "단체 대화 열기" }, _groupNote = new() { Content = "단체 쪽지 열기" };
    private bool _opening;
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

    public HomePane(ProfileStore store, Action openTasks)
    {
        (_store, _openTasks) = (store, openTasks);
        Background = Brush("PageBg"); Foreground = Brush("TextStrong"); FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(Text("그룹별 소통 정리", "PageTitle"));
        root.Children.Add(Text("최근 쪽지 5 · 최근 대화 3 · 최근 업무 3", "Small"));
        root.Children.Add(GroupActions());
        var filters = new Grid { Margin = new Thickness(0, 10, 0, 8) };
        filters.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _groups.Margin = new Thickness(0, 0, 4, 0); _friends.Margin = new Thickness(4, 0, 0, 0);
        _groups.MinWidth = _friends.MinWidth = 0;
        _groups.ToolTip = "온톡에서 관찰한 친구 그룹"; _friends.ToolTip = "선택한 그룹에서 관찰한 친구";
        Grid.SetColumn(_friends, 1); filters.Children.Add(_groups); filters.Children.Add(_friends); root.Children.Add(filters);
        var periodRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var basis = Text("발송·관찰일 기준", "Caption"); basis.VerticalAlignment = VerticalAlignment.Center;
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
        PopulateFilters(); UpdateGroupActions(); Render();
    }

    public void SetActive(bool active)
    {
        var changed = _active != active; _active = active;
        IsEnabled = active && !_disposed;
        if (active && !_disposed) { if (changed) Render(); _timer.Start(); }
        else { _timer.Stop(); _detailWindow?.Close(); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _active = false; _timer.Stop(); _detailWindow?.Close(); IsEnabled = false; }

    /// <summary>온톡 친구 목록에서 체크한 친구. 그룹 체크는 펼쳐 읽은 구성원만 센다.</summary>
    public IReadOnlyList<ContactPerson> CheckedPeople => _snapshot.People.Where(p => _snapshot.SelectedKeys.Contains(p.Key)).ToList();

    private Border GroupActions()
    {
        var content = new StackPanel();
        content.Children.Add(Text("체크한 친구와 함께", "SectionTitle"));
        _chosen.Style = (Style)FindResource("Small"); _chosen.TextWrapping = TextWrapping.Wrap; content.Children.Add(_chosen);
        var buttons = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        buttons.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _groupChat.Style = (Style)FindResource("PrimaryButton"); _groupChat.Margin = new Thickness(0, 0, 4, 0);
        _groupNote.Margin = new Thickness(4, 0, 0, 0); Grid.SetColumn(_groupNote, 1);
        _groupChat.ToolTip = "온톡에서 체크한 친구를 우클릭해 ‘단체 대화’를 엽니다.";
        _groupNote.ToolTip = "온톡에서 체크한 친구를 우클릭해 ‘단체 쪽지’ 작성 창을 엽니다.";
        _groupChat.Click += async (_, _) => await OpenGroup(GroupOpenKind.Chat);
        _groupNote.Click += async (_, _) => await OpenGroup(GroupOpenKind.Note);
        buttons.Children.Add(_groupChat); buttons.Children.Add(_groupNote); content.Children.Add(buttons);
        _groupStatus.Style = (Style)FindResource("Caption"); _groupStatus.TextWrapping = TextWrapping.Wrap; content.Children.Add(_groupStatus);
        UpdateGroupActions();
        return Card(content);
    }
    private void UpdateGroupActions()
    {
        var people = CheckedPeople;
        _chosen.Text = people.Count == 0
            ? "온톡 친구 목록에서 친구나 그룹을 체크하면 여기에 표시됩니다."
            : $"체크한 친구 {people.Count}명 · " + (people.Count <= 4 ? string.Join(", ", people.Select(p => p.Name))
                : string.Join(", ", people.Take(4).Select(p => p.Name)) + $" 외 {people.Count - 4}명");
        _groupChat.IsEnabled = _groupNote.IsEnabled = people.Count > 0 && !_opening;
    }
    private async Task OpenGroup(GroupOpenKind kind)
    {
        if (_disposed || !_active || _opening || CheckedPeople.Count == 0) return;
        _opening = true; UpdateGroupActions();
        var label = kind == GroupOpenKind.Chat ? "단체 대화" : "단체 쪽지";
        _groupStatus.Text = $"온톡에서 {label}를 여는 중입니다…";
        try
        {
            var status = await Task.Run(() => FriendActions.OpenGroup(kind));
            _groupStatus.Text = status switch
            {
                GroupOpenStatus.Opened => kind == GroupOpenKind.Chat ? "단체 대화창을 열었습니다. 대화 상대를 확인하세요."
                    : "단체 쪽지 작성 창을 열었습니다. 받는 사람을 확인하고 작성하세요.",
                GroupOpenStatus.NotFriends => "온톡 본창의 친구 화면을 찾지 못했습니다. 온톡에서 친구 메뉴를 열어 두세요.",
                GroupOpenStatus.NoSelection => "화면에 보이는 체크한 친구를 찾지 못했습니다. 온톡에서 친구를 체크한 뒤 다시 누르세요.",
                GroupOpenStatus.Covered => "온톡 친구 목록이 가려져 있거나 Ctrl·Shift 키가 눌려 있습니다. 온톡 창을 보이게 한 뒤 다시 누르세요.",
                GroupOpenStatus.MenuNotFound => $"우클릭 메뉴에서 ‘{label}’를 찾지 못했습니다. 온톡에서 체크한 친구를 직접 우클릭해 ‘{label}’를 누르세요.",
                _ => $"‘{label}’를 눌렀지만 새 창을 확인하지 못했습니다. 온톡 화면을 확인하세요.",
            };
        }
        finally { _opening = false; UpdateGroupActions(); }
    }

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
        var history = GroupHistory.Build(people, _store.GetDispatches(500), now, _days);
        var signature = JsonSerializer.Serialize(data) + JsonSerializer.Serialize(history) + HomeDashboard.Day(now);
        if (skipUnchanged && signature == _dataSignature) return;
        _dataSignature = signature;
        _body.Children.Clear();
        var scope = _snapshot.Scopes.FirstOrDefault(s => s.Key == _groupKey);
        _scope.Text = _friendIdentity is not null && people.FirstOrDefault() is { } person
            ? person.Name + " · " + string.Join(" / ", person.Organization)
            : (scope?.Name ?? "전체 그룹") + $" · 확인한 친구 {people.Select(p => p.DisplayIdentity).Distinct().Count()}명" +
              (scope?.DeclaredCount is { } declared ? $" / 그룹 표시 {declared}명" : "");
        _coverage.Text = "온톡에서 관찰한 친구·그룹과 이 계정에 보관한 자료만 표시합니다. 접힌 그룹·화면 밖 친구는 포함하지 않습니다.\n" +
            $"받은 쪽지: 보관일 / 보낸 쪽지: 사이드카 발송 기록 / 대화: 원문 관찰일 기준 · {now.ToOffset(KoreanNoteTime.SeoulOffset):HH:mm} 갱신";
        if (people.Count == 0)
        {
            _body.Children.Add(Card(Text("친구 그룹을 펼쳐 주세요", "SectionTitle"), Text("온톡 Home에서 친구가 표시되면 해당 그룹의 쪽지 발송 이력과 대화 요약을 모아 보여드립니다.", "Small")));
            return;
        }
        RenderNotes(RecentNote.Build(data, history), history, now);
        RenderChats(data, people);
        RenderAttention(data, now);
    }
    private void RenderNotes(IReadOnlyList<RecentNote> notes, GroupHistory history, DateTimeOffset now)
    {
        var content = new StackPanel(); content.Children.Add(Section("최근 쪽지 · 수신/발신", notes.Count));
        if (notes.Count == 0)
        {
            content.Children.Add(Text(_friendIdentity is null ? "이 그룹과 주고받은 쪽지 기록이 없습니다." : "이 친구와 주고받은 쪽지 기록이 없습니다.", "Body"));
            content.Children.Add(Text("받은 쪽지는 보관한 쪽지, 보낸 쪽지는 사이드카로 보낸 기록입니다. 기간을 ‘전체’로 바꿔 이전 기록도 확인하세요.", "Small"));
        }
        else if (history.LastSentAt is { } lastSent)
        {
            var last = lastSent.ToOffset(KoreanNoteTime.SeoulOffset);
            var reached = history.Dispatches.SelectMany(d => d.MatchedNames).Distinct().Count();
            content.Children.Add(Text($"마지막 발송 {ReminderRule.Describe(HomeDashboard.Day(last), now)} {last:HH:mm}" +
                (_friendIdentity is null ? $" · 보낸 사람 {reached}/{history.MemberCount}명" : ""), "Small"));
        }
        foreach (var item in notes.Take(5)) content.Children.Add(NoteRow(item));
        AddMore(content, notes.Skip(5).Select(NoteRow), "이전 쪽지");
        if (history.Dispatches.Count > 0)
            content.Children.Add(Text("보낸 쪽지는 이름으로 맞춘 기록이라 동명이인을 구분하지 못합니다." +
                (history.HasHomonyms ? " 이 범위에 동명이인이 있어 일부 기록이 다른 사람의 것일 수 있습니다." : "") +
                " ‘전송 단추를 누름’은 상대 도착까지 확인한 것이 아닙니다.", "Caption"));
        _body.Children.Add(Card(content));
    }
    private UIElement NoteRow(RecentNote item)
    {
        if (item.Sent is { } sent) return DispatchRow(sent);
        var row = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var at = item.At.ToOffset(KoreanNoteTime.SeoulOffset);
        row.Children.Add(Text("받음 · " + item.Counterpart, "SectionTitle"));
        row.Children.Add(Text(item.Received?.Analysis?.Summary is { Length: > 0 } summary ? summary : Snippet.Make(item.Body, [], 70), "Body"));
        row.Children.Add(Text($"{at:MM.dd HH:mm} · " + (item.Linked ? "친구에 연결한 쪽지" : "발신자 이름 일치 · 연결 전"), "Caption"));
        row.Children.Add(Link("받은 내용 보기 ›", () => ShowReceived(item))); return row;
    }
    private void ShowReceived(RecentNote item)
    {
        var note = (item.Received?.Source ?? item.Candidate)!.Note; var body = new StackPanel();
        body.Children.Add(Text($"{note.SentAtText} · {(item.Linked ? "친구에 연결한 쪽지" : "발신자 이름만 일치한 쪽지")}", "Caption"));
        body.Children.Add(Text("보낸 사람 " + note.SenderName + (note.SenderDetail.Length > 0 ? " · " + note.SenderDetail : ""), "SectionTitle"));
        if (item.Received?.Analysis?.Summary is { Length: > 0 } summary) { body.Children.Add(Text("AI 요약", "SectionTitle")); body.Children.Add(Text(summary, "Body")); }
        body.Children.Add(Text("받은 내용", "SectionTitle")); body.Children.Add(Text(note.Body, "Body"));
        ShowDetail("받은 쪽지 · 보관 원문", body);
    }
    private UIElement DispatchRow(GroupDispatch item)
    {
        var record = item.Record; var row = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var at = record.CreatedAt.ToOffset(KoreanNoteTime.SeoulOffset);
        row.Children.Add(Text("보냄 · " + RecipientLabel(record), "SectionTitle"));
        row.Children.Add(Text(Snippet.Make(record.Body, [], 70), "Body"));
        row.Children.Add(Text($"{at:MM.dd HH:mm} · {StatusLabel(record)}", "Caption"));
        row.Children.Add(Link("보낸 내용 보기 ›", () => ShowDispatch(item))); return row;
    }
    private static string RecipientLabel(DispatchRecord record) => record.RecipientNames.Count <= 3
        ? string.Join(", ", record.RecipientNames) : string.Join(", ", record.RecipientNames.Take(3)) + $" 외 {record.RecipientNames.Count - 3}명";
    private static string StatusLabel(DispatchRecord record) =>
        (record.ReserveAt is { } reserve ? $"{reserve:MM.dd HH:mm} 예약 · " : "") + record.Status switch
        {
            DispatchStatus.WindowClosed => "전송 단추를 누름 · 도착 미확인",
            DispatchStatus.StillOpen => "결과 불명 · 온톡에서 확인 필요",
            _ => "전송 결과 확인 필요",
        };
    private void ShowDispatch(GroupDispatch item)
    {
        var record = item.Record; var body = new StackPanel();
        body.Children.Add(Text($"{record.CreatedAt.ToOffset(KoreanNoteTime.SeoulOffset):yyyy.MM.dd HH:mm} · {StatusLabel(record)}", "Caption"));
        body.Children.Add(Text($"받는 사람 {record.RecipientNames.Count}명", "SectionTitle"));
        body.Children.Add(Text(string.Join(", ", record.RecipientNames.Select(n => item.MatchedNames.Contains(n) ? n : n + "(선택 범위 밖)")), "Body"));
        body.Children.Add(Text("보낸 내용", "SectionTitle")); body.Children.Add(Text(record.Body, "Body"));
        if (record.Detail.Length > 0) body.Children.Add(Text(record.Detail, "Caption"));
        ShowDetail("보낸 쪽지 · 발송 기록", body);
    }
    private void RenderAttention(HomeDashboard data, DateTimeOffset now)
    {
        var content = new StackPanel();
        var top = new DockPanel();
        var all = Link("업무 보기 ›", _openTasks); DockPanel.SetDock(all, Dock.Right); top.Children.Add(all);
        top.Children.Add(Text("최근 업무" + (data.OpenTasks.Count > 0 ? $"  {data.OpenTasks.Count}" : ""), "SectionTitle")); content.Children.Add(top);
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
        var content = new StackPanel(); content.Children.Add(Section("최근 대화", data.Chats.Count));
        if (data.Chats.Count == 0)
        {
            content.Children.Add(Text("최근에 어떤 이야기를 나눴나요?", "Body"));
            content.Children.Add(Text("대화창의 ‘정리’에서 원문을 요약하고 친구에게 연결하면, 다음 연락 때 이어갈 맥락이 여기에 남습니다.", "Small"));
        }
        foreach (var chat in data.Chats.Take(3))
        {
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
            row.Children.Add(Text(ContactNames(chat.ContactIdentities, people), "SectionTitle"));
            row.Children.Add(Text(chat.Analysis.Summary, "Body"));
            row.Children.Add(Text($"AI 요약 · 선택 원문 {chat.Source.Messages.Count}건 · {chat.Source.ObservedAt.ToOffset(KoreanNoteTime.SeoulOffset):MM.dd HH:mm} 관찰", "Caption"));
            foreach (var point in chat.Analysis.Points.Take(1))
                row.Children.Add(Text((point.Kind switch { "decision" => "결정 후보", "question" => "질문 후보", _ => "요청 후보" }) + " · " + point.Title, "Small"));
            row.Children.Add(Link("요약 근거 보기 ›", () => ShowChat(chat, people))); content.Children.Add(row);
        }
        AddMore(content, data.Chats.Skip(3).Select(chat => Link(ContactNames(chat.ContactIdentities, people) + " · " + chat.Source.RoomHint,
            () => ShowChat(chat, people))), "이전 대화 정리");
        _body.Children.Add(Card(content));
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
