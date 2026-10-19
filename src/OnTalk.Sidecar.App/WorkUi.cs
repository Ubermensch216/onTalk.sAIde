using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OnTalk.Sidecar.App;

/// <summary>작업 창(개인별 쪽지·선택 직원 쪽지)이 함께 쓰는 화면 조각: 번호 붙은 접이식 카드와 ‘?’ 안내 풍선.</summary>
internal static class WorkUi
{
    /// <summary>번호가 붙은 작업 카드. 제목·설명·오른쪽 도구를 머리에, 내용을 아래에 둔다.</summary>
    public static Border Section(FrameworkElement res, string? step, string title, string hint, UIElement body, UIElement? right = null)
    {
        T Res<T>(string key) => (T)res.FindResource(key);
        var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (step is not null)
            head.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Res<Brush>("Accent"),
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = step, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 14 });
        if (hint.Length > 0)
            texts.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Style = Res<Style>("Small"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(texts, 1); head.Children.Add(texts);
        if (right is not null)
        {
            if (right is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Top; fe.Margin = new Thickness(12, fe is Button ? 0 : 4, 0, 0); }
            Grid.SetColumn(right, 2); head.Children.Add(right);
        }
        // 모든 구역은 접고 펼 수 있다(기본은 펼침). 오른쪽 화살표 단추나 제목 글을 누르면 바뀐다.
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var chevron = new Button
        {
            Content = "", Style = Res<Style>("IconButton"), Width = 28, Height = 28, FontSize = 11, ToolTip = "접기/펼치기",
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        chevron.AutomationName(title + " 접기 또는 펼치기");
        void Toggle()
        {
            var open = body.Visibility != Visibility.Visible;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Content = open ? "" : "";
            head.Margin = new Thickness(0, 0, 0, open ? 12 : 0);
        }
        chevron.Click += (_, _) => Toggle();
        texts.Background = Brushes.Transparent; texts.Cursor = System.Windows.Input.Cursors.Hand;
        texts.MouseLeftButtonUp += (_, _) => Toggle();
        Grid.SetColumn(chevron, 3); head.Children.Add(chevron);
        var stack = new StackPanel();
        stack.Children.Add(head); stack.Children.Add(body);
        return new Border { Style = Res<Style>("Card"), Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 0, 0, 12), Child = stack };
    }

    /// <summary>‘?’ 단추. 누르면 안내 풍선이 열리고, 다른 곳을 누르면 닫힌다.</summary>
    public static Button HelpButton(Control res, string title, IEnumerable<string> lines, double width = 360)
    {
        T Res<T>(string key) => (T)res.FindResource(key);
        var guide = new StackPanel { Width = width };
        guide.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var line in lines)
            guide.Children.Add(new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) });
        // 아이콘 글꼴(IconButton)을 그대로 두면 풍선 안 글자까지 물려받아 깨지므로, 단추와 풍선 모두 창의 글꼴을 직접 지정한다.
        var button = new Button
        {
            Content = "?", Style = Res<Style>("IconButton"), FontFamily = res.FontFamily, FontSize = 14, FontWeight = FontWeights.Bold, Width = 24, Height = 24,
            ToolTip = title, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        button.AutomationName(title);
        var popup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.Bottom, HorizontalOffset = 28 - width - 28,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = Res<Brush>("Surface"), BorderBrush = Res<Brush>("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14, 16, 10), Margin = new Thickness(0, 4, 14, 14), Effect = Res<System.Windows.Media.Effects.Effect>("PopupShadow"), Child = guide,
            },
        };
        System.Windows.Documents.TextElement.SetFontFamily(popup.Child, res.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(popup.Child, 13);
        System.Windows.Documents.TextElement.SetForeground(popup.Child, Res<Brush>("TextStrong"));
        button.Click += (_, _) => popup.IsOpen = true;
        return button;
    }
}
