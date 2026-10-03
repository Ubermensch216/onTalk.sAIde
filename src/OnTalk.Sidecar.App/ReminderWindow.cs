using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OnTalk.Sidecar.App;

/// <summary>마감 알림. 앱이 꺼져 있던 동안 지나간 알림도 한 창에 모아 보여 준다.</summary>
public sealed class ReminderWindow : Window
{
    public ReminderWindow(IReadOnlyList<TaskRow> tasks, Action openTasks)
    {
        Title = "마감 알림 · onTalk.sAIde";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 520;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Malgun Gothic");
        FontSize = 13;
        Background = Brushes.White;

        var list = new StackPanel { Margin = new Thickness(16, 14, 16, 4) };
        list.Children.Add(new TextBlock
        {
            Text = $"마감이 가까운 업무 {tasks.Count}건",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });
        foreach (var task in tasks)
        {
            list.Children.Add(new TextBlock { Text = task.Title, TextWrapping = TextWrapping.Wrap });
            list.Children.Add(new TextBlock
            {
                Text = task.DueLine,
                Foreground = (Brush)Application.Current.Resources["Muted"],
                FontSize = 12,
                Margin = new Thickness(0, 1, 0, 10),
            });
        }

        var open = new Button { Content = "할 일 목록 열기", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) =>
        {
            openTasks();
            Close();
        };
        var ok = new Button { Content = "확인", Padding = new Thickness(16, 3, 16, 3), IsDefault = true };
        ok.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 6, 16, 14),
            Children = { open, ok },
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
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
