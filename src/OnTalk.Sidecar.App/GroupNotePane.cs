using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;
using TemplateRecord = OnTalk.Sidecar.Core.Template;

namespace OnTalk.Sidecar.App;

/// <summary>
/// 쪽지 보내기 작업 창(새 쪽지 · 답장 · 선택 직원에게 쪽지 공용).
/// 개인별 쪽지 창과 같이 작업 단위(① 받는 사람 → ② 본문 → ③ 발송 방식)를 카드로 나누고, 발송 단추는 항상 보이는 아래 줄에 둔다.
/// 본문 오른쪽에는 AI 문장 다듬기 패널(6가지 프리셋 단추)을 두어, 블럭 지정한 문장만 바꾸거나 전체 본문을 다듬을 수 있다.
/// </summary>
public sealed class GroupNotePane : WorkPane
{
    private const string GroupDraftSlot = "organization:group-note";

    private readonly ProfileStore _store;
    private readonly OllamaWriter _writer;
    private readonly IPersonalizedAutomation _automation;
    private readonly ContactPreparation? _preparation;
    private readonly StoredNote? _source;
    private readonly string _draftSlot;
    private readonly List<ContactPerson> _people;
    private readonly HashSet<string> _excluded = [];

    // ① 받는 사람
    private readonly WrapPanel _chips = new();
    private readonly TextBlock _recipientSummary = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _identityWarn = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBox _recipientSearch = new() { MaxLength = 40 };
    private readonly Button _searchRecipientButton;
    private readonly Border _candidateBorder = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6) };
    private readonly StackPanel _candidateList = new();
    private readonly ComboBox _groups = new();
    private readonly TextBox _groupName = new() { MaxLength = 40 };

    // ② 본문 + 오른쪽 AI 문장 다듬기 패널
    private readonly ComboBox _templates = new();
    private readonly TextBox _templateName = new() { MaxLength = 60 };
    private readonly TextBox _editor = new() { Height = 168, IsInactiveSelectionHighlightEnabled = true, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _bytes = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _bodyWarn = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _aiScope = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(2, 5, 2, 0) };
    private readonly List<Button> _aiButtons = [];
    private readonly Button _undoButton;
    private bool _applyingTemplate;
    private bool _aiBusy;
    private long _revision;
    private string? _undoText;
    private int _undoSelectionStart, _undoSelectionLength;
    private long _undoRevision = -1;

    // ③ 발송 방식
    private readonly CheckBox _useReservation = new() { Content = "예약 발송", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly DatePicker _reserveDate = new() { Width = 110, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _reserveHour = new() { Width = 56, Margin = new Thickness(0, 0, 3, 0) };
    private readonly ComboBox _reserveMinute = new() { Width = 56, Margin = new Thickness(0, 0, 3, 0) };
    private readonly StackPanel _reservePickers = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _reserveSummary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 5, 0, 0) };
    private readonly CheckBox _useSms = new() { Content = "SMS 발송", VerticalAlignment = VerticalAlignment.Center };

    // 첨부 파일. 파일 자체는 복사하지 않고 경로만 들고 있다가, 발송 때 온톡의 파일 선택창으로 붙인다.
    private readonly List<string> _attachments = [];
    private readonly StackPanel _attachList = new();
    private readonly TextBlock _attachSummary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    private UIElement? _attachSection;

    // 하단 줄
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 3, Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 5) };
    private readonly ScrollViewer _page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _stopButton, _prepareButton, _sendButton;
    private readonly List<UIElement> _interactive = [];
    private readonly Button _saveDraftButton, _deleteDraftButton;
    private readonly TextBlock _draftInfo = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new Thickness(2, 0, 0, 0) };
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _runCts;
    // 전송 단추를 누른 적이 있으면 이 창에서는 같은 쪽지를 다시 보내지 않는다.
    private bool _attempted;
    private bool _closed;

    public GroupNotePane(
        ProfileStore store,
        OllamaWriter writer,
        ContactPreparation? preparation = null,
        IPersonalizedAutomation? automation = null,
        StoredNote? source = null)
    {
        (_store, _writer, _preparation, _automation, _source) =
            (store, writer, preparation, automation ?? OnTalkPersonalizedAutomation.Instance, source);
        _people = preparation?.People.ToList() ?? [];
        _draftSlot = preparation is not null
            ? GroupDraftSlot
            : source is not null
                ? DraftText.ReplySlot(source.Id)
                : DraftText.NewSlot;

        var pageTitle = preparation is { People.Count: > 0 }
            ? "선택 직원에게 쪽지"
            : source is not null
                ? "답장 쪽지"
                : "새 쪽지";

        Title = pageTitle;

        _stopButton = Btn("중단", (_, _) => { _runCts?.Cancel(); _stopButton!.IsEnabled = false; Status("현재 단계를 마치고 멈춥니다...", "Muted"); }, track: false, style: "DangerButton");
        _stopButton.Visibility = Visibility.Collapsed;
        _prepareButton = Btn("온톡 쪽지창에 준비", (_, _) => Run(send: false),
            tip: "온톡 새 쪽지 창에 받는 사람·본문·예약만 채웁니다. 전송은 온톡 창에서 직접 합니다.");
        _sendButton = Btn("자동 발송", (_, _) => Run(send: true), style: "PrimaryButton",
            tip: "받는 사람·본문·예약을 채우고 검증한 뒤 바로 보냅니다.");
        _saveDraftButton = Btn("임시저장", (_, _) => SaveDraftManually(),
            tip: "지금 작성 중인 본문을 임시저장합니다. 누르지 않으면 저장되지 않으며, 다음에 같은 쪽지 창을 열 때 불러옵니다.");
        _deleteDraftButton = Btn("임시저장 삭제", (_, _) => DeleteSavedDraft(), track: false,
            tip: "저장해 둔 임시저장 쪽지를 지웁니다. 지금 화면의 본문은 그대로 남습니다.");
        _searchRecipientButton = Btn("온톡에서 찾기", OnSearchRecipientAsync, style: "SmallButton",
            tip: "온톡 사용자 검색으로 직급·소속을 확인해 받는 사람에 넣습니다.");
        _undoButton = new Button
        {
            Content = "되돌리기",
            Style = Res<Style>("SmallButton"),
            Focusable = false,
            IsEnabled = false,
            Padding = new Thickness(6, 1, 6, 1),
            MinHeight = 20,
            FontSize = 11,
            ToolTip = "직전 AI 문장 변환 전 상태로 되돌립니다.",
        };
        _undoButton.Click += (_, _) => UndoAi();

        StyleControls();
        var page = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
        page.Children.Add(BuildRecipientSection());
        page.Children.Add(BuildBodySection());
        page.Children.Add(BuildAttachmentSection());
        page.Children.Add(BuildReservationSection());
        _page.Content = page;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_page);
        var bar = BuildActionBar();
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);
        Content = root;

        var savedDraft = store.GetDraft(_draftSlot);
        if (savedDraft is null && _draftSlot == DraftText.NewSlot)
            savedDraft = store.GetDraft(GroupDraftSlot);
        _editor.Text = savedDraft ?? "";
        if (source is not null && _people.Count == 0)
            _recipientSearch.Text = source.Note.SenderName;

        ReloadGroups();
        ReloadTemplates();
        RefreshRecipients();
        UpdateBody();
        UpdateAiScope();
        UpdateReservationState();
        UpdateDraftState();
        Loaded += (_, _) =>
        {
            // 탭을 오갈 때마다 다시 불리므로, 받는 사람 자동 검색은 처음 한 번만 한다.
            var first = !_initialized;
            _initialized = true;
            _editor.Focus(); _editor.CaretIndex = _editor.Text.Length;
            // 답장은 보낸 사람을 받는 사람으로 자동 지정한다(온톡 사용자 검색에서 한 명으로 확정되면 바로 넣는다).
            if (first && source is not null && _people.Count == 0 && _recipientSearch.Text.Length > 0)
                OnSearchRecipientAsync(this, new RoutedEventArgs());
        };
    }

    private bool _initialized;

    internal override bool IsBusy => _runCts is not null;

    internal override void HostClosed() { _closed = true; _closing.Cancel(); _runCts?.Cancel(); }

    private IReadOnlyList<ContactPerson> Chosen => _people.Where(p => !_excluded.Contains(p.SelectionKey)).ToList();

    // ───────────────────────── 화면 구성 ─────────────────────────

    private T Res<T>(string key) => (T)FindResource(key);

    private Button Btn(string title, RoutedEventHandler handler, bool track = true, string? style = null, string? tip = null)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Left };
        if (style is not null) button.Style = Res<Style>(style);
        if (tip is not null) button.ToolTip = tip;
        button.Click += handler;
        if (track) _interactive.Add(button);
        return button;
    }

    private void StyleControls()
    {
        var multiline = Res<Style>("MultilineBox");
        _editor.Style = multiline;
        _editor.Tag = "보낼 쪽지 내용을 입력하세요. 특정 문장만 다듬으려면 마우스로 블럭 지정한 뒤 오른쪽 AI 단추를 누르세요.";
        _editor.FontSize = 13;
        var field = Res<Style>("FieldBox");
        _templateName.Style = field; _templateName.Tag = "서식 제목";
        _recipientSearch.Style = field; _recipientSearch.Tag = "이름으로 받는 사람 찾기 (Enter)";
        _groupName.Style = field; _groupName.Tag = "그룹 이름";
        _templates.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding Name}\" TextTrimming=\"CharacterEllipsis\"/></DataTemplate>");
        _groups.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding Name}\" TextTrimming=\"CharacterEllipsis\"/></DataTemplate>");
        _recipientSummary.Foreground = Res<Brush>("Muted"); _recipientSummary.FontSize = 12;
        _identityWarn.Foreground = Res<Brush>("Warn");
        _bodyWarn.Foreground = Res<Brush>("Danger");
        _bytes.FontSize = 12;
        _status.Style = Res<Style>("StatusLine");
        _progress.Foreground = Res<Brush>("Accent"); _progress.Background = Res<Brush>("Line");
        _candidateBorder.Background = Res<Brush>("SurfaceAlt");
        _candidateBorder.BorderBrush = Res<Brush>("Line");
        _candidateBorder.BorderThickness = new Thickness(1);
        _candidateBorder.Child = _candidateList;
        _editor.AutomationName("쪽지 내용"); _templateName.AutomationName("서식 제목");
        _recipientSearch.AutomationName("받는 사람 이름 검색"); _groupName.AutomationName("그룹 이름");

        foreach (var c in new UIElement[] { _chips, _recipientSearch, _groups, _groupName, _templates, _templateName, _editor, _useReservation, _useSms })
            _interactive.Add(c);
        ForwardWheel(_editor);

        _recipientSearch.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OnSearchRecipientAsync(s, e);
                e.Handled = true;
            }
        };
        _templates.SelectionChanged += (_, _) => ApplySelectedTemplate();
        _editor.TextChanged += (_, _) =>
        {
            _revision++;
            UpdateBody();
            UpdateUndoButton();
        };
        _editor.SelectionChanged += (_, _) => UpdateAiScope();
        _useReservation.Checked += (_, _) => UpdateReservationState();
        _useReservation.Unchecked += (_, _) => UpdateReservationState();
        _reserveDate.SelectedDateChanged += (_, _) => UpdateReservationState();
        _reserveHour.SelectionChanged += (_, _) => UpdateReservationState();
        _reserveMinute.SelectionChanged += (_, _) => UpdateReservationState();

        _reserveHour.ItemsSource = Enumerable.Range(0, 24).ToList();
        _reserveMinute.ItemsSource = Enumerable.Range(0, 60).ToList();
        var next = DateTime.Now.AddHours(1);
        _reserveDate.SelectedDate = next.Date;
        _reserveHour.SelectedItem = next.Hour;
        _reserveMinute.SelectedItem = 0;
    }

    private UIElement BuildRecipientSection()
    {
        var body = new StackPanel();
        body.Children.Add(_chips);
        body.Children.Add(_identityWarn);

        var searchRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchLabel = new TextBlock { Text = "사람 추가", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(searchLabel, 0);
        _recipientSearch.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(_recipientSearch, 1);
        _searchRecipientButton.Margin = new Thickness(0);
        Grid.SetColumn(_searchRecipientButton, 2);
        searchRow.Children.Add(searchLabel);
        searchRow.Children.Add(_recipientSearch);
        searchRow.Children.Add(_searchRecipientButton);
        body.Children.Add(searchRow);
        body.Children.Add(_candidateBorder);

        // 좁은 창에서도 한 줄에 다 들어가도록 고르기와 새 그룹 저장을 두 줄로 나눈다.
        var groupRow = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        groupRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        groupRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        groupRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        groupRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        groupRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var groupLabel = new TextBlock { Text = "수신 그룹", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(groupLabel, 0);
        _groups.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(_groups, 1);
        var groupButtonsLeft = new StackPanel { Orientation = Orientation.Horizontal };
        groupButtonsLeft.Children.Add(Btn("그룹 넣기", (_, _) => AddSelectedGroup(), style: "SmallButton", tip: "선택한 그룹의 구성원을 받는 사람에 추가합니다."));
        groupButtonsLeft.Children.Add(Btn("삭제", (_, _) => DeleteSelectedGroup(), style: "SmallButton", tip: "선택한 수신 그룹을 지웁니다."));
        Grid.SetColumn(groupButtonsLeft, 2);
        _groupName.Margin = new Thickness(0, 5, 6, 0);
        Grid.SetRow(_groupName, 1); Grid.SetColumn(_groupName, 1);
        var saveGroupBtn = Btn("그룹 저장", (_, _) => SaveCurrentGroup(), style: "SmallButton", tip: "지금 선택된 받는 사람들을 이 그룹 이름으로 저장합니다.");
        saveGroupBtn.Margin = new Thickness(0, 5, 0, 0);
        Grid.SetRow(saveGroupBtn, 1); Grid.SetColumn(saveGroupBtn, 2);
        groupRow.Children.Add(groupLabel);
        groupRow.Children.Add(_groups);
        groupRow.Children.Add(groupButtonsLeft);
        groupRow.Children.Add(_groupName);
        groupRow.Children.Add(saveGroupBtn);
        body.Children.Add(groupRow);

        var helpLines = new List<string>
        {
            "온톡 새 쪽지 창에서 이름으로 찾아 직급·소속이 일치하는 한 사람만 받는 사람에 넣습니다.",
            "한 사람이라도 확정하지 못하면 보내지 않고 멈춥니다. 그 대상을 ✕로 빼거나 온톡 창에서 직접 고르세요.",
        };
        if (_preparation is not null)
        {
            helpLines.Add($"{_preparation.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm} 온톡 화면 기준 정보입니다. {ContactSupport.IdentityWarning(_people)}");
        }
        var help = WorkUi.HelpButton(this, "받는 사람 안내", helpLines);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_recipientSummary); right.Children.Add(help);
        return WorkUi.Section(this, "1", "받는 사람", "", body, right);
    }

    private void RefreshRecipients()
    {
        _chips.Children.Clear();
        if (_people.Count == 0)
        {
            _chips.Children.Add(new TextBlock
            {
                Text = "아직 받는 사람이 없습니다. 아래에서 이름을 검색해 추가하거나 수신 그룹을 넣으세요.",
                Foreground = Res<Brush>("Muted"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 3),
            });
        }
        // 이름·직급·소속이 같은 대상은 빨강, 이름만 같은 동명이인은 주황으로 칩을 물들여 어느 사람인지 바로 보이게 한다.
        var active = Chosen;
        var sameIdentity = active.GroupBy(p => p.DisplayIdentity).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var sameName = active.Where(p => !sameIdentity.Contains(p.DisplayIdentity))
            .GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var person in _people)
        {
            var excluded = _excluded.Contains(person.SelectionKey);
            var dupGroup = !excluded ? sameIdentity.IndexOf(person.DisplayIdentity) : -1;
            var isDuplicate = dupGroup >= 0;
            var isNamesake = !excluded && !isDuplicate && sameName.Contains(person.Name);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var titlePart = person.Title.Length > 0 ? $" · {person.Title}" : "";
            var tag = isDuplicate ? (sameIdentity.Count > 1 ? $" · 중복{dupGroup + 1}" : " · 중복") : isNamesake ? " · 동명" : "";
            var label = new TextBlock
            {
                Text = $"{person.Name}{titlePart}{tag}", VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
                TextDecorations = excluded ? TextDecorations.Strikethrough : null,
                            };
            if (isDuplicate) label.Foreground = Res<Brush>("Danger");
            else if (isNamesake) label.Foreground = Res<Brush>("Warn");
            row.Children.Add(label);
            var toggle = new Button
            {
                Content = excluded ? "" : "", Style = Res<Style>("IconButton"), Width = 17, Height = 17, FontSize = 8,
                Margin = new Thickness(3, 0, -3, 0), ToolTip = excluded ? "다시 넣기" : "빼기", VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.AutomationName((excluded ? "다시 넣기 " : "빼기 ") + person.Name);
            toggle.Click += (_, _) => { if (!_excluded.Remove(person.SelectionKey)) _excluded.Add(person.SelectionKey); RefreshRecipients(); };
            row.Children.Add(toggle);
            _chips.Children.Add(new Border
            {
                Child = row, Padding = new Thickness(8, 2, 4, 2), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(11),
                Background = Res<Brush>(isDuplicate ? "DangerSoft" : isNamesake ? "WarnSoft" : excluded ? "Surface" : "SurfaceAlt"),
                BorderBrush = Res<Brush>(isDuplicate ? "Danger" : isNamesake ? "WarnLine" : "Line"),
                BorderThickness = new Thickness(isDuplicate ? 1.5 : 1),
                Opacity = excluded ? 0.55 : 1,
                ToolTip = person.Label + (excluded ? "\n(이번 쪽지에서 뺌)" : isDuplicate ? "\n(이름·직급·소속이 같은 대상 — 하나만 남기세요)" : isNamesake ? "\n(동명이인 — 직급·소속을 확인하세요)" : ""),
            });
        }
        var chosen = active;
        _recipientSummary.Text = _excluded.Count == 0 ? $"{chosen.Count}명" : $"{chosen.Count}명 · 뺀 사람 {_excluded.Count}명";
        _identityWarn.Text = sameIdentity.Count > 0
            ? "이름·직급·소속이 같은 대상이 있습니다. 빨갛게 표시된 칩 중 하나만 남기세요."
            : sameName.Count > 0 ? "동명이인이 있습니다. 주황색으로 표시된 칩의 직급·소속을 확인하세요." : "";
        _identityWarn.Foreground = Res<Brush>(sameIdentity.Count > 0 ? "Danger" : "Warn");
        
        _identityWarn.Visibility = _identityWarn.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private static ContactPerson ToContactPerson(Recipient r) =>
        new($"recipient:{r.Name}|{r.Title}|{string.Join("|", r.Details)}", r.Name, r.Title, r.Details);

    private void AddPerson(ContactPerson person)
    {
        var existing = _people.FirstOrDefault(p => p.DisplayIdentity == person.DisplayIdentity);
        if (existing is not null)
        {
            _excluded.Remove(existing.SelectionKey);
        }
        else
        {
            _people.Add(person);
        }
        RefreshRecipients();
    }

    private async void OnSearchRecipientAsync(object sender, RoutedEventArgs e)
    {
        if (_runCts is not null || _closed) return;
        var query = _recipientSearch.Text.Trim();
        if (query.Length == 0)
        {
            Status("찾을 받는 사람 이름을 입력하세요.", "Warn");
            _recipientSearch.Focus();
            return;
        }
        _searchRecipientButton.IsEnabled = false;
        _candidateBorder.Visibility = Visibility.Collapsed;
        Status($"온톡에서 ‘{query}’ 이름을 찾는 중...", "Muted");
        try
        {
            var found = await Task.Run<(ActionStatus Status, IntPtr SearchWindow, IReadOnlyList<PersonRow> Rows)>(() =>
            {
                // 온톡 검색은 새 쪽지 창의 '추가'로만 열린다. 이미 열려 있던 빈 창이 아니라 검색하려고 우리가 연 창은
                // 결과를 받은 뒤 닫아, 이 작업 창만 남게 한다.
                var preexisting = _automation.FindEmptyNewComposeWindow();
                var (ensure, hwnd) = _automation.EnsureEmptyNewComposeWindow();
                if (ensure != ActionStatus.Ok || hwnd == IntPtr.Zero)
                    return (ActionStatus.NotCompose, IntPtr.Zero, []);
                var openedByUs = preexisting == IntPtr.Zero;
                try
                {
                    var res = _automation.SearchRecipient(hwnd, query);
                    if (res.SearchWindow != IntPtr.Zero)
                        _automation.CloseUserSearchWindow(res.SearchWindow);
                    return (res.Status, res.SearchWindow, res.Rows);
                }
                finally
                {
                    if (openedByUs) _automation.CloseEmptyComposeWindow(hwnd);
                }
            });
            if (_closed) return;
            BringBack();
            if (found.Status != ActionStatus.Ok)
            {
                Status("온톡에서 사용자 검색을 실행하지 못했습니다. 온톡 본창이 열려 있는지 확인하세요.", "Warn");
                return;
            }
            if (found.Rows.Count == 0)
            {
                Status($"‘{query}’ 이름으로 검색된 사람이 없습니다.", "Warn");
                return;
            }
            var rows = found.Rows;
            if (rows.Count > 1 && _source is not null && query == _source.Note.SenderName)
            {
                // 답장: 받은 쪽지 머리글에 보인 직급·소속(잘려 있을 수 있음)으로 동명이인을 좁힌다.
                var hint = _source.Note.SenderDetail.Trim().TrimEnd('…', '.').Trim();
                if (hint.Length > 0)
                {
                    var narrowed = rows.Where(r => $"{r.Title} {string.Join(" ", r.Details)}".Contains(hint)).ToList();
                    if (narrowed.Count == 1) rows = narrowed;
                }
            }
            if (rows.Count == 1)
            {
                var person = ToContactPerson(rows[0].ToRecipient());
                AddPerson(person);
                _recipientSearch.Clear();
                Status($"‘{person.Label}’ 님을 받는 사람에 추가했습니다.");
                return;
            }
            ShowCandidatePicker(query, rows);
        }
        finally
        {
            if (!_closed) _searchRecipientButton.IsEnabled = _runCts is null;
        }
    }

    /// <summary>온톡 창을 오가는 조작이 끝나면 작업 중이던 이 창을 다시 앞으로 가져온다.</summary>
    private void BringBack()
    {
        if (_closed) return;
        BringHostBack();
        _recipientSearch.Focus();
    }

    private void ShowCandidatePicker(string query, IReadOnlyList<PersonRow> rows)
    {
        _candidateList.Children.Clear();
        _candidateList.Children.Add(new TextBlock
        {
            Text = $"‘{query}’ 검색 결과 {rows.Count}명 · 추가할 사람을 고르세요",
           
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
        });
        var checks = new List<(CheckBox Box, ContactPerson Person)>();
        foreach (var row in rows)
        {
            var person = ToContactPerson(row.ToRecipient());
            var box = new CheckBox
            {
                Content = person.Label,
                Margin = new Thickness(0, 1, 0, 1),
                IsChecked = rows.Count == 1,
            };
            checks.Add((box, person));
            _candidateList.Children.Add(box);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(Btn("선택 추가", (_, _) =>
        {
            var picked = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Person).ToList();
            if (picked.Count == 0) { Status("추가할 사람을 체크하세요.", "Warn"); return; }
            foreach (var p in picked) AddPerson(p);
            _candidateBorder.Visibility = Visibility.Collapsed;
            _recipientSearch.Clear();
            Status($"{picked.Count}명을 받는 사람에 추가했습니다.");
        }, track: false, style: "SmallButton"));
        actions.Children.Add(Btn("닫기", (_, _) => _candidateBorder.Visibility = Visibility.Collapsed, track: false, style: "SmallButton"));
        _candidateList.Children.Add(actions);
        _candidateBorder.Visibility = Visibility.Visible;
        Status($"‘{query}’ 검색 결과가 {rows.Count}명입니다. 직급·소속을 확인해 고르세요.");
    }

    private void ReloadGroups(string? select = null)
    {
        var list = _store.GetGroups();
        _groups.ItemsSource = list;
        _groups.SelectedItem = select is null ? list.FirstOrDefault() : list.FirstOrDefault(g => g.Name == select);
    }

    private void AddSelectedGroup()
    {
        if (_groups.SelectedItem is not RecipientGroup group)
        {
            Status("불러올 수신 그룹을 먼저 고르세요.", "Warn");
            return;
        }
        foreach (var member in group.Members)
            AddPerson(ToContactPerson(member));
        _groupName.Text = group.Name;
        Status($"‘{group.Name}’ 그룹({group.Members.Count}명)을 받는 사람에 넣었습니다.");
    }

    private void SaveCurrentGroup()
    {
        var name = _groupName.Text.Trim();
        if (name.Length == 0)
        {
            Status("저장할 그룹 이름을 입력하세요.", "Warn");
            _groupName.Focus();
            return;
        }
        var chosen = Chosen;
        if (chosen.Count == 0)
        {
            Status("그룹으로 저장할 받는 사람이 없습니다.", "Warn");
            return;
        }
        var members = chosen.Select(p => new Recipient(p.Name, p.Title, p.Organization)).ToList();
        _store.SaveGroup(name, members);
        ReloadGroups(name);
        Status($"‘{name}’ 그룹({members.Count}명)을 저장했습니다.");
    }

    private void DeleteSelectedGroup()
    {
        if (_groups.SelectedItem is not RecipientGroup group)
        {
            Status("삭제할 수신 그룹을 먼저 고르세요.", "Warn");
            return;
        }
        if (Dialogs.Show(Host, $"‘{group.Name}’ 수신 그룹을 삭제할까요?", "그룹 삭제", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return;
        _store.DeleteGroup(group.Id);
        ReloadGroups();
        Status($"‘{group.Name}’ 그룹을 삭제했습니다.");
    }

    private UIElement BuildBodySection()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Place(UIElement el, int row, int col, int span = 1, Thickness? margin = null)
        {
            Grid.SetRow(el, row); Grid.SetColumn(el, col); Grid.SetColumnSpan(el, span);
            if (el is FrameworkElement fe) fe.Margin = margin ?? new Thickness(0, 0, 0, 5);
            grid.Children.Add(el);
        }
        TextBlock Label(string text) => new() { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

        Place(Label("저장된 서식"), 0, 0, margin: new Thickness(0, 0, 8, 5));
        Place(_templates, 0, 1, margin: new Thickness(0, 0, 6, 5));
        Place(Btn("서식 삭제", (_, _) => DeleteSelectedTemplate(), style: "SmallButton", tip: "고른 저장 서식을 목록에서 지웁니다."), 0, 2);
        Place(Label("서식 제목"), 1, 0, margin: new Thickness(0, 0, 8, 5));
        Place(_templateName, 1, 1, margin: new Thickness(0, 0, 6, 5));
        Place(Btn("서식 저장", (_, _) => SaveTemplate(), style: "SmallButton", tip: "지금 내용을 이 제목의 서식으로 저장합니다. 같은 제목이 있으면 바꿉니다."), 1, 2);

        var body = new StackPanel();
        body.Children.Add(grid);

        // 본문(왼쪽)과 AI 문장 다듬기 패널(오른쪽)을 나란히 배치한다.
        var compare = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        compare.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        compare.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
        compare.Children.Add(_editor);

        var aiPanel = BuildAiPanel();
        Grid.SetColumn(aiPanel, 1);
        compare.Children.Add(aiPanel);

        body.Children.Add(compare);
        body.Children.Add(_bodyWarn);
        return WorkUi.Section(this, "2", "본문", "", body, _bytes);
    }

    private Border BuildAiPanel()
    {
        var stack = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(_undoButton, Dock.Right);
        header.Children.Add(_undoButton);
        header.Children.Add(new TextBlock
        {
            Text = "✦ AI 문장 다듬기",
           
            FontSize = 11,
            Foreground = Res<Brush>("AccentText"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(header);

        var buttonsGrid = new UniformGrid { Columns = 2, Rows = 3, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var preset in OllamaWriter.RefinePresets)
        {
            var btn = new Button
            {
                Content = preset.Label,
                Style = Res<Style>("SmallButton"),
                Focusable = false,
                Margin = new Thickness(1.5),
                Padding = new Thickness(3, 0, 3, 0),
                MinHeight = 26,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                ToolTip = preset.Description,
            };
            btn.AutomationName("AI " + preset.Label);
            btn.Click += (_, _) => OnAiRefineAsync(preset);
            _aiButtons.Add(btn);
            buttonsGrid.Children.Add(btn);
        }
        stack.Children.Add(buttonsGrid);
        stack.Children.Add(_aiScope);

        return new Border
        {
            Margin = new Thickness(8, 0, 0, 0),
            Background = Res<Brush>("AccentSoft"),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#CFDBFF")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7),
            Child = stack,
        };
    }

    private UIElement BuildReservationSection()
    {
        _reservePickers.Children.Add(_reserveDate);
        _reservePickers.Children.Add(_reserveHour);
        _reservePickers.Children.Add(new TextBlock { Text = "시", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _reservePickers.Children.Add(_reserveMinute);
        _reservePickers.Children.Add(new TextBlock { Text = "분", VerticalAlignment = VerticalAlignment.Center });
        var body = new StackPanel();
        var row = new WrapPanel();
        row.Children.Add(_useReservation);
        row.Children.Add(_reservePickers);
        body.Children.Add(row);
        body.Children.Add(_reserveSummary);
        _useSms.Margin = new Thickness(0, 6, 0, 0);
        _useSms.ToolTip = "온톡 쪽지 창의 ‘SMS 발송’ 확인란을 선택한 상태로 보냅니다.";
        body.Children.Add(_useSms);
        return WorkUi.Section(this, "4", "발송 방식", "", body);
    }

    // ───────────────────────── 첨부 파일 ─────────────────────────

    private UIElement BuildAttachmentSection()
    {
        var body = new StackPanel();
        var bar = new WrapPanel();
        bar.Children.Add(Btn("파일 추가…", (_, _) => OnPickAttachments(), style: "SmallButton",
            tip: $"붙일 파일을 고릅니다. 파일을 이 영역에 끌어다 놓아도 됩니다. 파일당 최대 20MB, 최대 {NoteAttachments.MaxCount}개, exe 파일은 붙일 수 없습니다."));
        body.Children.Add(bar);
        body.Children.Add(_attachList);
        body.Children.Add(_attachSummary);
        var section = WorkUi.Section(this, "3", "첨부 파일", "", body);
        section.AllowDrop = true;
        section.DragOver += (_, e) =>
        {
            e.Effects = _runCts is null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        section.Drop += (_, e) =>
        {
            if (_runCts is null && e.Data.GetData(DataFormats.FileDrop) is string[] dropped) AddAttachments(dropped);
            e.Handled = true;
        };
        _attachSection = section;
        RenderAttachments();
        return section;
    }

    private void OnPickAttachments()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "첨부할 파일 선택", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(Host) == true) AddAttachments(dialog.FileNames);
    }

    private void AddAttachments(IEnumerable<string> files)
    {
        var added = 0;
        foreach (var path in files)
        {
            if (Directory.Exists(path)) { Status($"폴더는 붙일 수 없습니다: {Path.GetFileName(path)}", "Warn"); continue; }
            if (_attachments.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            // 이름·형식·크기·개수는 발송 때와 같은 규칙으로 지금 확인한다.
            if (NoteAttachments.Validate([.. _attachments, path]) is { } problem) { Status(problem, "Warn"); continue; }
            _attachments.Add(path);
            added++;
        }
        RenderAttachments();
        UpdateButtons();
        if (added > 0) Status($"첨부 {added}개를 추가했습니다. 발송 때 온톡 쪽지 창에 붙입니다.", "Muted");
    }

    private void RenderAttachments()
    {
        _attachList.Children.Clear();
        foreach (var path in _attachments.ToList())
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var size = new FileInfo(path) is { Exists: true } info ? NoteAttachments.Size(info.Length) : "파일 없음";
            row.Children.Add(new TextBlock { Text = "📎 " + Path.GetFileName(path), ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var sizeText = new TextBlock { Text = size, Foreground = Res<Brush>("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
            Grid.SetColumn(sizeText, 1); row.Children.Add(sizeText);
            var remove = new Button { Content = "✕", Style = Res<Style>("SmallButton"), ToolTip = "첨부에서 뺍니다(원본 파일은 그대로)." };
            remove.AutomationName("첨부 제거 " + Path.GetFileName(path));
            remove.Click += (_, _) => { _attachments.Remove(path); RenderAttachments(); UpdateButtons(); };
            Grid.SetColumn(remove, 2); row.Children.Add(remove);
            _attachList.Children.Add(row);
        }
        var total = _attachments.Sum(p => new FileInfo(p) is { Exists: true } f ? f.Length : 0);
        _attachSummary.Text = _attachments.Count == 0
            ? "붙인 파일이 없습니다. 파일당 최대 20MB, 최대 10개, exe 파일은 붙일 수 없습니다."
            : $"첨부 {_attachments.Count}개 · 합계 {NoteAttachments.Size(total)}" +
              (total > NoteAttachments.MaxFileBytes ? " · 합계가 20MB를 넘으면 온톡이 거절할 수 있습니다." : "");
        _attachSummary.Foreground = Res<Brush>(total > NoteAttachments.MaxFileBytes ? "Warn" : "Muted");
    }

    private UIElement BuildActionBar()
    {
        var bar = new StackPanel { Margin = new Thickness(12, 7, 12, 8) };
        bar.Children.Add(_progress);
        bar.Children.Add(new ScrollViewer { MaxHeight = 52, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 5), Content = _status });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_stopButton);
        buttons.Children.Add(_prepareButton);
        _sendButton.Margin = new Thickness(0);
        buttons.Children.Add(_sendButton);

        _draftInfo.Foreground = Res<Brush>("Muted");
        var draftButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        draftButtons.Children.Add(_saveDraftButton);
        draftButtons.Children.Add(_deleteDraftButton);
        draftButtons.Children.Add(_draftInfo);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(draftButtons, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(draftButtons);
        row.Children.Add(buttons);
        bar.Children.Add(row);
        return new Border { Background = Res<Brush>("Surface"), BorderBrush = Res<Brush>("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
    }

    /// <summary>안쪽 스크롤이 끝에 닿았거나 필요 없으면 휠을 바깥 페이지로 넘긴다.</summary>
    private void ForwardWheel(UIElement element) =>
        element.PreviewMouseWheel += (_, e) =>
        {
            if (FindScroll(element) is { ScrollableHeight: > 0 } inner &&
                ((e.Delta > 0 && inner.VerticalOffset > 0) || (e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight)))
                return;
            e.Handled = true;
            _page.ScrollToVerticalOffset(_page.VerticalOffset - e.Delta);
        };

    private static ScrollViewer? FindScroll(DependencyObject node)
    {
        if (node is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindScroll(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    private void Status(string text, string brush = "AccentText")
    {
        _status.Text = text;
        _status.Foreground = Res<Brush>(brush);
    }

    // ───────────────────────── 본문·서식·AI 다듬기 ─────────────────────────

    private string Body => _editor.Text.Replace("\r\n", "\n");

    /// <summary>보낼 수 없는 본문이면 이유를 돌려준다.</summary>
    private string? BodyProblem()
    {
        var body = Body;
        if (body.Trim().Length == 0) return "본문을 입력하세요.";
        if (DraftText.UnfilledPlaceholders(body) is { Count: > 0 } unfilled) return $"채우지 않은 자리: {string.Join(" ", unfilled)}";
        return NoteBytes.Count(body) > NoteBytes.Limit ? $"{NoteBytes.Limit:N0}바이트를 넘습니다." : null;
    }

    private void UpdateBody()
    {
        var bytes = NoteBytes.Count(Body);
        _bytes.Text = $"{bytes:N0} / {NoteBytes.Limit:N0} Bytes";
        _bytes.Foreground = Res<Brush>(bytes > NoteBytes.Limit ? "Danger" : "Muted");
        // 빈 본문은 아직 쓰는 중이므로 경고하지 않는다.
        var problem = Body.Trim().Length == 0 ? null : BodyProblem();
        _bodyWarn.Text = problem ?? "";
        _bodyWarn.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateAiScope();
        UpdateButtons();
    }

    private void UpdateAiScope()
    {
        if (DraftText.TryTrimSelection(_editor.Text, _editor.SelectionStart, _editor.SelectionLength, out _, out var len, out _))
        {
            _aiScope.Text = $"선택 구간만 변환 ({len}자)";
            _aiScope.Foreground = Res<Brush>("AccentText");
        }
        else
        {
            _aiScope.Text = "블럭 지정 시 그 구간만, 없으면 전체 변환";
            _aiScope.Foreground = Res<Brush>("Muted");
        }
    }

    private void UpdateUndoButton() =>
        _undoButton.IsEnabled = !_aiBusy && _runCts is null && _undoText is not null && _undoRevision == _revision;

    // 임시저장은 사용자가 '임시저장' 단추를 누를 때만 한다(자동 저장 없음).
    private void SaveDraftManually()
    {
        if (_editor.Text.Length == 0) { Status("임시저장할 본문이 없습니다.", "Warn"); return; }
        _store.SaveDraft(_draftSlot, _editor.Text, DateTimeOffset.Now);
        UpdateDraftState();
        Status("임시저장했습니다.", "Success");
    }

    private void DeleteSavedDraft()
    {
        _store.DeleteDraft(_draftSlot);
        if (_draftSlot == DraftText.NewSlot) _store.DeleteDraft(GroupDraftSlot);
        UpdateDraftState();
        Status("임시저장한 쪽지를 삭제했습니다.", "Muted");
    }

    private void UpdateDraftState()
    {
        var saved = _store.GetStoredDraft(_draftSlot)
            ?? (_draftSlot == DraftText.NewSlot ? _store.GetStoredDraft(GroupDraftSlot) : null);
        _deleteDraftButton.IsEnabled = saved is not null && _runCts is null;
        _draftInfo.Text = saved is null ? "임시저장 없음" : $"임시저장 {saved.UpdatedAt.LocalDateTime:MM-dd HH:mm}";
    }

    private void ReloadTemplates(string? select = null)
    {
        _applyingTemplate = true;
        try
        {
            var list = _store.GetTemplates();
            _templates.ItemsSource = list;
            _templates.SelectedItem = select is null ? null : list.FirstOrDefault(t => t.Name == select);
        }
        finally { _applyingTemplate = false; }
    }

    private void ApplySelectedTemplate()
    {
        if (_applyingTemplate || _templates.SelectedItem is not TemplateRecord chosen) return;
        if (_editor.Text.Trim().Length > 0 && _editor.Text != chosen.Body &&
            Dialogs.Show(Host, $"지금 본문을 ‘{chosen.Name}’ 서식으로 바꿀까요?", "서식 불러오기", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
        {
            ReloadTemplates(_templateName.Text.Trim());
            return;
        }
        // 같은 쪽지를 모두가 받으므로 사람마다 바뀌는 {변수}는 쓰지 않는다. 개인별 서식에서 온 변수는 지운다.
        var body = chosen.Body;
        var variables = NotePersonalization.ExtractVariables(body);
        foreach (var variable in variables) body = NotePersonalization.RemoveVariable(body, variable);
        _templateName.Text = chosen.Name;
        _editor.Text = body;
        Status(variables.Count == 0
            ? $"‘{chosen.Name}’ 서식을 불러왔습니다."
            : $"‘{chosen.Name}’ 서식을 불러왔습니다. 개인별 변수 {variables.Count}개는 뺐으니 본문을 확인하세요.");
    }

    private void SaveTemplate()
    {
        var name = _templateName.Text.Trim();
        if (name.Length == 0) { Status("서식 제목을 입력하세요.", "Warn"); _templateName.Focus(); return; }
        if (_editor.Text.Trim().Length == 0) { Status("본문이 비어 있어 저장하지 않았습니다.", "Warn"); _editor.Focus(); return; }
        var existing = _store.GetTemplates().FirstOrDefault(t => t.Name == name);
        if (existing is not null && existing.Body != _editor.Text &&
            Dialogs.Show(Host, $"‘{name}’ 서식이 이미 있습니다. 지금 본문으로 바꿀까요?", "서식 덮어쓰기", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _store.SaveTemplate(name, _editor.Text);
        ReloadTemplates(name);
        Status(existing is null ? $"‘{name}’ 서식을 저장했습니다." : $"‘{name}’ 서식을 바꿔 저장했습니다.");
    }

    private void DeleteSelectedTemplate()
    {
        if (_templates.SelectedItem is not TemplateRecord chosen) { Status("삭제할 서식을 먼저 고르세요.", "Warn"); return; }
        if (Dialogs.Show(Host, $"‘{chosen.Name}’ 서식을 삭제할까요?", "서식 삭제", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _store.DeleteTemplate(chosen.Id);
        ReloadTemplates();
        Status($"‘{chosen.Name}’ 서식을 삭제했습니다.");
    }

    private async void OnAiRefineAsync(AiRefinePreset preset)
    {
        if (_aiBusy || _runCts is not null || _closed) return;
        var snapshot = _editor.Text;
        if (snapshot.Trim().Length == 0)
        {
            Status("다듬을 쪽지 본문을 먼저 입력하세요.", "Warn");
            _editor.Focus();
            return;
        }

        var selStart = _editor.SelectionStart;
        var selLength = _editor.SelectionLength;
        var isBlock = DraftText.TryTrimSelection(snapshot, selStart, selLength, out var trimmedStart, out var trimmedLength, out var selectedBlock);

        _aiBusy = true;
        foreach (var b in _aiButtons) b.IsEnabled = false;
        UpdateUndoButton();
        Status(isBlock
            ? $"로컬 AI가 선택한 구간({trimmedLength}자)을 ‘{preset.Label}’ 방식으로 다듬는 중입니다..."
            : $"로컬 AI가 전체 본문을 ‘{preset.Label}’ 방식으로 다듬는 중입니다...", "Muted");

        var revision = _revision;
        try
        {
            var refined = await _writer.RefineAsync(snapshot, isBlock ? selectedBlock : null, preset.Instruction, _closing.Token);
            if (_closed) return;
            if (_revision != revision)
            {
                Status("기다리는 사이 본문이 바뀌어 AI 변환 결과를 반영하지 않았습니다.", "Warn");
                return;
            }
            if (string.IsNullOrWhiteSpace(refined))
            {
                Status("AI로부터 빈 응답을 받아 본문을 유지했습니다.", "Warn");
                return;
            }

            _undoText = snapshot;
            _undoSelectionStart = isBlock ? trimmedStart : 0;
            _undoSelectionLength = isBlock ? trimmedLength : 0;

            var applied = DraftText.ApplyRefinement(snapshot, selStart, selLength, refined);
            _editor.Text = applied.Text;
            _undoRevision = _revision;
            UpdateUndoButton();

            _editor.Focus();
            if (applied.IsBlock)
            {
                _editor.Select(applied.SelectionStart, applied.SelectionLength);
                Status($"선택한 문장을 ‘{preset.Label}’ 방식으로 바꿨습니다. 마음에 들지 않으면 ‘되돌리기’를 누르세요.");
            }
            else
            {
                _editor.CaretIndex = _editor.Text.Length;
                Status($"전체 본문을 ‘{preset.Label}’ 방식으로 다듬었습니다. 사실·날짜를 확인하세요.");
            }
        }
        catch (OperationCanceledException) { }
        catch (AnalysisUnavailableException ex) { if (!_closed) Status(ex.Message, "Warn"); }
        finally
        {
            _aiBusy = false;
            if (!_closed)
            {
                var enabled = _runCts is null;
                foreach (var b in _aiButtons) b.IsEnabled = enabled;
                UpdateUndoButton();
            }
        }
    }

    private void UndoAi()
    {
        if (_undoText is null || _undoRevision != _revision) return;
        var restoreText = _undoText;
        var restoreStart = _undoSelectionStart;
        var restoreLength = _undoSelectionLength;
        _undoText = null;
        _undoRevision = -1;
        _editor.Text = restoreText;
        _editor.Focus();
        if (restoreLength > 0 && restoreStart + restoreLength <= _editor.Text.Length)
            _editor.Select(restoreStart, restoreLength);
        else
            _editor.CaretIndex = _editor.Text.Length;
        UpdateUndoButton();
        Status("AI 변환 전 문장으로 되돌렸습니다.");
    }

    // ───────────────────────── 예약 ─────────────────────────

    private void UpdateReservationState()
    {
        var on = _useReservation.IsChecked == true;
        _reservePickers.IsEnabled = on && _runCts is null;
        _reserveSummary.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        if (TryGetReservation(out var at, out var error) && at is { } when)
        {
            _reserveSummary.Text = $"{when:yyyy년 M월 d일 HH:mm} 예약";
            _reserveSummary.Foreground = Res<Brush>("Muted");
        }
        else
        {
            _reserveSummary.Text = error;
            _reserveSummary.Foreground = Res<Brush>("Warn");
        }
    }

    private bool TryGetReservation(out DateTime? reservation, out string error)
    {
        reservation = null; error = "";
        if (_useReservation.IsChecked != true) return true;
        if (_reserveDate.SelectedDate is not { } date || _reserveHour.SelectedItem is not int hour || _reserveMinute.SelectedItem is not int minute)
        {
            error = "예약 날짜와 시각을 모두 고르세요.";
            return false;
        }
        var when = date.Date.AddHours(hour).AddMinutes(minute);
        if (when <= DateTime.Now.AddMinutes(2))
        {
            error = "예약 시각은 지금보다 3분 이후여야 합니다.";
            return false;
        }
        reservation = when;
        return true;
    }

    // ───────────────────────── 발송 ─────────────────────────

    private void UpdateButtons()
    {
        if (_runCts is not null) return;
        var ready = Chosen.Count > 0 && Body.Trim().Length > 0;
        _prepareButton.IsEnabled = ready;
        _sendButton.IsEnabled = ready && !_attempted;
    }

    private async void Run(bool send)
    {
        if (_runCts is not null || _closed) return;
        var chosen = Chosen;
        if (chosen.Count == 0) { Status("받는 사람이 없습니다.", "Warn"); return; }
        if (BodyProblem() is { } problem) { Status(problem, "Warn"); _editor.Focus(); return; }
        if (!TryGetReservation(out var reservation, out var reserveError)) { Status(reserveError, "Warn"); return; }
        if (send && _attempted) { Status("이 창에서 이미 발송을 시도했습니다. 온톡 보낸 쪽지함을 확인하세요.", "Warn"); return; }
        var attachments = _attachments.ToList();
        var sms = _useSms.IsChecked == true;
        if (NoteAttachments.Validate(attachments) is { } attachProblem) { Status(attachProblem, "Warn"); return; }
        if (send && !ConfirmSend(chosen, reservation, attachments, sms)) { Status("발송을 취소했습니다.", "Muted"); return; }

        using var cts = new CancellationTokenSource();
        _runCts = cts;
        SetRunning(true);
        _progress.Maximum = chosen.Count + 1; _progress.Value = 0;
        try
        {
            var outcome = await GroupNoteDispatcher.RunAsync(chosen, Body, reservation, send, _store, _automation,
                (done, total, text) => Dispatcher.InvokeAsync(() =>
                {
                    if (_closed) return;
                    _progress.Value = done;
                    Status(text, "Muted");
                }),
                cts.Token, attachments, sms);
            if (outcome.Attempted) _attempted = true;
            if (outcome.Result == GroupNoteResult.Sent) _store.DeleteDraft(_draftSlot);
            if (_closed) return;
            Status(outcome.Message, outcome.Result switch
            {
                GroupNoteResult.Sent => "Success",
                GroupNoteResult.Prepared => "AccentText",
                GroupNoteResult.Stopped => "Warn",
                _ => "Danger",
            });
        }
        catch (Exception ex)
        {
            if (!_closed) Status("온톡 조작을 확인하지 못했습니다. 온톡 쪽지 창을 직접 확인하세요. " + ex.Message, "Danger");
        }
        finally
        {
            _runCts = null;
            if (!_closed) SetRunning(false);
        }
    }

    private bool ConfirmSend(IReadOnlyList<ContactPerson> chosen, DateTime? reservation, IReadOnlyList<string> attachments, bool sms)
    {
        var sample = string.Join("\n", chosen.Take(8).Select(p => $"  · {p.Name} ({p.Title} · {string.Join(" · ", p.Organization)})"));
        var more = chosen.Count > 8 ? $"\n  외 {chosen.Count - 8}명" : "";
        var body = Body;
        var preview = body.Length > 180 ? body[..180] + "…" : body;
        var prompt =
            $"{chosen.Count}명에게 같은 쪽지 한 통을 보냅니다.\n\n" +
            $"발송 방식: {(reservation is { } at ? $"{at:yyyy-MM-dd HH:mm} 예약 발송" : "즉시 발송")}{(sms ? " · SMS 발송 선택" : "")}\n" +
            (attachments.Count > 0 ? $"첨부 파일 {attachments.Count}개: {string.Join(", ", attachments.Take(5).Select(Path.GetFileName))}{(attachments.Count > 5 ? $" 외 {attachments.Count - 5}개" : "")}\n" : "") +
            $"받는 사람:\n{sample}{more}\n\n" +
            $"본문 ({NoteBytes.Count(body):N0} Bytes):\n{preview}\n\n" +
            "보낸 뒤에는 되돌릴 수 없습니다. 보낼까요?";
        return Dialogs.Show(Host, prompt, $"{Title} 발송 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void SetRunning(bool running)
    {
        foreach (var control in _interactive) control.IsEnabled = !running;
        _searchRecipientButton.IsEnabled = !running;
        if (_attachSection is not null) _attachSection.IsEnabled = !running;
        foreach (var b in _aiButtons) b.IsEnabled = !running && !_aiBusy;
        UpdateUndoButton();
        _stopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _stopButton.IsEnabled = running;
        _progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        UpdateReservationState();
        if (!running) UpdateButtons();
        UpdateDraftState();
    }
}
