using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;
using TemplateRecord = OnTalk.Sidecar.Core.Template;

namespace OnTalk.Sidecar.App;

/// <summary>
/// 개인별 쪽지 작업 창. 작업 단위(① 서식 → ② 대상자·데이터 → ③ 본문 확인 → ④ 첨부 파일 → ⑤ 발송 방식)마다 카드로 나누고,
/// 발송 단추는 항상 보이는 아래 줄에 둔다.
/// Saved work contains recipient snapshots and per-person bodies, never a shared personalized recipient list.
/// </summary>
public sealed class PersonalizedPane : WorkPane
{
    private readonly ProfileStore _store;
    private readonly OllamaWriter _writer;
    private PersonalizedWork _work;
    private readonly Func<PersonalizedWork, PersonalizedWorkItem, Task>? _prepare;
    private readonly IPersonalizedAutomation _automation;

    // ① 서식
    private readonly ComboBox _templates = new();
    private readonly TextBox _templateName = new();
    private readonly TextBox _template = new() { Height = 84 };
    private readonly WrapPanel _fieldChips = new();
    private readonly TextBlock _templateCheck = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private bool _applyingTemplate;

    // ② 대상자
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, Height = 168, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _summary = new() { Style = null, VerticalAlignment = VerticalAlignment.Center };

    // ③ 본문
    private readonly TextBlock _selectedInfo = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _bodyInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBox _preview = new() { Height = 150, IsInactiveSelectionHighlightEnabled = true };
    private readonly TextBlock _aiScope = new() { TextWrapping = TextWrapping.Wrap, FontSize = 10.5, Margin = new Thickness(2, 5, 2, 0) };
    private readonly List<Button> _aiButtons = [];
    private readonly Button _undoButton;
    private bool _aiBusy, _running;
    private long _revision;
    private PersonalizedWorkItem? _undoItem;
    private string? _undoText;
    private int _undoSelectionStart, _undoSelectionLength;
    private long _undoRevision = -1;
    private readonly CancellationTokenSource _closing = new();

    // ⑤ 발송 방식
    private readonly CheckBox _useReservation = new() { Content = "예약 발송", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly DatePicker _reserveDate = new() { Width = 110, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _reserveHour = new() { Width = 56, Margin = new Thickness(0, 0, 3, 0) };
    private readonly ComboBox _reserveMinute = new() { Width = 56, Margin = new Thickness(0, 0, 3, 0) };
    private readonly TextBlock _reserveSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _useSms = new() { Content = "SMS 발송", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };

    // 첨부 파일. 모든 대상에게 같은 파일을 붙인다. 파일은 복사하지 않고 경로만 들고 있다가, 발송 때 온톡의 파일 선택창으로 붙인다.
    private readonly List<string> _attachments = [];
    private readonly StackPanel _attachList = new();
    private readonly TextBlock _attachSummary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    // 작업 / 하단 줄
    private readonly ComboBox _saved = new();
    private readonly TextBlock _savedInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 3, Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 5) };
    private readonly ScrollViewer _page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _batchSendButton;
    private readonly Button _stopBatchButton;
    private readonly Button _singleSendButton;
    private readonly FrameworkElement _reservePickers;
    private readonly List<UIElement> _interactiveControls = [];
    private CancellationTokenSource? _batchCts;
    private PersonalizedWorkItem? _shown;
    private bool _closed;
    private readonly StackPanel _savedBody = new();
    private readonly TextBlock _savedChevron = new(), _savedCount = new();
    private bool _savedExpanded;
    private readonly Dictionary<string, (TextBlock Count, ProgressBar Bar)> _headerCounters = [];
    private string _cleanSnapshot = "";
    private readonly IReadOnlyList<ContactPerson> _initialPeople;

