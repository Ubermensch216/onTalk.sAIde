using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OnTalk.Sidecar.App;

/// <summary>
/// 윈도 기본 메시지 상자 대신 쓰는 확인 창. 호출 방식과 결과는 <see cref="MessageBox"/>와 같다.
/// 단추 이름을 "예·아니오·확인·취소"로 한국어로 보이고, 기본 단추를 강조한다.
/// </summary>
public partial class DialogWindow : Window
{
    private readonly MessageBoxResult _escape;

    public MessageBoxResult Result { get; private set; }

    public DialogWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        InitializeComponent();
        Title = title;
        // 제목이 프로그램 이름일 뿐이면 머리글을 따로 보이지 않는다.
        TitleText.Text = title;
        TitleText.Visibility = title is "" or "onTalk.sAIde" ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = message;
        if (TitleText.Visibility == Visibility.Collapsed)
        {
            MessageText.FontSize = 14;
        }

        var (glyph, tint, soft) = image switch
        {
            MessageBoxImage.Warning => ("", "Warn", "WarnSoft"),
            MessageBoxImage.Error => ("", "Danger", "DangerSoft"),
            MessageBoxImage.Question => ("", "Accent", "AccentSoft"),
            _ => ("", "Accent", "AccentSoft"),
        };
        IconGlyph.Text = glyph;
        IconGlyph.Foreground = (Brush)FindResource(tint);
        IconBack.Background = (Brush)FindResource(soft);

        var choices = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { (MessageBoxResult.OK, "확인"), (MessageBoxResult.Cancel, "취소") },
            MessageBoxButton.YesNo => new[] { (MessageBoxResult.Yes, "예"), (MessageBoxResult.No, "아니오") },
            MessageBoxButton.YesNoCancel => new[]
            {
                (MessageBoxResult.Yes, "예"), (MessageBoxResult.No, "아니오"), (MessageBoxResult.Cancel, "취소"),
            },
            _ => new[] { (MessageBoxResult.OK, "확인") },
        };
        _escape = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel,
        };
        var primary = choices.Any(c => c.Item1 == defaultResult) ? defaultResult : choices[0].Item1;
        Result = _escape;

        foreach (var (result, label) in choices)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 76,
                Margin = new Thickness(Buttons.Children.Count == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = result == primary,
                IsCancel = result == _escape,
            };
            if (result == primary)
            {
                button.Style = (Style)FindResource("PrimaryButton");
            }
            var captured = result;
            button.Click += (_, _) =>
            {
                Result = captured;
                DialogResult = true;
            };
            Buttons.Children.Add(button);
        }
        Loaded += (_, _) => Buttons.Children.OfType<Button>().First(b => b.IsDefault).Focus();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}

/// <summary><see cref="MessageBox.Show(Window, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult)"/>를 대신하는 진입점.</summary>
public static class Dialogs
{
    public static MessageBoxResult Show(string message, string title) => Show(null, message, title);

    public static MessageBoxResult Show(Window? owner, string message, string title = "onTalk.sAIde",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new DialogWindow(message, title, buttons, image, defaultResult);
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }
        dialog.ShowDialog();
        return dialog.Result;
    }
}

/// <summary>코드에서 색을 고를 때 쓰는 테마 색 모음.</summary>
internal static class UiBrushes
{
    public static Brush Accent => Get("Accent");
    public static Brush AccentText => Get("AccentText");
    public static Brush Danger => Get("Danger");
    public static Brush Muted => Get("Muted");
    public static Brush TextStrong => Get("TextStrong");

    private static Brush Get(string key) => (Brush)Application.Current.Resources[key];
}
