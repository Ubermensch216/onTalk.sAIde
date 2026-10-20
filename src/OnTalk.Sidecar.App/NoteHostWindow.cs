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
/// </summary>
internal static class Density
{
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
        new("FontSmall", 11d), new("LineSmall", 15d), new("FontCaption", 10.5), new("LineCaption", 14d),
        new("FontPageTitle", 15d),
    ];
}

/// <summary>
/// 쪽지 보내기 창. ‘쪽지’(같은 내용을 모두에게)와 ‘개인별 쪽지’(사람마다 다른 내용) 두 화면을 탭으로 묶는다.
/// 조직도에서 고른 직원은 두 탭 모두에 같은 받는 사람·대상자로 들어간다.
/// </summary>
public sealed class NoteHostWindow : Window
{
    private readonly TabControl? _tabs;
    private readonly List<(TabItem? Tab, WorkPane Pane)> _items = [];
    private WorkPane? _current;
    private bool _reverting;

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
        Content = personalized;
        Hook();
    }

    private void Configure()
    {
        Width = 600; Height = Math.Min(600, SystemParameters.WorkArea.Height - 40); MinWidth = 540; MinHeight = 380;
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