    public PersonalizedPane(
        ProfileStore store,
        OllamaWriter writer,
        IReadOnlyList<ContactPerson> people,
        string template,
        Func<PersonalizedWork, PersonalizedWorkItem, Task>? prepare = null,
        IPersonalizedAutomation? automation = null)
    {
        (_store, _writer, _prepare, _automation) = (store, writer, prepare, automation ?? OnTalkPersonalizedAutomation.Instance);
        _initialPeople = people;
        _work = NewWork(people, template);
        EnsureHeaders();
        InitializeReservationControls();
        _undoButton = new Button
        {
            Content = "되돌리기",
            Focusable = false,
            IsEnabled = false,
            Padding = new Thickness(6, 1, 6, 1),
            MinHeight = 20,
            FontSize = 10.5,
            ToolTip = "직전 AI 문장 변환 전 상태로 되돌립니다.",
        };
        _undoButton.Click += (_, _) => UndoAi();

        Title = "개인별 쪽지";
        StyleControls();

        _reservePickers = BuildReservationPickers();
        _stopBatchButton = Btn("연속 발송 중단", (_, _) =>
        {
            _batchCts?.Cancel();
            _stopBatchButton!.IsEnabled = false;
            _status.Text = "현재 대상 처리 후 연속 발송을 중단합니다...";
        }, track: false, style: "DangerButton");
        _stopBatchButton.IsEnabled = false;
        _singleSendButton = Btn("선택한 한 사람만 발송", OnSingleSendAsync);
        _batchSendButton = Btn("준비된 대상 자동 연속 발송", OnBatchSendAsync, style: "PrimaryButton");

        var page = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
        page.Children.Add(BuildHeader());
        page.Children.Add(BuildTemplateSection());
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

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control && _batchCts is null)
            {
                TempSave();
                e.Handled = true;
            }
        };
        Load();
        SetSavedExpanded(_saved.Items.Count > 0);   // 저장한 작업이 없으면 접어 둔다.
    }

    private static PersonalizedWork NewWork(IReadOnlyList<ContactPerson> people, string template) => new()
    {
        Template = template,
        Headers = NotePersonalization.ExtractVariables(template).Count > 0
            ? NotePersonalization.ExtractVariables(template).ToList()
            : [PersonalizedWork.DefaultHeader],
        Items = people.Select(p => new PersonalizedWorkItem { Person = p }).ToList()
    };

    internal override bool IsBusy => _batchCts is not null || _running;

    internal override void HostActivated()
    {
        if (_batchCts is null) { FlushPreview(); Commit(); RefreshGrid(); ShowPreview(); }
    }

    internal override bool QueryClose() => _closed || ConfirmLeave("개인별 쪽지 닫기", "닫습니다.");

    internal override void HostClosed() { _batchCts?.Cancel(); _closed = true; _closing.Cancel(); }

    /// <summary>열려 있던 작업이 저장소에서 지워졌다. 창은 닫지 않고, 같은 대상자로 서식과 값이 빈 새 작업을 시작한다.</summary>
    private void StartFreshWork()
    {
        _work = NewWork(_initialPeople, "");
        _shown = null;
        Load();
    }

    // ───────────────────────── 화면 구성 ─────────────────────────

    private T Res<T>(string key) => (T)FindResource(key);

    private void StyleControls()
    {
        var multiline = Res<Style>("MultilineBox");
        _template.Style = multiline; _preview.Style = multiline;
        _template.Tag = "여기에 서식을 입력하세요.  예) {소속} {이름} {직급}님, {항목}";
        _preview.Tag = "②에서 대상자를 고르고 ‘서식으로 본문 생성’을 누르면 이곳에 개인별 본문이 나타납니다.";
        _templateName.Tag = "서식 제목 (예: 교육 일정 안내)";
        _templates.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(TemplateItemTemplate);
        _templateName.MaxLength = 60;
        _summary.Foreground = Res<Brush>("Muted"); _summary.FontSize = 11;
        _selectedInfo.FontWeight = FontWeights.SemiBold;
        _bodyInfo.Foreground = Res<Brush>("Muted"); _bodyInfo.FontSize = 11;
        _savedInfo.Foreground = Res<Brush>("Muted"); _savedInfo.FontSize = 11;
        _reserveSummary.Foreground = Res<Brush>("Muted"); _reserveSummary.FontSize = 11;
        _reserveSummary.Margin = new Thickness(0, 5, 0, 0);
        _status.Style = Res<Style>("StatusLine");
        _progress.Foreground = Res<Brush>("Accent"); _progress.Background = Res<Brush>("Line");
        _grid.FontSize = 11.5; _grid.MinRowHeight = 24; _grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _grid.RowHeight = 24; _grid.CanUserSortColumns = false; _grid.CanUserReorderColumns = false;
        _grid.AutomationName("개인별 대상자와 입력 데이터");
        _template.AutomationName("서식 내용"); _templateName.AutomationName("서식 제목"); _preview.AutomationName("선택한 사람의 본문");

        foreach (var c in new UIElement[] { _templates, _templateName, _template, _grid, _preview, _saved, _useReservation, _useSms, _attachList })
            _interactiveControls.Add(c);
        _interactiveControls.Add(_fieldChips);
        ForwardWheel(_grid); ForwardWheel(_template); ForwardWheel(_preview);

        _saved.MinHeight = 34;
        _saved.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(SavedItemTemplate);
        _saved.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnRemoveSavedClick));
        _templates.SelectionChanged += (_, _) => ApplySelectedTemplate();
        _preview.TextChanged += (_, _) => { _revision++; UpdateBodyInfo(); UpdateAiScope(); UpdateUndoButton(); };
        _preview.SelectionChanged += (_, _) => UpdateAiScope();
        _undoButton.Style = Res<Style>("SmallButton");
        _template.TextChanged += (_, _) => UpdateFieldChips();
        _grid.SelectionChanged += (_, _) => { FlushPreview(); ShowPreview(); };
        _grid.CellEditEnding += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit) Dispatcher.BeginInvoke(new Action(RefreshGrid), System.Windows.Threading.DispatcherPriority.Background);
        };
        _useReservation.Checked += (_, _) => UpdateReservationState();
        _useReservation.Unchecked += (_, _) => UpdateReservationState();
        _reserveDate.SelectedDateChanged += (_, _) => UpdateReservationState();
        _reserveHour.SelectionChanged += (_, _) => UpdateReservationState();
        _reserveMinute.SelectionChanged += (_, _) => UpdateReservationState();
    }

    /// <summary>저장된 서식 목록·선택 칸에는 서식 제목만 한 줄로 보인다.</summary>
    private const string TemplateItemTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis"/>
        </DataTemplate>
        """;

    private const string SavedItemTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Grid MinWidth="220">
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <StackPanel Margin="0,0,6,0" VerticalAlignment="Center">
              <DockPanel LastChildFill="True">
                <Border x:Name="DraftTag" DockPanel.Dock="Right" Margin="6,0,0,0" Padding="6,0" CornerRadius="8" VerticalAlignment="Center"
                        Background="{DynamicResource AccentSoft}" Visibility="Collapsed">
                  <TextBlock Text="임시 저장" FontSize="10.5" FontWeight="SemiBold" Foreground="{DynamicResource AccentText}"/>
                </Border>
                <Border x:Name="SentTag" DockPanel.Dock="Right" Margin="6,0,0,0" Padding="6,0" CornerRadius="8" VerticalAlignment="Center"
                        Background="{DynamicResource SuccessSoft}" Visibility="Collapsed">
                  <TextBlock Text="발송 완료" FontSize="10.5" FontWeight="SemiBold" Foreground="{DynamicResource Success}"/>
                </Border>
                <Border x:Name="PartTag" DockPanel.Dock="Right" Margin="6,0,0,0" Padding="6,0" CornerRadius="8" VerticalAlignment="Center"
                        Background="{DynamicResource WarnSoft}" Visibility="Collapsed">
                  <TextBlock Text="일부 발송" FontSize="10.5" FontWeight="SemiBold" Foreground="{DynamicResource Warn}"/>
                </Border>
                <TextBlock Text="{Binding Headline}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
              </DockPanel>
              <TextBlock Text="{Binding Detail}" FontSize="10.5" Foreground="{DynamicResource Muted}" TextTrimming="CharacterEllipsis"/>
            </StackPanel>
            <Button x:Name="RemoveButton" Grid.Column="1" Style="{DynamicResource IconButton}" Content="&#xE711;" Tag="{Binding Id}"
                    ToolTip="이 저장 작업 삭제" AutomationProperties.Name="저장 작업 삭제" Width="22" Height="22"/>
          </Grid>
          <DataTemplate.Triggers>
            <DataTrigger Binding="{Binding IsDraft}" Value="True">
              <Setter TargetName="DraftTag" Property="Visibility" Value="Visible"/>
            </DataTrigger>
            <DataTrigger Binding="{Binding IsSent}" Value="True">
              <Setter TargetName="SentTag" Property="Visibility" Value="Visible"/>
            </DataTrigger>
            <DataTrigger Binding="{Binding IsPartlySent}" Value="True">
              <Setter TargetName="PartTag" Property="Visibility" Value="Visible"/>
            </DataTrigger>
            <DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType=ComboBoxItem}}" Value="{x:Null}">
              <Setter TargetName="RemoveButton" Property="Visibility" Value="Collapsed"/>
            </DataTrigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """;

    /// <summary>목록 줄의 X 단추: 고른 작업을 바꾸지 않고 그 줄의 저장 작업만 지운다.</summary>
    private void OnRemoveSavedClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: string id } || (_saved.ItemsSource as IEnumerable<PersonalizedWork>)?.FirstOrDefault(w => w.Id == id) is not { } work) return;
        e.Handled = true;
        var current = work.Id == _work.Id;
        var prompt = $"저장한 작업을 삭제할까요?\n\n  {work.Headline}\n  {work.Detail}\n\n입력 데이터와 본문이 삭제되며 발송 기록은 유지됩니다." +
            (current ? "\n지금 열려 있는 작업이라 이 창도 닫힙니다." : "");
        if (Dialogs.Show(Host, prompt, "저장 작업 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _store.DeletePersonalizedWork(id);
        if (current) { StartFreshWork(); _status.Text = $"저장 작업 ‘{work.Headline}’을 삭제하고 새 작업으로 바꿨습니다."; return; }
        _saved.IsDropDownOpen = false;
        ReloadSaved();
        _status.Text = $"저장 작업 ‘{work.Headline}’을 삭제했습니다.";
    }

    private Button Btn(string title, RoutedEventHandler handler, bool track = true, string? style = null, string? tip = null)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Left };
        if (style is not null) button.Style = Res<Style>(style);
        if (tip is not null) button.ToolTip = tip;
        button.Click += handler;
        if (track) _interactiveControls.Add(button);
        return button;
    }

    private Button SmallBtn(string title, RoutedEventHandler handler, string? tip = null)
    {
        var button = Btn(title, handler, tip: tip);
        button.Style = Res<Style>("SmallButton");
        return button;
    }

    private TextBlock Muted(string text, double top = 0) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Style = Res<Style>("Small"), Margin = new Thickness(0, top, 0, 0)
    };

    private Border Section(string? step, string title, string hint, UIElement body, UIElement? right = null) =>
        WorkUi.Section(this, step, title, hint, body, right);

    private static TextBlock FieldLabel(string text, bool top = false) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold, FontSize = 11.5, Margin = new Thickness(0, top ? 5 : 0, 8, 0),
        VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center
    };

    private UIElement BuildHeader()
    {
        var page = new StackPanel();

        // ‘저장한 작업’은 제목과 떼어 접고 펼 수 있는 따로의 구역으로 둔다.
        var toggleContent = new StackPanel { Orientation = Orientation.Horizontal };
        _savedChevron.FontFamily = (FontFamily)FindResource("IconFont"); _savedChevron.FontSize = 10; _savedChevron.VerticalAlignment = VerticalAlignment.Center; _savedChevron.Margin = new Thickness(0, 0, 6, 0);
        toggleContent.Children.Add(_savedChevron);
        toggleContent.Children.Add(new TextBlock { Text = "저장한 작업", FontWeight = FontWeights.Bold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        _savedCount.Foreground = Res<Brush>("Muted"); _savedCount.FontSize = 11; _savedCount.VerticalAlignment = VerticalAlignment.Center; _savedCount.Margin = new Thickness(6, 0, 0, 0);
        toggleContent.Children.Add(_savedCount);
        var toggle = new Button
        {
            Content = toggleContent, Style = Res<Style>("LinkButton"), Foreground = Res<Brush>("TextStrong"), HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0), ToolTip = "저장한 작업 목록 접기/펼치기",
        };
        toggle.AutomationName("저장한 작업 접기 또는 펼치기");
        toggle.Click += (_, _) => SetSavedExpanded(!_savedExpanded);

        var saved = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        saved.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        saved.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        saved.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        saved.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _saved.Margin = new Thickness(0, 0, 6, 0); saved.Children.Add(_saved);
        var load = SmallBtn("불러오기", (_, _) =>
        {
            if (_saved.SelectedItem is not PersonalizedWork work) { _status.Text = "불러올 저장 작업을 목록에서 먼저 고르세요."; return; }
            if (!ConfirmLeave("저장 작업 불러오기", "불러옵니다.")) return;
            if (_store.GetPersonalizedWorks().FirstOrDefault(w => w.Id == work.Id) is not { } fresh) { _status.Text = "저장 작업을 찾지 못했습니다."; Load(); return; }
            _work = fresh; _shown = null; Load();
            _status.Text = "저장한 작업을 불러왔습니다.";
        }, "고른 저장 작업으로 이 창의 내용을 바꿉니다. 저장하지 않은 변경이 있으면 먼저 저장할지 묻습니다.");
        Grid.SetColumn(load, 1); saved.Children.Add(load);
        var delete = SmallBtn("현재 작업 삭제", (_, _) =>
        {
            if (Dialogs.Show(Host, "저장한 개인화 작업과 입력 데이터·본문을 삭제할까요? 발송 기록은 유지됩니다.", "개인화 작업 삭제", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            _store.DeletePersonalizedWork(_work.Id); StartFreshWork(); _status.Text = "저장한 작업을 삭제하고 새 작업으로 바꿨습니다.";
        }, "지금 열려 있는 작업을 저장소에서 지우고 서식과 입력 값이 빈 새 작업으로 바꿉니다.");
        delete.Style = Res<Style>("DangerButton"); delete.MinHeight = 22; delete.Padding = new Thickness(8, 0, 8, 0); delete.FontSize = 11; delete.Margin = new Thickness(0);
        var clean = SmallBtn("표시 없는 작업 정리", (_, _) => CleanUnmarkedWorks(),
            "‘임시 저장’ 표시도 발송 기록도 없는 저장 작업(이전 버전이 창을 닫을 때 자동 저장한 것 등)을 한꺼번에 지웁니다.");
        Grid.SetColumn(clean, 2); saved.Children.Add(clean);
        Grid.SetColumn(delete, 3); saved.Children.Add(delete);
        _savedBody.Children.Add(saved);
        _savedBody.Children.Add(_savedInfo);
        var section = new StackPanel();
        section.Children.Add(toggle); section.Children.Add(_savedBody);
        page.Children.Add(new Border { Style = Res<Style>("Card"), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Child = section });
        return page;
    }

    private void SetSavedExpanded(bool expanded)
    {
        _savedExpanded = expanded;
        _savedBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _savedChevron.Text = expanded ? "" : "";
    }

    private UIElement BuildTemplateSection()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Place(UIElement el, int row, int col, int span = 1, Thickness? margin = null)
        {
            Grid.SetRow(el, row); Grid.SetColumn(el, col); Grid.SetColumnSpan(el, span);
            if (el is FrameworkElement fe) fe.Margin = margin ?? new Thickness(0, 0, 0, 5);
            grid.Children.Add(el);
        }

        Place(FieldLabel("저장된 서식"), 0, 0, margin: new Thickness(0, 0, 8, 5));
        Place(_templates, 0, 1, margin: new Thickness(0, 0, 6, 5));
        var del = SmallBtn("서식 삭제", (_, _) => DeleteSelectedTemplate(), "고른 저장 서식을 목록에서 지웁니다. 진행 중인 작업의 서식 내용은 그대로입니다.");
        Place(del, 0, 2, margin: new Thickness(0, 0, 0, 5));

        Place(FieldLabel("서식 제목"), 1, 0, margin: new Thickness(0, 0, 8, 5));
        Place(_templateName, 1, 1, margin: new Thickness(0, 0, 6, 5));
        var save = SmallBtn("서식 저장", (_, _) => SaveTemplate(), "제목과 내용을 서식으로 저장합니다. 같은 제목이 있으면 내용을 바꿉니다.");
        save.Style = Res<Style>("SmallButton");
        Place(save, 1, 2, margin: new Thickness(0, 0, 0, 5));

        Place(FieldLabel("서식 내용", top: true), 2, 0, margin: new Thickness(0, 5, 8, 5));
        Place(_template, 2, 1, 2);

        Place(FieldLabel("변수 삽입", top: true), 3, 0, margin: new Thickness(0, 4, 8, 0));
        Place(_fieldChips, 3, 1, 2, new Thickness(0));
        Place(_templateCheck, 4, 1, 2, new Thickness(0, 2, 0, 0));

        return Section("1", "서식", "모든 대상에게 쓸 공통 문안입니다. 중괄호 변수는 사람마다 다른 값으로 바뀝니다. ②에서 만든 항목은 모두 서식에 넣어야 합니다.", grid);
    }

    private UIElement BuildRecipientSection()
    {
        var body = new StackPanel();
        var files = new WrapPanel();
        files.Children.Add(SmallBtn("＋ 항목 추가", (_, _) => AddHeaderInteractive(), "사람마다 다른 값을 넣을 항목 열을 하나 더 만듭니다. 만든 항목은 서식에 꼭 넣어야 합니다."));
        files.Children.Add(SmallBtn("입력 양식 CSV 내보내기", (_, _) => ExportCsv(), "대상자의 이름·소속·직급과 서식 변수 열이 들어 있는 CSV를 저장합니다."));
        files.Children.Add(SmallBtn("CSV · TSV · XLSX 가져오기", (_, _) => ImportFile(), "열 이름(헤더)이 서식 변수와 같은 표 파일을 읽어 사람별 값을 채웁니다."));
        body.Children.Add(files);
        _grid.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_grid);
        var help = TableHelpButton();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_summary); right.Children.Add(help);
        return Section("2", "대상자와 개인별 데이터", "조직도에서 고른 사람과 사람마다 다른 값을 확인합니다.", body, right);
    }

    /// <summary>②의 표 사용법을 보여 주는 ‘?’ 단추.</summary>
    private Button TableHelpButton() => WorkUi.HelpButton(this, "표 사용 안내", new[]
    {
        "연한 칸은 더블클릭하거나 선택 후 F2로 직접 입력합니다.",
        "비어 있는 칸은 붉은 바탕에 ‘입력 필요 (F2 or 더블클릭)’로 보이고, 열 제목 옆에 입력 현황(예: 3/53)이 표시됩니다.",
        "발송 상태는 줄에 마우스를 올리거나 ③에서 봅니다.",
        "항목 제목 옆의 ✎로 제목을 바꾸고 ✕로 열을 지웁니다.",
        "이름·소속·직급은 수정하지 않습니다.",
    });

    private UIElement BuildBodySection()
    {
        var body = new StackPanel();
        var info = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        DockPanel.SetDock(_bodyInfo, Dock.Right);
        info.Children.Add(_bodyInfo); info.Children.Add(_selectedInfo);

        // 왼쪽: 사람 정보 + 본문 / 오른쪽: AI 문장 다듬기 패널(본문과 같은 높이로 나란히 둔다).
        var left = new StackPanel();
        left.Children.Add(info);
        left.Children.Add(_preview);
        var compare = new Grid();
        compare.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        compare.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
        compare.Children.Add(left);
        var aiPanel = BuildAiPanel();
        Grid.SetColumn(aiPanel, 1);
        compare.Children.Add(aiPanel);
        body.Children.Add(compare);

        var nav = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        nav.Children.Add(SmallBtn("◀ 이전 사람", (_, _) => MoveSelection(-1)));
        nav.Children.Add(SmallBtn("다음 사람 ▶", (_, _) => MoveSelection(1)));
        nav.Children.Add(SmallBtn("다음 미발송 대상 선택", (_, _) => SelectNextPending(), "본문이 준비되고 아직 발송을 시도하지 않은 다음 사람을 고릅니다."));
        if (_prepare is not null)
            nav.Children.Add(SmallBtn("온톡 쪽지창에 직접 준비", async (_, _) =>
            {
                SyncWork();
                if (!RequireAllFields("쪽지창에 준비할")) return;
                if (_grid.SelectedItem is not PersonalizedWorkItem item) { _status.Text = "대상을 목록에서 먼저 선택하세요."; return; }
                if (item.Attempted) { _status.Text = "발송을 시도한 대상입니다. 온톡 보낸 쪽지함과 발송 기록을 확인하세요. 자동 재전송하지 않습니다."; return; }
                if (!NotePersonalization.IsValidBody(item.Body))
                { _status.Text = "본문의 빈 값·미완성 변수·4000바이트 초과를 먼저 수정하세요."; return; }
                SetBatchRunning(true);
                try { await _prepare(_work, item); if (!_closed && item.Status == "작성 중 · 미발송") { HideHost(); } }
                finally { if (!_closed) { SetBatchRunning(false); RefreshGrid(); } }
            }, "자동 발송 대신 온톡 쪽지 작성창에 이 사람의 수신자·본문만 채워 두고, 직접 확인해 전송합니다."));
        body.Children.Add(nav);

        var generate = Btn("서식으로 본문 생성 · 다시 생성", (_, _) => GenerateBodies(), style: "PrimaryButton", tip: "①의 서식과 ②의 값을 합쳐 미발송 대상의 본문을 만듭니다.");
        return Section("3", "본문 확인과 수정", "사람을 고르면 본문이 나타납니다. 필요한 경우 한 사람씩 직접 고치거나 AI로 다듬을 수 있습니다.", body, generate);
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
            FontWeight = FontWeights.SemiBold,
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
        var body = new StackPanel();
        var row = new WrapPanel();
        row.Children.Add(_useReservation);
        row.Children.Add(_reservePickers);
        body.Children.Add(row);
        body.Children.Add(_reserveSummary);

        _useSms.ToolTip = "온톡 쪽지 창의 ‘SMS 발송’ 확인란을 선택한 상태로 모든 대상에게 보냅니다.";
        body.Children.Add(_useSms);
        return Section("5", "발송 방식", "기본은 즉시 발송입니다. 예약하면 모든 대상에게 같은 예약 시각이 적용되며, SMS 발송도 모든 대상에게 똑같이 적용됩니다.", body);
    }

    private UIElement BuildAttachmentSection()
    {
        var body = new StackPanel();
        var bar = new WrapPanel();
        bar.Children.Add(SmallBtn("파일 추가…", (_, _) => OnPickAttachments(),
            $"붙일 파일을 고릅니다. 파일을 이 영역에 끌어다 놓아도 됩니다. 모든 대상에게 같은 파일이 붙으며, 파일당 최대 20MB, 최대 {NoteAttachments.MaxCount}개, exe 파일은 붙일 수 없습니다."));
        body.Children.Add(bar);
        body.Children.Add(_attachList);
        body.Children.Add(_attachSummary);
        var section = Section("4", "첨부 파일", "모든 대상에게 같은 파일을 붙입니다. 필요 없으면 비워 두세요.", body);
        section.AllowDrop = true;
        section.DragOver += (_, e) =>
        {
            e.Effects = _batchCts is null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        section.Drop += (_, e) =>
        {
            if (_batchCts is null && e.Data.GetData(DataFormats.FileDrop) is string[] dropped) AddAttachments(dropped);
            e.Handled = true;
        };
        RenderAttachments();
        return section;
    }

    // ───────────────────────── 첨부 파일 ─────────────────────────

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
            if (System.IO.Directory.Exists(path)) { _status.Text = $"폴더는 붙일 수 없습니다: {System.IO.Path.GetFileName(path)}"; continue; }
            if (_attachments.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            // 이름·형식·크기·개수는 발송 때와 같은 규칙으로 지금 확인한다.
            if (NoteAttachments.Validate([.. _attachments, path]) is { } problem) { _status.Text = problem; continue; }
            _attachments.Add(path);
            added++;
        }
        RenderAttachments();
        if (added > 0) _status.Text = $"첨부 {added}개를 추가했습니다. 발송 때 모든 대상의 온톡 쪽지 창에 붙입니다.";
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
            var size = new System.IO.FileInfo(path) is { Exists: true } info ? NoteAttachments.Size(info.Length) : "파일 없음";
            row.Children.Add(new TextBlock { Text = "📎 " + System.IO.Path.GetFileName(path), ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var sizeText = new TextBlock { Text = size, Foreground = Res<Brush>("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
            Grid.SetColumn(sizeText, 1); row.Children.Add(sizeText);
            var remove = new Button { Content = "✕", Style = Res<Style>("SmallButton"), ToolTip = "첨부에서 뺍니다(원본 파일은 그대로)." };
            remove.AutomationName("첨부 제거 " + System.IO.Path.GetFileName(path));
            remove.Click += (_, _) => { _attachments.Remove(path); RenderAttachments(); };
            Grid.SetColumn(remove, 2); row.Children.Add(remove);
            _attachList.Children.Add(row);
        }
        var total = _attachments.Sum(p => new System.IO.FileInfo(p) is { Exists: true } f ? f.Length : 0);
        _attachSummary.Text = _attachments.Count == 0
            ? $"붙인 파일이 없습니다. 파일당 최대 20MB, 최대 {NoteAttachments.MaxCount}개, exe 파일은 붙일 수 없습니다."
            : $"첨부 {_attachments.Count}개 · 합계 {NoteAttachments.Size(total)} · 대상자마다 같은 파일을 새로 붙입니다." +
              (total > NoteAttachments.MaxFileBytes ? " 합계가 20MB를 넘으면 온톡이 거절할 수 있습니다." : "");
        _attachSummary.Foreground = Res<Brush>(total > NoteAttachments.MaxFileBytes ? "Warn" : "Muted");
    }

    private FrameworkElement BuildReservationPickers()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_reserveDate);
        panel.Children.Add(_reserveHour);
        panel.Children.Add(new TextBlock { Text = "시", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        panel.Children.Add(_reserveMinute);
        panel.Children.Add(new TextBlock { Text = "분", VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private UIElement BuildActionBar()
    {
        var bar = new StackPanel { Margin = new Thickness(12, 7, 12, 8) };
        bar.Children.Add(_progress);
        var statusScroll = new ScrollViewer { MaxHeight = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 5), Content = _status };
        bar.Children.Add(statusScroll);

        var buttons = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        _stopBatchButton.Margin = new Thickness(0, 0, 6, 0);
        right.Children.Add(_stopBatchButton);
        _singleSendButton.Margin = new Thickness(0, 0, 6, 0);
        right.Children.Add(_singleSendButton);
        _batchSendButton.Margin = new Thickness(0);
        right.Children.Add(_batchSendButton);
        buttons.Children.Add(right);
        var saveButton = Btn("임시 저장", (_, _) => TempSave(), tip: "수신자 정보·입력 데이터·개별 본문을 저장해 두었다가 나중에 불러옵니다. (Ctrl+S)");
        buttons.Children.Add(saveButton);
        bar.Children.Add(buttons);
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

    // ───────────────────────── 서식 ─────────────────────────

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
        if (_template.Text.Trim().Length > 0 && _template.Text != chosen.Body &&
            Dialogs.Show(Host, $"현재 서식 내용을 ‘{chosen.Name}’ 서식으로 바꿀까요?\n(만들어 둔 개인별 본문은 ‘서식으로 본문 생성’을 누를 때까지 바뀌지 않습니다.)", "서식 불러오기", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
        {
            _applyingTemplate = true;
            try { _templates.SelectedItem = ((IEnumerable<TemplateRecord>)_templates.ItemsSource).FirstOrDefault(t => t.Name == _templateName.Text.Trim()); }
            finally { _applyingTemplate = false; }
            return;
        }
        _templateName.Text = chosen.Name; _template.Text = chosen.Body;
        // 서식에 맞춰 항목을 정리한다: 서식에 없고 값도 없는 항목(기본으로 만들어 둔 항목 등)은 지운다.
        Commit();
        var (added, removed) = _work.AlignHeadersTo(_template.Text);
        if (added.Count + removed.Count > 0) { EnsureHeaders(); RebuildColumns(); UpdateFieldChips(); RefreshGrid(); }
        _status.Text = $"‘{chosen.Name}’ 서식을 불러왔습니다." +
            (removed.Count > 0 ? $" 서식에 없고 값도 없는 항목 {string.Join(", ", removed.Select(r => "‘" + r + "’"))}은(는) 지웠습니다." : "");
    }

    private void SaveTemplate()
    {
        var name = _templateName.Text.Trim();
        if (name.Length == 0) { _status.Text = "서식을 저장하려면 서식 제목을 입력하세요."; _templateName.Focus(); return; }
        if (_template.Text.Trim().Length == 0) { _status.Text = "서식 내용이 비어 있어 저장하지 않았습니다."; _template.Focus(); return; }
        if (!RequireAllFields("서식을 저장할")) return;
        var existing = _store.GetTemplates().FirstOrDefault(t => t.Name == name);
        if (existing is not null && existing.Body != _template.Text &&
            Dialogs.Show(Host, $"‘{name}’ 서식이 이미 있습니다. 내용을 지금 서식으로 바꿀까요?", "서식 덮어쓰기", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _store.SaveTemplate(name, _template.Text);
        ReloadTemplates(name);
        _status.Text = existing is null ? $"‘{name}’ 서식을 저장했습니다." : $"‘{name}’ 서식의 내용을 바꿔 저장했습니다.";
    }

    private void DeleteSelectedTemplate()
    {
        if (_templates.SelectedItem is not TemplateRecord chosen) { _status.Text = "삭제할 저장 서식을 목록에서 먼저 고르세요."; return; }
        if (Dialogs.Show(Host, $"‘{chosen.Name}’ 저장 서식을 삭제할까요?\n진행 중인 작업의 서식 내용은 그대로 남습니다.", "서식 삭제", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _store.DeleteTemplate(chosen.Id);
        ReloadTemplates();
        _status.Text = $"‘{chosen.Name}’ 서식을 삭제했습니다.";
    }

    /// <summary>변수 삽입 단추: 기본 변수 → 항목(개수만큼). 서식에 아직 없는 항목은 주황색으로 표시한다.</summary>
    private void UpdateFieldChips()
    {
        var missing = NotePersonalization.MissingFields(_template.Text, _work.Headers);
        _fieldChips.Children.Clear();
        foreach (var field in NotePersonalization.IdentityFields)
            _fieldChips.Children.Add(FieldChip(field, "서식 내용의 커서 위치에 넣습니다."));
        _fieldChips.Children.Add(new Border { Width = 1, Height = 14, Background = Res<Brush>("Line"), Margin = new Thickness(3, 0, 6, 4) });
        foreach (var field in _work.Headers)
        {
            var isMissing = missing.Contains(field);
            var chip = FieldChip(field, isMissing ? "서식에 아직 넣지 않은 항목입니다. 누르면 커서 위치에 넣습니다." : "서식에 들어 있습니다. 누르면 커서 위치에 한 번 더 넣습니다.");
            chip.Content = "{" + field + "}" + (isMissing ? "" : " ✓");
            chip.Foreground = Res<Brush>(isMissing ? "Warn" : "Accent");
            chip.BorderBrush = Res<Brush>(isMissing ? "Warn" : "Accent");
            _fieldChips.Children.Add(chip);
        }

        if (missing.Count == 0)
        {
            _templateCheck.Text = $"✓ 항목 {_work.Headers.Count}개가 모두 서식에 들어 있습니다.";
            _templateCheck.Foreground = Res<Brush>("Muted");
        }
        else
        {
            _templateCheck.Text = $"서식에 아직 넣지 않은 항목: {string.Join(", ", missing.Select(m => "{" + m + "}"))} — 모두 넣어야 서식 저장·본문 생성·발송을 할 수 있습니다.";
            _templateCheck.Foreground = Res<Brush>("Warn");
        }
    }

    private Button FieldChip(string field, string tip)
    {
        var chip = new Button { Content = "{" + field + "}", Style = Res<Style>("SmallButton"), Margin = new Thickness(0, 0, 4, 4), ToolTip = tip };
        chip.Click += (_, _) =>
        {
            var start = _template.SelectionStart; var text = "{" + field + "}";
            _template.SelectedText = text; _template.CaretIndex = start + text.Length; _template.Focus();
        };
        return chip;
    }

    /// <summary>항목이 서식에 모두 들어 있어야 다음 단계로 간다. 빠진 것이 있으면 알리고 서식 칸으로 돌아간다.</summary>
    private bool RequireAllFields(string action)
    {
        var missing = NotePersonalization.MissingFields(_template.Text, _work.Headers);
        if (missing.Count == 0) return true;
        _status.Text = $"서식에 항목 {string.Join(", ", missing.Select(m => "{" + m + "}"))}을(를) 모두 넣어야 {action} 수 있습니다. ①의 항목 단추로 커서 위치에 넣으세요.";
        _template.Focus();
        return false;
    }

    // ───────────────────────── 항목 추가·이름 변경·삭제 ─────────────────────────

    private void AddHeaderInteractive()
    {
        Commit(); FlushPreview();
        var name = AskHeaderName("항목 추가", "새 항목의 제목을 입력하세요.", _work.NextHeaderName(), n => NotePersonalization.ValidateHeaderName(n, _work.Headers));
        if (name is null) return;
        if (_work.AddHeader(name) is { } error) { _status.Text = error; return; }
        RebuildColumns(); UpdateFieldChips(); RefreshGrid();
        _status.Text = $"항목 ‘{name}’을(를) 추가했습니다. 서식에 {{{name}}}을(를) 넣고 ②에서 사람별 값을 입력하세요.";
    }

    private void RenameHeaderInteractive(string header)
    {
        if (_batchCts is not null) return;
        Commit(); FlushPreview();
        var name = AskHeaderName("항목 제목 바꾸기", $"‘{header}’의 새 제목을 입력하세요. 서식의 {{{header}}}도 함께 바뀝니다.", header, n => NotePersonalization.ValidateHeaderName(n, _work.Headers, header));
        if (name is null || name == header) return;
        if (_work.RenameHeader(header, name) is { } error) { _status.Text = error; return; }
        _template.Text = NotePersonalization.RenameVariable(_template.Text, header, name);
        RebuildColumns(); UpdateFieldChips(); RefreshGrid();
        _status.Text = $"항목 ‘{header}’의 제목을 ‘{name}’(으)로 바꿨습니다. 이미 만든 본문은 그대로이니 필요하면 ③에서 다시 생성하세요.";
    }

    private void RemoveHeaderInteractive(string header)
    {
        if (_batchCts is not null) return;
        if (_work.Headers.Count <= 1) { _status.Text = "항목은 최소 하나가 있어야 합니다."; return; }
        Commit(); FlushPreview();
        var inTemplate = NotePersonalization.ExtractVariables(_template.Text).Contains(header);
        var prompt = $"항목 ‘{header}’ 열을 지울까요?\n\n사람별로 입력한 이 열의 값이 모두 삭제됩니다." +
            (inTemplate ? $"\n서식 내용의 {{{header}}}도 함께 지워집니다." : "");
        if (Dialogs.Show(Host, prompt, "항목 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        if (_work.RemoveHeader(header) is { } error) { _status.Text = error; return; }
        _template.Text = NotePersonalization.RemoveVariable(_template.Text, header);
        RebuildColumns(); UpdateFieldChips(); RefreshGrid();
        _status.Text = $"항목 ‘{header}’을(를) 지웠습니다. 이미 만든 본문은 그대로이니 필요하면 ③에서 다시 생성하세요.";
    }

    /// <summary>항목 제목 입력 창. 검사에 실패하면 창 안에 이유를 보이고 닫지 않는다. 취소하면 null.</summary>
    private string? AskHeaderName(string title, string message, string initial, Func<string, string?> validate)
    {
        var input = new TextBox { Text = initial, MaxLength = NotePersonalization.MaxHeaderLength, Style = Res<Style>("FieldBox"), MinHeight = 32, Margin = new Thickness(0, 10, 0, 0) };
        var error = new TextBlock { Foreground = Res<Brush>("Warn"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = "확인", MinWidth = 76, IsDefault = true, Style = Res<Style>("PrimaryButton") };
        var cancel = new Button { Content = "취소", MinWidth = 76, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(input); stack.Children.Add(error); stack.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Owner = Host, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 420,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Style = Res<Style>("AppWindow"), Content = stack,
        };
        string? result = null;
        ok.Click += (_, _) =>
        {
            if (validate(input.Text) is { } problem) { error.Text = problem; input.Focus(); return; }
            result = input.Text.Trim(); dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog();
        return result;
    }

    /// <summary>서식에 새로 쓴 변수가 있으면 입력 열을 만들어 값을 채울 수 있게 한다.</summary>
    private bool AddMissingHeaders()
    {
        var missing = NotePersonalization.ExtractVariables(_template.Text).Where(v => !_work.Headers.Contains(v)).ToList();
        if (missing.Count == 0) return false;
        Commit();
        _work.Headers.AddRange(missing);
        EnsureHeaders(); RebuildColumns(); UpdateFieldChips(); RefreshGrid();
        return true;
    }

    // ───────────────────────── 대상자·가져오기 ─────────────────────────

    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "개인별 입력 양식 저장", Filter = "CSV 파일 (*.csv)|*.csv", FileName = "개인별_쪽지_입력.csv" };
        if (dialog.ShowDialog(Host) != true) return;
        try
        {
            var headers = _work.Headers.Concat(NotePersonalization.ExtractVariables(_template.Text)).Distinct().ToList();
            System.IO.File.WriteAllText(dialog.FileName, PersonalizedImport.ExampleCsv(_work.Items.Select(i => i.Person), headers), new System.Text.UTF8Encoding(true));
            _status.Text = "선택 대상의 이름·소속·직급 양식을 저장했습니다. 파일에 원하는 헤더(열)와 데이터를 입력한 뒤 가져오세요.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _status.Text = "양식을 저장하지 못했습니다: " + ex.Message; }
    }

    private void ImportFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "개인별 데이터 파일 가져오기", Filter = "표 파일 (*.csv;*.tsv;*.xlsx)|*.csv;*.tsv;*.xlsx" };
        if (dialog.ShowDialog(Host) != true) return;
        Commit(); FlushPreview();
        var imported = PersonalizedImport.ReadFile(dialog.FileName);
        var errors = imported.Ready ? PersonalizedImport.Match(_work, imported) : imported.Errors;
        if (errors.Count > 0)
        {
            _status.Text = "가져오지 않았습니다. 선택 대상과 파일의 이름·소속·직급 및 헤더를 확인하세요.\n" + string.Join("\n", errors.Take(12)) +
                (errors.Count > 12 ? $"\n외 {errors.Count - 12}건" : "");
            return;
        }
        EnsureHeaders(); RebuildColumns(); UpdateFieldChips(); RefreshGrid();
        _status.Text = _work.Headers.Count > 0
            ? $"{imported.Rows.Count}명의 데이터(헤더: {string.Join(", ", _work.Headers)})를 가져왔습니다. ③에서 ‘서식으로 본문 생성’을 눌러 확인하세요."
            : $"{imported.Rows.Count}명의 기본 명단을 확인했습니다. 파일에 개인별 데이터 헤더(열)를 추가한 뒤 다시 가져오세요.";
    }

    private void GenerateBodies()
    {
        FlushPreview(); Commit();
        if (_template.Text.Trim().Length == 0) { _status.Text = "①에 서식 내용을 먼저 입력하세요."; _template.Focus(); return; }
        var added = AddMissingHeaders();
        if (!RequireAllFields("본문을 생성할")) return;
        if (_work.Items.Any(i => i.Body.Length > 0 && !i.Attempted) && Dialogs.Show(Host, "미발송 대상의 개별 수정 본문을 현재 서식으로 다시 만들까요?", "본문 다시 생성", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        var errors = new List<string>();
        foreach (var item in _work.Items.Where(i => !i.Attempted))
        {
            var rendered = NotePersonalization.Render(item.Person, _template.Text, item.Values);
            item.Body = rendered.Body; item.Status = rendered.Ready ? "준비됨" : string.Join(" / ", rendered.Errors);
            if (!rendered.Ready) errors.Add(item.Name + ": " + item.Status);
        }
        _shown = null; RefreshGrid(); ShowPreview();
        var note = added ? "서식에 새로 쓴 변수의 입력 열을 ②에 추가했습니다. 값을 입력한 뒤 다시 생성하세요.\n" : "";
        _status.Text = errors.Count == 0
            ? note + "개인별 본문을 만들었습니다. 사람별로 확인하고 필요하면 수정하세요."
            : note + "본문을 만들지 못한 대상이 있습니다. ②의 값을 채우거나 파일을 가져온 뒤 다시 생성하세요.\n" + string.Join("\n", errors.Take(12)) + (errors.Count > 12 ? $"\n외 {errors.Count - 12}건" : "");
    }

    private void MoveSelection(int step)
    {
        if (_work.Items.Count == 0) return;
        Commit();
        var index = Math.Clamp((_grid.SelectedIndex < 0 ? 0 : _grid.SelectedIndex) + step, 0, _work.Items.Count - 1);
        _grid.SelectedIndex = index;
        _grid.ScrollIntoView(_grid.SelectedItem);
    }

    // ───────────────────────── 예약 ─────────────────────────

    private void InitializeReservationControls()
    {
        _reserveHour.ItemsSource = Enumerable.Range(0, 24).ToList();
        _reserveMinute.ItemsSource = Enumerable.Range(0, 60).ToList();
        var next = DateTime.Now.AddHours(1);
        _reserveDate.SelectedDate = next.Date;
        _reserveHour.SelectedItem = next.Hour;
        _reserveMinute.SelectedItem = 0;
    }

    private void UpdateReservationState()
    {
        var on = _useReservation.IsChecked == true;
        _reservePickers.IsEnabled = on && _batchCts is null;
        _reserveSummary.Foreground = Res<Brush>("Muted");
        if (!on)
        {
            _reserveSummary.Text = "즉시 발송: 발송 단추를 누르면 바로 한 사람씩 보냅니다.";
            return;
        }
        if (TryGetReservation(out var at, out var error) && at is { } when)
            _reserveSummary.Text = $"{when:yyyy년 M월 d일 HH:mm}에 예약 발송합니다. 모든 대상에게 같은 시각이 적용됩니다.";
        else
        {
            _reserveSummary.Text = error;
            _reserveSummary.Foreground = Res<Brush>("Warn");
        }
    }

    private bool TryGetReservation(out DateTime? reservation, out string error)
    {
        reservation = null;
        error = "";
        if (_useReservation.IsChecked != true)
        {
            return true;
        }
        if (_reserveDate.SelectedDate is not { } date ||
            _reserveHour.SelectedItem is not int hour ||
            _reserveMinute.SelectedItem is not int minute)
        {
            error = "일괄 예약 발송 날짜와 시각(시·분)을 모두 선택하세요.";
            return false;
        }
        var when = date.Date.AddHours(hour).AddMinutes(minute);
        if (when <= DateTime.Now.AddMinutes(2))
        {
            error = "예약 시각은 현재 시각보다 최소 3분 이후여야 합니다.";
            return false;
        }
        reservation = when;
        return true;
    }

    // ───────────────────────── 발송 ─────────────────────────

    private async void OnBatchSendAsync(object sender, RoutedEventArgs e)
    {
        SyncWork();
        if (!RequireAllFields("발송할")) return;
        var candidates = _work.Items.Where(i => !i.Attempted && NotePersonalization.IsValidBody(i.Body)).ToList();
        if (candidates.Count == 0)
        {
            _status.Text = "자동 발송할 준비된 미발송 대상이 없습니다. 먼저 ③에서 ‘서식으로 본문 생성’을 누르고 각 본문을 확인하세요.";
            return;
        }
        if (!TryGetReservation(out var reservation, out var reserveError))
        {
            _status.Text = reserveError;
            return;
        }
        if (NoteAttachments.Validate(_attachments) is { } attachProblem) { _status.Text = attachProblem; return; }
        var skipped = _work.Items.Count(i => !i.Attempted) - candidates.Count;
        var sample = string.Join("\n", candidates.Take(5).Select(c => $"  · {c.Name} ({c.Title} · {c.Organization})"));
        var more = candidates.Count > 5 ? $"\n  외 {candidates.Count - 5}명" : "";
        var modeText = ModeText(reservation);
        var prompt =
            $"준비된 미발송 대상 {candidates.Count}명에게 개인별 쪽지를 연속으로 자동 발송합니다.\n\n" +
            $"발송 방식: {modeText}\n" +
            AttachmentLine() +
            $"대상 미리보기:\n{sample}{more}\n\n" +
            (skipped > 0 ? $"※ 본문이 준비되지 않은 미발송 {skipped}명은 이번에 보내지 않습니다.\n\n" : "") +
            "온톡에서 빈 '새 쪽지' 창을 자동으로 확보(없으면 새로 열기)하여 한 사람씩 수신자 대조·본문 반영·검증·전송을 수행합니다.\n" +
            "보낸 뒤에는 되돌릴 수 없습니다. 자동 연속 발송을 시작할까요?";
        if (Dialogs.Show(Host, prompt, "개인별 쪽지 자동 연속 발송 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            _status.Text = "자동 연속 발송을 취소했습니다.";
            return;
        }
        await RunBatchAsync(candidates, reservation);
    }

    private async void OnSingleSendAsync(object sender, RoutedEventArgs e)
    {
        SyncWork();
        if (!RequireAllFields("발송할")) return;
        if (_grid.SelectedItem is not PersonalizedWorkItem item)
        {
            _status.Text = "자동 발송할 대상을 ②의 목록에서 먼저 선택하세요.";
            return;
        }
        if (item.Attempted)
        {
            _status.Text = "이미 발송을 시도한 대상입니다. 온톡 보낸 쪽지함과 발송 기록을 확인하세요.";
            return;
        }
        if (!NotePersonalization.IsValidBody(item.Body))
        {
            _status.Text = "선택한 대상의 본문에 빈 값·미완성 변수·4000바이트 초과가 있습니다. 먼저 수정하세요.";
            return;
        }
        if (!TryGetReservation(out var reservation, out var reserveError))
        {
            _status.Text = reserveError;
            return;
        }
        if (NoteAttachments.Validate(_attachments) is { } attachProblem) { _status.Text = attachProblem; return; }
        var modeText = ModeText(reservation);
        var preview = item.Body.Length > 180 ? item.Body[..180] + "…" : item.Body;
        var prompt =
            $"{item.Name} ({item.Title} · {item.Organization}) 1명에게 개인별 쪽지를 자동 발송합니다.\n\n" +
            $"발송 방식: {modeText}\n" +
            AttachmentLine() +
            $"본문 ({NoteBytes.Count(item.Body)} Bytes):\n{preview}\n\n" +
            "보낼까요?";
        if (Dialogs.Show(Host, prompt, "개인별 쪽지 자동 발송 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            _status.Text = "발송을 취소했습니다.";
            return;
        }
        await RunBatchAsync([item], reservation);
    }

    private string ModeText(DateTime? reservation) =>
        (reservation is { } at ? $"{at:yyyy-MM-dd HH:mm} 예약 발송" : "즉시 발송") + (_useSms.IsChecked == true ? " · SMS 발송 선택" : "");

    private string AttachmentLine() => _attachments.Count == 0
        ? ""
        : $"첨부 파일 {_attachments.Count}개(대상마다 같은 파일): {string.Join(", ", _attachments.Take(5).Select(System.IO.Path.GetFileName))}" +
          $"{(_attachments.Count > 5 ? $" 외 {_attachments.Count - 5}개" : "")}\n";

    public async Task<PersonalizedBatchSummary> RunBatchAsync(
        IReadOnlyList<PersonalizedWorkItem>? candidates = null,
        DateTime? reservation = null)
    {
        Save(draft: false);
        var attachments = _attachments.ToList();
        var sms = _useSms.IsChecked == true;
        var targets = candidates ?? _work.Items.Where(i => !i.Attempted && NotePersonalization.IsValidBody(i.Body)).ToList();
        using var cts = new CancellationTokenSource();
        _batchCts = cts;
        _progress.Maximum = Math.Max(1, targets.Count); _progress.Value = 0;
        SetBatchRunning(true);
        try
        {
            var summary = await PersonalizedBatchDispatcher.RunAsync(
                _work,
                targets,
                reservation,
                _store,
                _automation,
                (item, order, total, text) =>
                {
                    if (_closed) return;
                    _progress.Maximum = Math.Max(1, total); _progress.Value = Math.Max(0, order - 1);
                    _grid.SelectedItem = item;
                    _grid.ScrollIntoView(item);
                    RefreshGrid();
                    ShowPreview();
                    _status.Text = text;
                },
                cts.Token,
                attachments,
                sms);

            if (!_closed)
            {
                _progress.Value = _progress.Maximum;
                RefreshGrid();
                ShowPreview();
                _status.Text = summary.Message;
            }
            return summary;
        }
        finally
        {
            _batchCts = null;
            if (!_closed)
            {
                SetBatchRunning(false);
                MarkClean();   // 발송 과정에서 저장소에 이미 기록되었다.
                ReloadSaved();
            }
        }
    }

    private void SetBatchRunning(bool running)
    {
        _running = running;
        foreach (var control in _interactiveControls)
        {
            control.IsEnabled = !running;
        }
        _stopBatchButton.IsEnabled = running;
        _progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        UpdateReservationState();
        if (!running)
        {
            ShowPreview();
        }
        else
        {
            _preview.IsReadOnly = true;
        }
        UpdateAiButtons();
    }

    // ───────────────────────── AI 문장 다듬기 ─────────────────────────

    /// <summary>AI로 고칠 수 있는 상태: 사람이 골라져 있고, 발송을 시도하지 않았고, 연속 발송·쪽지창 준비 중이 아니다.</summary>
    private bool CanRefine => !_running && _batchCts is null && _shown is { Attempted: false };

    private void UpdateAiScope()
    {
        if (DraftText.TryTrimSelection(_preview.Text, _preview.SelectionStart, _preview.SelectionLength, out _, out var len, out _))
        {
            _aiScope.Text = $"선택 구간만 변환 ({len}자)";
            _aiScope.Foreground = Res<Brush>("AccentText");
            _aiScope.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            _aiScope.Text = "블럭 지정 시 그 구간만, 없으면 이 사람의 전체 본문 변환";
            _aiScope.Foreground = Res<Brush>("Muted");
            _aiScope.FontWeight = FontWeights.Normal;
        }
    }

    private void UpdateUndoButton() =>
        _undoButton.IsEnabled = !_aiBusy && CanRefine && _undoText is not null && ReferenceEquals(_undoItem, _shown) && _undoRevision == _revision;

    private void UpdateAiButtons()
    {
        var enabled = !_aiBusy && CanRefine;
        foreach (var b in _aiButtons) b.IsEnabled = enabled;
        UpdateUndoButton();
    }

    private async void OnAiRefineAsync(AiRefinePreset preset)
    {
        if (_aiBusy || _closed || !CanRefine) return;
        var item = _shown!;
        var snapshot = _preview.Text;
        if (snapshot.Trim().Length == 0)
        {
            _status.Text = "다듬을 본문이 없습니다. ‘서식으로 본문 생성’을 누르거나 본문을 먼저 입력하세요.";
            _preview.Focus();
            return;
        }

        var selStart = _preview.SelectionStart;
        var selLength = _preview.SelectionLength;
        var isBlock = DraftText.TryTrimSelection(snapshot, selStart, selLength, out var trimmedStart, out var trimmedLength, out var selectedBlock);

        _aiBusy = true;
        UpdateAiButtons();
        _status.Text = isBlock
            ? $"로컬 AI가 {item.Name}님 본문의 선택 구간({trimmedLength}자)을 ‘{preset.Label}’ 방식으로 다듬는 중입니다..."
            : $"로컬 AI가 {item.Name}님 본문 전체를 ‘{preset.Label}’ 방식으로 다듬는 중입니다...";

        var revision = _revision;
        try
        {
            var refined = await _writer.RefineAsync(snapshot, isBlock ? selectedBlock : null, preset.Instruction, _closing.Token);
            if (_closed) return;
            if (_revision != revision || !ReferenceEquals(_shown, item))
            {
                _status.Text = "기다리는 사이 본문이나 선택한 사람이 바뀌어 AI 변환 결과를 반영하지 않았습니다.";
                return;
            }
            if (string.IsNullOrWhiteSpace(refined))
            {
                _status.Text = "AI로부터 빈 응답을 받아 본문을 유지했습니다.";
                return;
            }

            _undoItem = item;
            _undoText = snapshot;
            _undoSelectionStart = isBlock ? trimmedStart : 0;
            _undoSelectionLength = isBlock ? trimmedLength : 0;

            var applied = DraftText.ApplyRefinement(snapshot, selStart, selLength, refined);
            _preview.Text = applied.Text;
            _undoRevision = _revision;

            _preview.Focus();
            if (applied.IsBlock)
            {
                _preview.Select(applied.SelectionStart, applied.SelectionLength);
                _status.Text = $"{item.Name}님 본문의 선택한 문장을 ‘{preset.Label}’ 방식으로 바꿨습니다. 마음에 들지 않으면 ‘되돌리기’를 누르세요.";
            }
            else
            {
                _preview.CaretIndex = _preview.Text.Length;
                _status.Text = $"{item.Name}님 본문 전체를 ‘{preset.Label}’ 방식으로 다듬었습니다. 사실·날짜를 확인하세요.";
            }
        }
        catch (OperationCanceledException) { }
        catch (AnalysisUnavailableException ex) { if (!_closed) _status.Text = ex.Message; }
        finally
        {
            _aiBusy = false;
            if (!_closed) UpdateAiButtons();
        }
    }

    private void UndoAi()
    {
        if (_undoText is null || !ReferenceEquals(_undoItem, _shown) || _undoRevision != _revision) return;
        var restoreText = _undoText;
        var restoreStart = _undoSelectionStart;
        var restoreLength = _undoSelectionLength;
        _undoText = null; _undoItem = null; _undoRevision = -1;
        _preview.Text = restoreText;
        _preview.Focus();
        if (restoreLength > 0 && restoreStart + restoreLength <= _preview.Text.Length)
            _preview.Select(restoreStart, restoreLength);
        else
            _preview.CaretIndex = _preview.Text.Length;
        UpdateAiButtons();
        _status.Text = "AI 변환 전 문장으로 되돌렸습니다.";
    }

    // ───────────────────────── 표·본문 ─────────────────────────

    private static string EscapePath(string header) =>
        string.Concat(header.Select(ch => ch is '^' or '[' or ']' or ',' ? $"^{ch}" : ch.ToString()));

    private void EnsureHeaders()
    {
        if (_work.Headers.Count == 0) _work.Headers.Add(PersonalizedWork.DefaultHeader);
        foreach (var item in _work.Items)
            foreach (var header in _work.Headers)
                item.Values.TryAdd(header, "");
    }

    private void RebuildColumns()
    {
        _grid.Columns.Clear(); _headerCounters.Clear();
        var identity = new[] { ("이름", "Name", 1.4, 88d), ("소속", "Organization", 2.0, 84d), ("직급", "Title", 1.2, 62d) };
        foreach (var (title, property, weight, min) in identity)
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = title, Binding = new Binding(property), IsReadOnly = true,
                Width = new DataGridLength(weight, DataGridLengthUnitType.Star), MinWidth = min, ElementStyle = Trim(),
            });
        var editBox = new Style(typeof(TextBox), Res<Style>("FieldBox"));
        editBox.Setters.Add(new Setter(Control.MinHeightProperty, 22d));
        editBox.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 0, 5, 0)));
        foreach (var header in _work.Headers)
            _grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = HeaderCell(header), IsReadOnly = false, Width = new DataGridLength(1.8, DataGridLengthUnitType.Star), MinWidth = 130,
                CellStyle = ItemCellStyle(header), CellTemplate = ItemCellTemplate(header), CellEditingTemplate = ItemEditTemplate(header, editBox),
            });
        // 처리 상태 열은 따로 두지 않는다. 준비 상태는 항목 칸의 아이콘으로, 자세한 처리 상태는 줄에 올린 풍선 도움말과 ③에 보인다.
        var row = new Style(typeof(DataGridRow), (Style)FindResource(typeof(DataGridRow)));
        row.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Status") { StringFormat = "처리 상태: {0}" }));
        _grid.RowStyle = row;
    }

    private static SolidColorBrush Tint(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    private static readonly Brush TintEditable = Tint("#F7FAFF"), TintTodo = Tint("#FEF3F2"), TintDone = Tint("#ECFDF3"), TintWait = Tint("#FFF6E5"), PlaceholderInk = Tint("#98A2B3");
    private const string Placeholder = "입력 필요 (F2 or 더블클릭)";

    /// <summary>항목 칸의 표시: 값이 없으면 ‘입력 필요’ 힌트와 연한 붉은 바탕, 있으면 값만 보인다.
    /// 발송 시도·보류·작성 중인 줄은 처리 상태에 맞춰 바탕색만 바꾼다(초록 완료 · 주황 확인 필요 · 빨강 오류).</summary>
    private static (string Text, Brush? Ink, Brush Back) CellLook(PersonalizedWorkItem item, string header)
    {
        var value = item[header];
        var status = item.Status;
        if (item.Attempted || status.StartsWith("보류") || status.StartsWith("작성 중"))
        {
            var brush = (Brush)StatusBrush.Instance.Convert(status, typeof(Brush), "", CultureInfo.InvariantCulture);
            return (value, null, brush == StatusBrush.Done ? TintDone : brush == StatusBrush.Bad ? TintTodo : TintWait);
        }
        return string.IsNullOrWhiteSpace(value) ? (Placeholder, PlaceholderInk, TintTodo) : (value, null, TintEditable);
    }

    private sealed class CellLookConverter(int part) : IValueConverter
    {
        public static readonly CellLookConverter TextOf = new(0), InkOf = new(1), BackOf = new(2);
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not PersonalizedWorkItem item) return DependencyProperty.UnsetValue;
            var (text, ink, back) = CellLook(item, (string)parameter);
            return part switch { 0 => text, 1 => (object?)ink ?? DependencyProperty.UnsetValue, _ => back };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private Style ItemCellStyle(string header)
    {
        var style = new Style(typeof(DataGridCell), (Style)FindResource(typeof(DataGridCell)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new Binding { Converter = CellLookConverter.BackOf, ConverterParameter = header }));
        return style;
    }

    private DataTemplate ItemCellTemplate(string header)
    {
        var value = new FrameworkElementFactory(typeof(TextBlock));
        value.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        value.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        value.SetBinding(TextBlock.TextProperty, new Binding { Converter = CellLookConverter.TextOf, ConverterParameter = header });
        value.SetBinding(TextBlock.ForegroundProperty, new Binding { Converter = CellLookConverter.InkOf, ConverterParameter = header });
        value.SetBinding(FrameworkElement.ToolTipProperty, new Binding { Path = new PropertyPath("Text"), RelativeSource = RelativeSource.Self });
        return new DataTemplate { VisualTree = value };
    }

    private static DataTemplate ItemEditTemplate(string header, Style editBox)
    {
        var box = new FrameworkElementFactory(typeof(TextBox));
        box.SetValue(FrameworkElement.StyleProperty, editBox);
        box.SetBinding(TextBox.TextProperty, new Binding($"[{EscapePath(header)}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        box.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => { var tb = (TextBox)sender; tb.Focus(); tb.SelectAll(); }));
        return new DataTemplate { VisualTree = box };
    }

    /// <summary>항목 열 머리: 제목과 제목 바꾸기(✎)·삭제(✕) 단추.</summary>
    private UIElement HeaderCell(string header)
    {
        Button Tool(string glyph, string tip, Action act)
        {
            var button = new Button { Content = glyph, Style = Res<Style>("IconButton"), Width = 18, Height = 18, FontSize = 10, Margin = new Thickness(1, 0, 0, 0), ToolTip = tip };
            button.AutomationName(tip + " " + header);
            button.Click += (_, e) => { e.Handled = true; act(); };
            return button;
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 80, ToolTip = header });
        var count = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var bar = new ProgressBar { Width = 22, Height = 3, Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), Background = Res<Brush>("Line"), VerticalAlignment = VerticalAlignment.Center };
        var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 0, 2, 0), ToolTip = "값이 입력된 사람 수 / 전체 대상자 수" };
        chip.Children.Add(count); chip.Children.Add(bar);
        panel.Children.Add(chip);
        _headerCounters[header] = (count, bar);
        panel.Children.Add(Tool("", "항목 제목 바꾸기", () => RenameHeaderInteractive(header)));
        if (_work.Headers.Count > 1) panel.Children.Add(Tool("", "항목 열 삭제", () => RemoveHeaderInteractive(header)));
        return panel;
    }

    /// <summary>긴 값은 말줄임표로 줄이고 전체는 풍선 도움말로 보여 준다.</summary>
    private static Style Trim(string path = "")
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding { Path = new PropertyPath("Text"), RelativeSource = RelativeSource.Self }));
        // 선택한 줄의 배경이 연하므로 글자는 항상 진하게 유지한다.
        var selected = new DataTrigger { Binding = new Binding("IsSelected") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1) }, Value = true };
        selected.Setters.Add(new Setter(TextBlock.ForegroundProperty, (Brush)Application.Current.Resources["TextStrong"]));
        style.Triggers.Add(selected);
        return style;
    }

    private void RefreshGrid()
    {
        try { _grid.Items.Refresh(); }
        catch (InvalidOperationException) { /* 셀 편집 중에는 다음 갱신에서 반영한다. */ }
        var total = _work.Items.Count;
        var ready = _work.Items.Count(i => !i.Attempted && NotePersonalization.IsValidBody(i.Body));
        var attempted = _work.Items.Count(i => i.Attempted);
        _summary.Text = $"총 {total}명 · 발송 준비 {ready}명 · 발송 시도 {attempted}명";
        foreach (var (header, (count, bar)) in _headerCounters)
        {
            var filled = _work.Items.Count(i => !string.IsNullOrWhiteSpace(i[header]));
            var done = total > 0 && filled == total;
            count.Text = $"{filled}/{total}";
            count.Foreground = Res<Brush>(done ? "Success" : "Muted");
            bar.Maximum = Math.Max(1, total); bar.Value = filled;
            bar.Foreground = done ? StatusBrush.Done : Res<Brush>("Accent");
        }
    }

    private void Commit() { _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true); }
    private void FlushPreview() { if (_shown is { Attempted: false }) _shown.Body = _preview.Text; }

    private void ShowPreview()
    {
        _shown = _grid.SelectedItem as PersonalizedWorkItem;
        _preview.Text = _shown?.Body ?? ""; _preview.IsReadOnly = _shown is null || _shown.Attempted || _batchCts is not null;
        _selectedInfo.Text = _shown is null
            ? (_work.Items.Count == 0 ? "대상자가 없습니다. 조직도에서 대상을 고른 뒤 다시 여세요." : "②의 목록에서 사람을 고르세요.")
            : $"{_shown.Name} · {_shown.Organization} · {_shown.Title}" + (_shown.Attempted ? "  (발송을 시도해 수정할 수 없음)" : "") + $"  ·  처리 상태: {_shown.Status}";
        UpdateBodyInfo();
        UpdateAiScope();
        UpdateAiButtons();
    }

    private void UpdateBodyInfo()
    {
        if (_shown is null) { _bodyInfo.Text = ""; return; }
        var bytes = NoteBytes.Count(_preview.Text);
        _bodyInfo.Text = $"{bytes:N0} / {NoteBytes.Limit:N0} Bytes";
        _bodyInfo.Foreground = Res<Brush>(bytes > NoteBytes.Limit ? "Danger" : "Muted");
    }

    private void Load()
    {
        EnsureHeaders();
        UpdateFieldChips();
        RebuildColumns();
        _templateName.Text = _work.TemplateName;
        _template.Text = _work.Template;
        ReloadTemplates(_work.TemplateName.Length > 0 ? _work.TemplateName : null);
        _grid.ItemsSource = null;
        _grid.ItemsSource = _work.Items;
        _grid.SelectedIndex = _work.Items.Count > 0 ? 0 : -1;
        RefreshGrid();
        ShowPreview();
        ReloadSaved();
        UpdateReservationState();
        MarkClean();
    }

    private void ReloadSaved()
    {
        var works = _store.GetPersonalizedWorks();
        _saved.ItemsSource = works;
        _savedCount.Text = works.Count == 0 ? "없음" : $"{works.Count}개";
        _saved.SelectedItem = works.FirstOrDefault(w => w.Id == _work.Id);
        _savedInfo.Text = works.Count == 0
            ? "아직 저장한 작업이 없습니다. 작업은 ‘임시 저장’을 누르거나 발송할 때만 저장됩니다."
            : $"저장한 작업 {works.Count}개 · 작업은 ‘임시 저장’을 누르거나 발송할 때만 저장됩니다.";
    }

    /// <summary>화면의 서식·표·본문 입력을 작업 객체에 모은다. 저장소에는 쓰지 않는다.</summary>
    private void SyncWork()
    {
        Commit(); FlushPreview(); _work.Template = _template.Text; _work.TemplateName = _templateName.Text.Trim();
    }

    private string Snapshot() => System.Text.Json.JsonSerializer.Serialize(_work);

    /// <summary>지금 상태를 저장한 것(또는 막 불러온 것)으로 본다. 닫을 때 저장 여부를 묻는 기준이다.</summary>
    private void MarkClean() { SyncWork(); _cleanSnapshot = Snapshot(); }

    private bool IsDirty() { SyncWork(); return Snapshot() != _cleanSnapshot; }

    /// <summary>임시 저장 표시도 발송 기록도 없는 저장 작업을 확인 후 지운다. 열려 있는 작업은 건드리지 않는다.</summary>
    private void CleanUnmarkedWorks()
    {
        var targets = _store.GetPersonalizedWorks().Where(w => w.IsUnmarked && w.Id != _work.Id).ToList();
        if (targets.Count == 0) { _status.Text = "정리할 저장 작업이 없습니다. ‘임시 저장’ 표시와 발송 기록이 모두 없는 작업이 없습니다."; return; }
        var sample = string.Join("\n", targets.Take(5).Select(w => $"  · {w.Headline} ({w.Detail})")) + (targets.Count > 5 ? $"\n  외 {targets.Count - 5}개" : "");
        var prompt = $"‘임시 저장’ 표시도 발송 기록도 없는 저장 작업 {targets.Count}개를 삭제할까요?\n\n{sample}\n\n" +
            "이전 버전이 창을 닫을 때 자동 저장한 작업 등입니다. 예전에 ‘작업 저장’으로 직접 저장한 작업이 섞여 있을 수 있으니 목록을 확인하세요. 삭제하면 되돌릴 수 없습니다.";
        if (Dialogs.Show(Host, prompt, "표시 없는 저장 작업 정리", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        foreach (var work in targets) _store.DeletePersonalizedWork(work.Id);
        ReloadSaved();
        _status.Text = $"표시 없는 저장 작업 {targets.Count}개를 삭제했습니다.";
    }

    private void TempSave()
    {
        Save(draft: true);
        _status.Text = "임시 저장했습니다. 수신자 정보·입력 데이터·개별 본문을 이 계정의 암호화 저장소에 보관했고, ‘저장한 작업’에서 불러올 수 있습니다.";
    }

    /// <summary>저장소에 쓰는 곳은 ‘임시 저장’(단추·Ctrl+S)과 발송뿐이다. 그 밖의 편집은 저장하지 않는다.</summary>
    private void Save(bool draft)
    {
        SyncWork();
        _work.Draft = draft;
        _store.SavePersonalizedWork(_work);
        _cleanSnapshot = Snapshot();
        ReloadSaved();
    }

    /// <summary>임시 저장하지 않은 변경이 있으면 저장 여부를 묻는다. 계속해도 되면 true, 취소하면 false.</summary>
    private bool ConfirmLeave(string title, string action)
    {
        if (_batchCts is not null || !IsDirty()) return true;
        var answer = Dialogs.Show(Host, $"임시 저장하지 않은 변경이 있습니다.\n\n‘예’: 임시 저장하고 {action}\n‘아니오’: 저장하지 않고 {action}\n‘취소’: 계속 작업", title,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) Save(draft: true);
        return true;
    }

    public void SelectNextPending()
    {
        SyncWork();
        var next = _work.Items.FirstOrDefault(i => !i.Attempted && NotePersonalization.IsValidBody(i.Body));
        if (next is null) { _status.Text = "준비된 미발송 대상이 없습니다. 작업 상태와 각 사람의 본문을 확인하세요."; return; }
        _grid.SelectedItem = next;
        _grid.ScrollIntoView(next);
        _status.Text = $"다음 대상: {next.Name} · {next.Organization}. ‘선택한 한 사람만 발송’ 또는 ‘준비된 대상 자동 연속 발송’을 누르세요.";
        ActivateHost();
    }

    /// <summary>처리 상태 글을 색으로 구분한다: 완료 초록, 준비 파랑, 미준비 회색, 작성 중·시도 주황, 그 밖의 보류·오류 빨강.</summary>
    private sealed class StatusBrush : IValueConverter
    {
        public static readonly StatusBrush Instance = new();
        private static Brush Make(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
        internal static readonly Brush Done = Make("#12A150"), Ready = Make("#2A58E0"), Idle = Make("#98A2B3"), Wait = Make("#B54708"), Bad = Make("#D92D20");
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string ?? "") switch
        {
            var s when s.StartsWith("발송 완료") || s.StartsWith("예약 완료") => Done,
            "준비됨" => Ready,
            "미준비" or "" => Idle,
            var s when s.StartsWith("작성 중") || s.StartsWith("전송 시도") || s.StartsWith("전송 준비") => Wait,
            _ => Bad,
        };
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

internal static class PersonalizedPaneExtensions
{
    public static void AutomationName(this DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
