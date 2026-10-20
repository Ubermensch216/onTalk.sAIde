using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>쪽지 메뉴의 목록. 이 PC에서 읽어 보관한 쪽지를 카드로 보여 주고, 카드를 누르면 아래로 펼쳐 전체 내용을 보여 준다.</summary>
public sealed class NotesPane : UserControl, IDisposable
{
    private const int ListLimit = 100;
    private const int BriefLength = 50;
    private static readonly Duration Slide = new(TimeSpan.FromMilliseconds(180));

    private readonly ProfileStore _store;
    private readonly TextBox _query = new() { Tag = "이름, 날짜, 쪽지 내용으로 찾기" };
    private readonly ComboBox _period = new();
    private readonly CheckBox _attachmentOnly = new() { Content = "첨부 있음", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new();
    private readonly StackPanel _cards = new();
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<long, NoteCard> _byId = [];
    private NoteCard? _open;
    private long? _openId;
    private string _signature = "";
    private bool _active, _disposed;

    public NotesPane(ProfileStore store, Action newNote)
    {
        _store = store;
        Background = Brush("PageBg"); Foreground = Brush("TextStrong"); FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(12) };
        var compose = new Button { Content = "새 쪽지", Style = (Style)FindResource("PrimaryButton") };
        compose.Click += (_, _) => newNote();
        root.Children.Add(compose);

        _query.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(_query);

        foreach (var label in new[] { "전체 기간", "최근 7일", "최근 30일", "올해" }) _period.Items.Add(label);
        _period.SelectedIndex = 0;
        var filters = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _attachmentOnly.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(_attachmentOnly, Dock.Right);
        filters.Children.Add(_attachmentOnly); filters.Children.Add(_period);
        root.Children.Add(filters);

        _status.Style = (Style)FindResource("Caption"); _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new Thickness(2, 10, 2, 8);
        root.Children.Add(_status);
        root.Children.Add(_cards);

        _scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = _scroll;

        _query.TextChanged += (_, _) => Reload(force: true);
        _period.SelectionChanged += (_, _) => Reload(force: true);
        _attachmentOnly.Click += (_, _) => Reload(force: true);
        _timer.Tick += (_, _) => { if (_active && !_disposed) Reload(force: false); };
        Reload(force: true);
    }

    public void SetActive(bool active)
    {
        var changed = _active != active;
        _active = active;
        IsEnabled = active && !_disposed;
        if (active && !_disposed)
        {
            if (changed) Reload(force: false);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _active = false; _timer.Stop(); IsEnabled = false;
    }

    private DateTimeOffset? Since()
    {
        var now = DateTimeOffset.Now.ToOffset(KoreanNoteTime.SeoulOffset);
        return _period.SelectedIndex switch
        {
            1 => now.AddDays(-7),
            2 => now.AddDays(-30),
            3 => new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, KoreanNoteTime.SeoulOffset),
            _ => null,
        };
    }

    /// <summary>검색 조건이 같고 보관한 쪽지가 그대로면 다시 그리지 않아 펼친 카드와 스크롤을 지킨다.</summary>
    private void Reload(bool force)
    {
        if (_disposed) return;
        var range = _store.GetCollectionRange();
        var terms = NoteQuery.SplitTerms(_query.Text);
        var query = new NoteQuery(terms, Since(), _attachmentOnly.IsChecked == true);
        var signature = $"{range.Count}|{string.Join(' ', terms)}|{_period.SelectedIndex}|{query.WithAttachmentOnly}";
        if (!force && signature == _signature) return;
        _signature = signature;

        var notes = _store.SearchNotes(query, ListLimit);
        _cards.Children.Clear();
        _byId.Clear();
        _open = null;
        foreach (var stored in notes)
        {
            var card = new NoteCard(this, stored);
            _byId[stored.Id] = card;
            _cards.Children.Add(card.Root);
        }
        if (_openId is { } id && _byId.TryGetValue(id, out var keep)) { _open = keep; keep.SetOpen(true, animate: false); }
        else _openId = null;

        var filtered = terms.Count > 0 || query.Since is not null || query.WithAttachmentOnly;
        _status.Text = range.Count == 0
            ? "아직 보관한 쪽지가 없습니다. 온톡에서 쪽지를 열면 이 목록에 들어갑니다."
            : notes.Count == 0
                ? "조건에 맞는 보관 쪽지가 없습니다."
                : (filtered ? $"찾은 쪽지 {notes.Count}건" : $"보관한 쪽지 {range.Count}건") +
                  (notes.Count >= ListLimit ? $" · 최근 {ListLimit}건만 표시" : "") + " · 이 PC에서 연 쪽지만 보관됩니다";
    }

