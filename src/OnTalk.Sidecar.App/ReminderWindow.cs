using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OnTalk.Sidecar.App;

/// <summary>마감 알림. 앱이 꺼져 있던 동안 지나간 알림도 한 창에 모아 보여 준다.</summary>
public sealed class ReminderWindow : Window
{
    public ReminderWindow(IReadOnlyList<TaskRow> tasks, Action openTasks)
    {
        Style = (Style)Application.Current.Resources["AppWindow"];
        Title = "마감 알림 · onTalk.sAIde";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 560;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var heading = new DockPanel { Margin = new Thickness(20, 18, 20, 12) };
        heading.Children.Add(new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = (Brush)Application.Current.Resources["WarnSoft"],
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
                FontSize = 16,
                Foreground = (Brush)Application.Current.Resources["Warn"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        DockPanel.SetDock(heading.Children[0], Dock.Left);
        heading.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "마감이 가까운 업무", FontWeight = FontWeights.Bold, FontSize = 15 },
                new TextBlock { Text = $"{tasks.Count}건", Style = (Style)Application.Current.Resources["Small"] },
            },
        });

        var list = new StackPanel { Margin = new Thickness(16, 0, 16, 4) };
        foreach (var task in tasks)
        {
            list.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["Card"],
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = task.Title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold },
                        new TextBlock
                        {
                            Text = task.DueLine,
                            Style = (Style)Application.Current.Resources["Small"],
                            Margin = new Thickness(0, 2, 0, 0),
                        },
                    },
                },
            });
        }

        var open = new Button { Content = "할 일 목록 열기", Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) =>
        {
            openTasks();
            Close();
        };
        var ok = new Button
        {
            Content = "확인",
            MinWidth = 76,
            IsDefault = true,
            Style = (Style)Application.Current.Resources["PrimaryButton"],
        };
        ok.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 8, 20, 16),
            Children = { open, ok },
        };

        var root = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(heading);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
        Content = root;

        // 작업 표시줄 위 오른쪽 아래에 띄운다.
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
    }
}
