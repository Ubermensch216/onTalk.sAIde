using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OnTalk.Sidecar.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 두 개가 동시에 뜨면 같은 저장소에 함께 쓰게 된다.
        _singleInstance = new Mutex(true, @"Local\OnTalk.sAIde", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("onTalk.sAIde가 이미 실행 중입니다.", "onTalk.sAIde");
            Shutdown();
            return;
        }
        // 이 PC(Intel UHD 770)에서 하드웨어 렌더링은 시작하자마자 650MB를 쓴다. 소프트웨어 렌더링은 약 50MB다.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        MainWindow = new PanelWindow();
        MainWindow.Show();
    }
}
