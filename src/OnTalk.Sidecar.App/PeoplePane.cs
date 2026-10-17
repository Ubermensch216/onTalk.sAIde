using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.App;

/// <summary>
/// 온톡 조직도에서 사용자가 선택해 둔 직원을 보여 주고 같은 쪽지 또는 개인별 쪽지를 준비한다.
/// 온톡이 선택 상태를 알려 주지 않거나 추가 대상이 필요한 경우 직접 추가할 수 있다.
/// </summary>
public sealed class PeoplePane : UserControl, IDisposable
{
    private readonly Action<ContactPreparation, bool> _open;
    private readonly TextBlock _selection = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _emptyNotice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly StackPanel _rows = new();
    private readonly StackPanel _candidates = new();
    private readonly Button _addButton = new();
    private readonly Border _addSection = new();
    private readonly TextBox _searchBox = new();
    private readonly Button _same = new() { Content = "선택 직원에게 쪽지 보내기", Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _personal = new() { Content = "선택 직원에게 개인별 쪽지 보내기", Margin = new Thickness(0, 6, 0, 0) };
    private readonly HashSet<string> _excluded = [];
    private readonly Dictionary<string, ContactPerson> _manual = [];
    private PeopleSnapshot _snapshot = new(MainMenu.Organization, [], [], [], DateTimeOffset.Now);
    private List<(ContactPerson Person, bool Native)> _selected = [];
    private string _nativeSignature = "\0", _rowsSignature = "\0", _candidatesSignature = "\0";
    private bool _disposed, _active = true;

    public PeoplePane(Action<ContactPreparation, bool> open)
    {
        _open = open;
        var body = new StackPanel { Margin = new Thickness(12) };

        // 1. 선택한 직원 카드 레이어 (배경·테두리·그림자로 명확히 구분)
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12)
        };
        var cardContent = new StackPanel();

        // 헤더: 선택 직원 현황 라벨 + 우측 상단 '추가(아이콘 only)' 단추
        var headerDock = new DockPanel { LastChildFill = true };
        _addButton.Style = (Style)FindResource("IconButton");
        _addButton.FontFamily = (FontFamily)FindResource("IconFont");
        _addButton.Content = "\uE710"; // Segoe MDL2 Assets Add '+' 아이콘
        _addButton.FontSize = 13;
        _addButton.Width = 28;
        _addButton.Height = 28;
        _addButton.ToolTip = "직원 직접 추가";
        _addButton.Click += (_, _) =>
        {
            if (_addSection.Visibility == Visibility.Visible)
            {
                _addSection.Visibility = Visibility.Collapsed;
            }
            else
            {
                _addSection.Visibility = Visibility.Visible;
                _searchBox.Text = "";
                _searchBox.Focus();
            }
        };
        DockPanel.SetDock(_addButton, Dock.Right);
        headerDock.Children.Add(_addButton);

        _selection.Style = (Style)FindResource("SectionTitle");
        _selection.Margin = new Thickness(0, 4, 8, 4);
        headerDock.Children.Add(_selection);
        cardContent.Children.Add(headerDock);

        // 선택 직원이 없을 때의 안내 문구
        _emptyNotice.Style = (Style)FindResource("Caption");
        _emptyNotice.Text = "온톡 조직도에서 직원이나 부서를 선택하세요. 우측 상단의 [+] 버튼을 눌러 직원을 직접 추가할 수도 있습니다.";
        cardContent.Children.Add(_emptyNotice);

