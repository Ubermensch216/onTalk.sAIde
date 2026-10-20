using System.Windows;
using System.Windows.Controls;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>쪽지 작업 창이 처음 보여 줄 탭.</summary>
public enum NoteTab { Note, Personalized }

/// <summary>
/// 쪽지 보내기 창의 촘촘한 화면 값. 창은 900×900의 2/3(600×600)로 줄였고, 높이·여백은 그에 비례해 줄이되
/// 글자와 누르는 칸은 읽고 누르기 어려워지지 않도록 덜 줄였다(글자 13→12, 단추·입력칸 32~34→22~26).
/// 그래도 작게 느껴져 화면 전체에 최대 <see cref="MaxScale"/>배를 한 번에 건다. 화면 안의 모든 값은 기준 크기 그대로이고,
/// 창이 그만큼 커져 글자·단추·여백·아이콘이 같은 비율로 커진다(레이아웃은 달라지지 않는다).
/// </summary>
internal static class Density
{
    /// <summary>화면 전체 확대 배율의 상한. 작업 영역이 모자라면 <see cref="Fit"/>이 이보다 낮춘다.</summary>
    public const double MaxScale = 1.0;

    /// <summary>이보다 낮추지는 않는다. 그래도 모자라면 창이 작업 영역에 맞춰지고 세로로 스크롤된다.</summary>
    public const double MinScale = 1.0;

    /// <summary>
    /// 배율을 걸기 전의 배치 크기. 높이는 ‘쪽지’ 탭의 모든 구역(받는 사람·본문·첨부 파일·발송 방식)과 아래 단추 줄이
    /// 스크롤 없이 한눈에 들어오는 값이다(두 사람을 받는 사람으로 넣은 경우를 잰 822에 여유를 더함).
    /// 어느 탭으로 열든 같은 크기로 연다.
    /// </summary>
    public const double LayoutWidth = 580, LayoutHeight = 820, MinLayoutWidth = 400, MinLayoutHeight = 380;

    /// <summary>창 틀(제목 줄과 테두리)이 차지하는 크기. 화면에 거는 배율과 무관하다.</summary>
    public static (double Width, double Height) Frame =>
        (2 * SystemParameters.ResizeFrameVerticalBorderWidth + 8,
         SystemParameters.WindowCaptionHeight + 2 * SystemParameters.ResizeFrameHorizontalBorderHeight + 8);

    /// <summary>작업 영역 안에 기본 크기가 다 들어가도록 정한 배율. 넉넉하면 <see cref="MaxScale"/>.</summary>
    public static double Fit(double workWidth, double workHeight)
    {
        var (frameWidth, frameHeight) = Frame;
        var fit = Math.Min((workWidth - 40 - frameWidth) / LayoutWidth, (workHeight - 40 - frameHeight) / LayoutHeight);
        return Math.Clamp(fit, MinScale, MaxScale);
    }

    /// <summary>화면 뿌리에 확대를 건다. 배치가 확대된 뒤 기준 크기로 이루어져 레이아웃이 보존된다.</summary>
    public static void ApplyScale(FrameworkElement root, double scale) =>
        root.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);

    public static IEnumerable<KeyValuePair<string, object>> Compact { get; } =
    [
        new("CtlMinHeight", 26d), new("CtlPadding", new Thickness(10, 0, 10, 0)),
        new("SmallMinHeight", 22d), new("SmallPadding", new Thickness(8, 0, 8, 0)),
        new("FieldMinHeight", 26d), new("FieldPadding", new Thickness(8, 0, 8, 0)),
        new("MultilinePadding", new Thickness(8, 6, 8, 6)),
        new("ComboPadding", new Thickness(8, 0, 24, 0)), new("ComboItemPadding", new Thickness(8, 4, 8, 4)),
        new("GridHeaderHeight", 24d), new("GridRowMinHeight", 24d), new("GridCellPadding", new Thickness(8, 0, 8, 0)),
        new("TabHeight", 24d), new("TabRadius", new CornerRadius(12)), new("TabBarRadius", new CornerRadius(15)),
        new("TabBarPadding", new Thickness(2)), new("TabBarMargin", new Thickness(8, 0, 8, 4)),
        new("CheckSize", 15d),
        new("FontSmall", 12d), new("LineSmall", 16d), new("FontCaption", 11d), new("LineCaption", 15d),
        new("FontPageTitle", 15d),
    ];
}

/// <summary>
/// 쪽지 보내기 창. ‘쪽지’(같은 내용을 모두에게)와 ‘개인별 쪽지’(사람마다 다른 내용) 두 화면을 탭으로 묶는다.
/// 조직도에서 고른 직원은 두 탭 모두에 같은 받는 사람·대상자로 들어간다.
/// </summary>
public sealed class NoteHostWindow : Window
{
    /// <summary>기본 창의 배율 전 배치 크기와 최대 배율(미리보기 검사용).</summary>
    public const double DefaultLayoutWidth = Density.LayoutWidth, DefaultLayoutHeight = Density.LayoutHeight, MaxScale = Density.MaxScale;

    private readonly TabControl? _tabs;
    private readonly List<(TabItem? Tab, WorkPane Pane)> _items = [];
    private WorkPane? _current;
    private bool _reverting;
    private double _scale = Density.MaxScale;