    /// <summary>한 번에 한 카드만 펼친다. 이미 펼친 카드를 다시 누르면 접는다.</summary>
    private void Toggle(NoteCard card)
    {
        var opening = !ReferenceEquals(_open, card);
        _open?.SetOpen(false, animate: true);
        _open = opening ? card : null;
        _openId = opening ? card.Id : null;
        if (opening)
        {
            card.SetOpen(true, animate: true);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => card.Root.BringIntoView());
        }
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private sealed class NoteCard
    {
        private readonly NotesPane _pane;
        private readonly StoredNote _stored;
        private readonly TextBlock _chevron;
        private readonly Border _detailHost;
        private FrameworkElement? _detail;
        private bool _isOpen;

        public long Id => _stored.Id;
        public Border Root { get; }

        public NoteCard(NotesPane pane, StoredNote stored)
        {
            _pane = pane;
            _stored = stored;
            var note = stored.Note;

            var name = new TextBlock { Text = note.SenderName, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
            var when = new TextBlock
            {
                Text = note.SentAt is { } t ? t.Value.ToString("yyyy-MM-dd HH:mm") : note.SentAtText,
                Style = (Style)pane.FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(8, 0, 0, 0),
            };
            _chevron = new TextBlock
            {
                Text = "", FontFamily = (FontFamily)pane.FindResource("IconFont"), FontSize = 11,
                Foreground = pane.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(0),
            };
            var top = new DockPanel();
            DockPanel.SetDock(_chevron, Dock.Right); DockPanel.SetDock(when, Dock.Right);
            top.Children.Add(_chevron); top.Children.Add(when); top.Children.Add(name);

            var brief = Snippet.Brief(note.Body, BriefLength);
            if (note.AttachmentNames.Count > 0) brief = (brief.Length > 0 ? brief + "  " : "") + "📎" + note.AttachmentNames.Count;
            var preview = new TextBlock
            {
                Text = brief.Length > 0 ? brief : "(내용 없음)", Style = (Style)pane.FindResource("Small"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = pane.Brush("Muted"),
            };

            _detailHost = new Border { Height = 0, Visibility = Visibility.Collapsed, ClipToBounds = true };
            var stack = new StackPanel();
            stack.Children.Add(top); stack.Children.Add(preview); stack.Children.Add(_detailHost);

            Root = new Border
            {
                Style = (Style)pane.FindResource("Card"), Padding = new Thickness(12, 10, 12, 10), Child = stack,
                Cursor = System.Windows.Input.Cursors.Hand, Background = pane.Brush("Surface"),
            };
            // 펼친 본문 안에서 글자를 고르는 클릭은 접지 않도록 머리 부분과 미리보기에서만 반응한다.
            top.Background = Brushes.Transparent;
            top.MouseLeftButtonUp += (_, _) => _pane.Toggle(this);
            preview.MouseLeftButtonUp += (_, _) => _pane.Toggle(this);
        }

        public void SetOpen(bool open, bool animate)
        {
            if (_isOpen == open) return;
            _isOpen = open;
            _chevron.RenderTransform.BeginAnimation(RotateTransform.AngleProperty,
                animate ? new DoubleAnimation(open ? 180 : 0, Slide) : null);
            if (!animate) ((RotateTransform)_chevron.RenderTransform).Angle = open ? 180 : 0;
            Root.BorderBrush = _pane.Brush(open ? "Accent" : "Line");

            if (open)
            {
                _detail ??= BuildDetail();
                _detailHost.Child = _detail;
                _detailHost.Visibility = Visibility.Visible;
                if (!animate) { _detailHost.BeginAnimation(HeightProperty, null); _detailHost.Height = double.NaN; return; }
                var width = Math.Max(120, _pane.ActualWidth - 24 - 24 - 2);
                _detail.Measure(new Size(width, double.PositiveInfinity));
                var target = _detail.DesiredSize.Height;
                var grow = new DoubleAnimation(0, target, Slide) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                grow.Completed += (_, _) => { if (_isOpen) { _detailHost.BeginAnimation(HeightProperty, null); _detailHost.Height = double.NaN; } };
                _detailHost.BeginAnimation(HeightProperty, grow);
            }
            else if (!animate)
            {
                _detailHost.BeginAnimation(HeightProperty, null);
                _detailHost.Height = 0; _detailHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                var from = _detailHost.ActualHeight;
                var shrink = new DoubleAnimation(from, 0, Slide) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                shrink.Completed += (_, _) => { if (!_isOpen) { _detailHost.Visibility = Visibility.Collapsed; } };
                _detailHost.BeginAnimation(HeightProperty, shrink);
            }
        }

        private FrameworkElement BuildDetail()
        {
            var note = _stored.Note;
            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(new Border { Height = 1, Background = _pane.Brush("Line"), Margin = new Thickness(0, 0, 0, 10) });

            var sub = new List<string>();
            if (note.SenderDetail.Length > 0) sub.Add(note.SenderDetail);
            sub.Add(note.SentAt switch
            {
                null => note.SentAtText,
                { YearInferred: true } t => $"{t.Value:yyyy-MM-dd HH:mm} (연도는 추정)",
                { } t => $"{t.Value:yyyy-MM-dd HH:mm}",
            });
            panel.Children.Add(new TextBlock { Text = string.Join("\n", sub), Style = (Style)_pane.FindResource("Small"), TextWrapping = TextWrapping.Wrap });

            panel.Children.Add(new TextBox
            {
                Text = note.Body, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 10, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Cursor = System.Windows.Input.Cursors.IBeam,
            });

            if (note.AttachmentNames.Count > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = $"첨부 파일 이름: {string.Join(", ", note.AttachmentNames)}", Style = (Style)_pane.FindResource("Caption"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
                });
            panel.Children.Add(new TextBlock
            {
                Text = $"{_stored.FirstSeenAt.ToLocalTime():yyyy-MM-dd HH:mm}에 보관한 사본입니다. 온톡의 원본은 이미 삭제되었을 수 있습니다.",
                Style = (Style)_pane.FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            });
            return panel;
        }
    }
}
