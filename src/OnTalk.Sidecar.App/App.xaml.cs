using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OnTalk.Sidecar.App;

public partial class App : Application
{
    private const string QuitEventName = @"Local\OnTalk.sAIde.Quit";

    private Mutex? _singleInstance;
    private EventWaitHandle? _quitSignal;
    private RegisteredWaitHandle? _quitWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // `OnTalk.sAIde.exe --quit`: 떠 있는 onTalk.sAIde에게만 끝내라고 알린다. 온톡은 건드리지 않는다.
        if (e.Args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var running))
            {
                running.Set();
                running.Dispose();
            }
            Shutdown();
            return;
        }
        // 두 개가 동시에 뜨면 같은 저장소에 함께 쓰게 된다.
        _singleInstance = new Mutex(true, @"Local\OnTalk.sAIde", out var isFirst);
        if (!isFirst)
        {
            Dialogs.Show("onTalk.sAIde가 이미 실행 중입니다.", "onTalk.sAIde");
            Shutdown();
            return;
        }
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal,
            (_, _) => Dispatcher.BeginInvoke(() => MainWindow?.Close()), null, Timeout.Infinite, executeOnlyOnce: true);
        // 이 PC(Intel UHD 770)에서 하드웨어 렌더링은 시작하자마자 650MB를 쓴다. 소프트웨어 렌더링은 약 50MB다.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        MainWindow = new PanelWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _quitWait?.Unregister(null);
        _quitSignal?.Dispose();
        base.OnExit(e);
    }
}