    /// <summary>쪽지 탭 화면. 개인별 쪽지만 담은 창에서는 null.</summary>
    public GroupNotePane? Note { get; }

    /// <summary>개인별 쪽지 탭 화면. 쪽지만 담은 창에서는 null.</summary>
    public PersonalizedPane? Personalized { get; }

    /// <summary>두 탭을 모두 가진 쪽지 창. <paramref name="preparation"/>의 직원이 두 탭에 똑같이 배치된다.</summary>
    public NoteHostWindow(
        ProfileStore store,
        OllamaWriter writer,
        ContactPreparation? preparation = null,
        StoredNote? source = null,
        NoteTab initial = NoteTab.Note,
        IPersonalizedAutomation? automation = null)
        : this(
            new GroupNotePane(store, writer, preparation, automation, source),
            new PersonalizedPane(store, writer, preparation?.People ?? [], "", automation: automation),
            initial)
    {
    }

    public NoteHostWindow(GroupNotePane note, PersonalizedPane personalized, NoteTab initial = NoteTab.Note)
    {
        (Note, Personalized) = (note, personalized);
        Title = "쪽지 보내기";
        Configure();
        _tabs = new TabControl { Style = (Style)FindResource("PillTabs"), Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (header, pane) in new (string, WorkPane)[] { ("쪽지", note), ("개인별 쪽지", personalized) })
        {
            var tab = new TabItem { Header = header, Content = pane };
            _items.Add((tab, pane));
            _tabs.Items.Add(tab);
            Wire(pane);
        }
        _tabs.SelectedIndex = initial == NoteTab.Personalized ? 1 : 0;
        _current = _items[_tabs.SelectedIndex].Pane;
        _tabs.SelectionChanged += OnTabChanged;
        Density.ApplyScale(_tabs, _scale);
        Content = _tabs;
        Hook();
    }

    /// <summary>개인별 쪽지 화면 하나만 담는 창(탭 없음).</summary>
    public NoteHostWindow(PersonalizedPane personalized)
    {
        Personalized = personalized;
        Title = "개인별 쪽지 · 작성과 미리보기";
        Configure();
        _items.Add((null, personalized));
        _current = personalized;
        Wire(personalized);
        Density.ApplyScale(personalized, _scale);
        Content = personalized;
        Hook();
    }

    private void Configure()
    {
        // 쪽지 탭이 한눈에 들어오는 크기로 연다. 개인별 쪽지 탭으로 열어도 같은 크기다.
        var work = SystemParameters.WorkArea;
        _scale = Density.Fit(work.Width, work.Height);
        var (frameWidth, frameHeight) = Density.Frame;
        Width = Math.Min(Density.LayoutWidth * _scale + frameWidth, work.Width - 40);
        Height = Math.Min(Density.LayoutHeight * _scale + frameHeight, work.Height - 40);
        MinWidth = Math.Min(Density.MinLayoutWidth * _scale + frameWidth, Width);
        MinHeight = Math.Min(Density.MinLayoutHeight * _scale + frameHeight, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource("AppWindow");
        FontSize = 12;
        // 작은 창에 맞춰 단추·입력칸·탭의 높이와 안쪽 여백을 줄인다. 키는 Theme.xaml의 밀도 값이고, 이 창 안에서만 바뀐다.
        foreach (var (key, value) in Density.Compact) Resources[key] = value;
    }

    private void Wire(WorkPane pane)
    {
        pane.ActivateRequested += () =>
        {
            Show();
            if (_tabs is not null && _items.FirstOrDefault(i => ReferenceEquals(i.Pane, pane)).Tab is { } tab) _tabs.SelectedItem = tab;
            Activate();
        };
        pane.HideRequested += () => { Hide(); Owner?.Activate(); };
    }

    private void Hook()
    {
        Activated += (_, _) => _current?.HostActivated();
        Closing += (_, e) =>
        {
            foreach (var (tab, pane) in _items)
            {
                if (pane.QueryClose()) continue;
                // 닫기를 취소한 쪽 화면을 보여 주어 무엇을 이어서 할지 알 수 있게 한다.
                if (tab is not null && _tabs is not null && !(_current?.IsBusy ?? false)) _tabs.SelectedItem = tab;
                e.Cancel = true;
                return;
            }
        };
        Closed += (_, _) => { foreach (var (_, pane) in _items) pane.HostClosed(); };
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // 탭 안의 목록·콤보 상자에서 올라온 선택 변경은 탭 전환이 아니다.
        if (_reverting || !ReferenceEquals(e.OriginalSource, _tabs)) return;
        var next = (_tabs!.SelectedItem as TabItem)?.Content as WorkPane;
        if (_current is { IsBusy: true } busy && !ReferenceEquals(busy, next))
        {
            // 발송·준비가 도는 동안 다른 탭의 단추로 온톡 창을 동시에 건드리지 않도록 되돌린다.
            _reverting = true;
            try { _tabs.SelectedItem = _items.First(i => ReferenceEquals(i.Pane, busy)).Tab; }
            finally { _reverting = false; }
            Dialogs.Show(this, "발송·준비가 진행 중입니다. 끝나거나 중단한 뒤에 탭을 바꿀 수 있습니다.", "쪽지 보내기");
            return;
        }
        _current = next;
        _current?.HostActivated();
    }
}