        // 선택 직원 목록 스크롤 영역
        cardContent.Children.Add(new ScrollViewer
        {
            Content = _rows,
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0)
        });
        card.Child = cardContent;
        body.Children.Add(card);

        // 2. 직원 직접 추가 레이어 (기존 직접 고르기 Expander를 해체하여 '+' 버튼과 연동된 인라인 패널로 반영)
        _addSection.Background = (Brush)FindResource("SurfaceAlt");
        _addSection.BorderBrush = (Brush)FindResource("Line");
        _addSection.BorderThickness = new Thickness(1);
        _addSection.CornerRadius = new CornerRadius(8);
        _addSection.Padding = new Thickness(10);
        _addSection.Margin = new Thickness(0, 0, 0, 8);
        _addSection.Visibility = Visibility.Collapsed;

        var addPanel = new StackPanel();
        var addHeader = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        var closeAdd = new Button
        {
            Style = (Style)FindResource("IconButton"),
            FontFamily = (FontFamily)FindResource("IconFont"),
            Content = "\uE711",
            FontSize = 11,
            Width = 24,
            Height = 24,
            ToolTip = "닫기"
        };
        closeAdd.Click += (_, _) => _addSection.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(closeAdd, Dock.Right);
        addHeader.Children.Add(closeAdd);

        var addTitle = Label("직원 직접 추가", "SectionTitle");
        addTitle.Margin = new Thickness(0, 2, 0, 0);
        addHeader.Children.Add(addTitle);
        addPanel.Children.Add(addHeader);

        _searchBox.Style = (Style)FindResource("FieldBox");
        _searchBox.Tag = "이름 또는 부서 검색...";
        _searchBox.Margin = new Thickness(0, 0, 0, 6);
        _searchBox.MinHeight = 30;
        _searchBox.TextChanged += (_, _) => FilterCandidates();
        addPanel.Children.Add(_searchBox);

        addPanel.Children.Add(new ScrollViewer
        {
            Content = _candidates,
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        _addSection.Child = addPanel;
        body.Children.Add(_addSection);

        // 3. 쪽지 발송 버튼
        _same.Click += (_, _) => Open(false);
        _personal.Click += (_, _) => Open(true);
        body.Children.Add(_same);
        body.Children.Add(_personal);

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Update();
    }

    private TextBlock Label(string text, string style) => new() { Text = text, Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap };

    /// <summary>쪽지 대상. 온톡에서 선택했거나 직접 고른 직원 중 제외하지 않은 사람.</summary>
    public IReadOnlyList<ContactPerson> Chosen => _selected.Where(s => !_excluded.Contains(s.Person.SelectionKey)).Select(s => s.Person).ToList();
    public string SelectionNotice => _selection.Text;

    private void Open(bool personalized)
    {
        var chosen = Chosen;
        if (_disposed || !_active || chosen.Count == 0) return;
        _open(new ContactPreparation(chosen, "온톡 조직도 선택 · " + _snapshot.Coverage,
            chosen.Select(p => p.DirectoryObservedAt ?? _snapshot.ObservedAt).Min()), personalized);
    }

    public void Refresh(PeopleSnapshot snapshot)
    {
        _snapshot = snapshot;
        var native = snapshot.People.Where(p => snapshot.SelectedKeys.Contains(p.Key)).Select(p => p with { DirectoryObservedAt = snapshot.ObservedAt }).ToList();
        // 온톡의 선택이 바뀌면 이전 제외 표시는 버린다. 온라인 상태 같은 변화는 선택을 바꾸지 않는다.
        var signature = string.Join("|", native.Select(p => p.SelectionKey));
        if (signature != _nativeSignature) { _nativeSignature = signature; _excluded.Clear(); }
        // 직접 고른 사람은 화면에서 접히거나 사라져도 보관하되, 같은 행의 다른 사람으로 바뀐 경우에는 따라가지 않는다.
        foreach (var person in snapshot.People.Where(p => _manual.ContainsKey(p.SelectionKey))) _manual[person.SelectionKey] = person with { DirectoryObservedAt = snapshot.ObservedAt };
        _selected = native.Select(p => (p, true)).Concat(_manual.Values.Where(m => native.All(n => n.SelectionKey != m.SelectionKey)).Select(m => (m, false))).ToList();
        Update();
    }

    private void Update()
    {
        var chosen = Chosen;
        var rowsSignature = string.Join("|", _selected.Select(s => s.Person.SelectionKey + s.Native + _excluded.Contains(s.Person.SelectionKey)));
        if (rowsSignature != _rowsSignature)
        {
            _rowsSignature = rowsSignature;
            _rows.Children.Clear();
            foreach (var (person, native) in _selected)
            {
                var excluded = _excluded.Contains(person.SelectionKey);
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

                // 제외 버튼: 텍스트 없는 아이콘(X)으로 표시 (제외 시 되돌리기 아이콘)
                var toggle = new Button
                {
                    Style = (Style)FindResource("IconButton"),
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    Content = excluded ? "\uE72B" : "\uE711",
                    FontSize = 11,
                    Width = 26,
                    Height = 26,
                    ToolTip = excluded ? "다시 포함" : "제외",
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(toggle, Dock.Right);
                toggle.Click += (_, _) => { if (!_excluded.Remove(person.SelectionKey)) _excluded.Add(person.SelectionKey); Update(); };
                row.Children.Add(toggle);

                var labelText = person.Label + (native ? "" : "\n직접 선택") + (excluded ? "\n(제외됨)" : "");
                var label = Label(labelText, excluded ? "Caption" : "Body");
                if (excluded) label.Opacity = 0.5;
                row.Children.Add(label);

                _rows.Children.Add(row);
            }
        }

        _selection.Text = _selected.Count == 0
            ? "선택된 직원을 확인하지 못했습니다."
            : $"선택한 직원 {_selected.Count}명 · 쪽지 대상 {chosen.Count}명";
        _emptyNotice.Visibility = _selected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _same.IsEnabled = _personal.IsEnabled = chosen.Count > 0;

        var candidatesSignature = string.Join("|", _snapshot.People.Select(p => p.SelectionKey));
        if (candidatesSignature != _candidatesSignature)
        {
            _candidatesSignature = candidatesSignature;
            _candidates.Children.Clear();
            foreach (var person in _snapshot.People)
            {
                var check = new CheckBox
                {
                    Content = Label(person.Label, "Body"),
                    IsChecked = _manual.ContainsKey(person.SelectionKey),
                    Margin = new Thickness(0, 5, 0, 5),
                    Tag = person
                };
                check.Click += (_, _) =>
                {
                    if (check.IsChecked == true) _manual[person.SelectionKey] = person with { DirectoryObservedAt = _snapshot.ObservedAt };
                    else _manual.Remove(person.SelectionKey);
                    Refresh(_snapshot);
                };
                _candidates.Children.Add(check);
            }
            if (_snapshot.People.Count == 0)
            {
                _candidates.Children.Add(Label("읽은 직원이 없습니다. 온톡에서 부서를 펼치세요.", "Small"));
            }
        }
        else
        {
            foreach (var check in _candidates.Children.OfType<CheckBox>())
            {
                if (check.Tag is ContactPerson p)
                    check.IsChecked = _manual.ContainsKey(p.SelectionKey);
            }
        }
        FilterCandidates();
    }

    private void FilterCandidates()
    {
        var query = _searchBox.Text.Trim();
        foreach (var element in _candidates.Children)
        {
            if (element is CheckBox check && check.Tag is ContactPerson person)
            {
                var match = string.IsNullOrEmpty(query) ||
                            person.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            person.Label.Contains(query, StringComparison.OrdinalIgnoreCase);
                check.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    public void SetActive(bool active) { _active = active; IsEnabled = active && !_disposed; }
    public void Dispose() { if (_disposed) return; _disposed = true; IsEnabled = false; }
}
