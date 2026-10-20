using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OnTalk.Sidecar.App;

/// <summary>
/// 쪽지 작업 창(NoteHostWindow)의 탭 안에 들어가는 화면 조각의 공통 바탕.
/// 예전에 따로 창이던 화면이 창에 기대던 일(대화상자 소유 창, 닫기 확인, 앞으로 가져오기)을 소유 창을 통해 대신 처리한다.
/// </summary>
public abstract class WorkPane : UserControl
{
    protected WorkPane()
    {
        // 창 스타일(AppWindow)이 주던 글꼴·글자색을 직접 지정한다. 아직 창에 붙기 전에도 같은 모양으로 만들어져야 한다.
        FontFamily = new FontFamily("Malgun Gothic");
        FontSize = 13;
        UseLayoutRounding = true;
        SetResourceReference(ForegroundProperty, "TextStrong");
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    /// <summary>탭 이름이 아니라 작업의 이름. 발송 확인 창의 제목 등에 쓴다.</summary>
    public string Title { get; protected set; } = "";

    /// <summary>이 화면이 들어 있는 창. 대화상자·파일 선택창의 소유 창으로 쓴다(아직 창에 붙기 전에는 null).</summary>
    protected Window? Host => Window.GetWindow(this);

    /// <summary>발송·준비 같은 긴 작업이 진행 중이다. 이 동안에는 다른 탭으로 옮기지 못한다.</summary>
    internal virtual bool IsBusy => false;

    /// <summary>창을 닫기 직전에 불린다. false를 돌려주면 닫기를 취소한다.</summary>
    internal virtual bool QueryClose() => true;

    /// <summary>창이 닫혔다. 진행 중인 작업을 멈추고 자원을 놓는다.</summary>
    internal virtual void HostClosed() { }

    /// <summary>창이 다시 앞으로 오거나 이 탭이 열렸다. 다른 곳에서 바뀐 내용을 화면에 다시 맞춘다.</summary>
    internal virtual void HostActivated() { }

    /// <summary>이 탭을 열고 창을 앞으로 가져와 달라는 요청.</summary>
    internal event Action? ActivateRequested;

    /// <summary>온톡 창을 다루도록 이 창을 잠시 숨겨 달라는 요청.</summary>
    internal event Action? HideRequested;

    protected void ActivateHost() => ActivateRequested?.Invoke();

    protected void HideHost() => HideRequested?.Invoke();

    /// <summary>온톡 창을 오가는 조작이 끝나면 작업 중이던 이 창을 다시 맨 앞으로 가져온다.</summary>
    protected void BringHostBack()
    {
        if (Host is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
    }
}
