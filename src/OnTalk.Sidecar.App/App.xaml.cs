using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OnTalk.Sidecar.Automation;

namespace OnTalk.Sidecar.App;

public partial class App : Application
{
    private const string QuitEventName = @"Local\OnTalk.sAIde.Quit";

    private Mutex? _singleInstance;
    private EventWaitHandle? _quitSignal;
    private RegisteredWaitHandle? _quitWait;
    // 자동 실행으로 떴을 때 온톡을 기다리는 동안 --quit이나 종료 요청이 오면 기다림을 멈춘다.
    private readonly CancellationTokenSource _startupWait = new();

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
            (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _startupWait.Cancel();
                if (MainWindow is null)
                {
                    Shutdown();
                }
                else
                {
                    MainWindow.Close();
                }
            }), null, Timeout.Infinite, executeOnlyOnce: true);
        // 이 PC(Intel UHD 770)에서 하드웨어 렌더링은 시작하자마자 650MB를 쓴다. 소프트웨어 렌더링은 약 50MB다.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var autoStart = AutoStart.ForCurrentProcess();
        try
        {
            // 폴더를 옮기거나 새 버전으로 바꿔도 등록된 경로가 어긋나지 않게 한다.
            autoStart.RefreshPath();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // 레지스트리를 쓸 수 없어도 앱은 그대로 동작한다.
        }
        if (AutoStart.IsAutoStartLaunch(e.Args))
        {
            // Windows 시작 때는 온톡이 뜰 때까지 기다린 뒤에 시작한다. 직접 실행했을 때의 동작은 그대로다.
            _ = StartAfterOnTalkAsync();
            return;
        }
        ShowPanel();
    }

    private async Task StartAfterOnTalkAsync()
    {
        try
        {
            // 제한 시간 안에 온톡이 뜨지 않아도 패널은 띄운다. 패널이 온톡을 지켜보다가 뜨면 자동으로 붙는다.
            await OnTalkWaiter.ForSystem().WaitAsync(_startupWait.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        ShowPanel();
    }

    private void ShowPanel()
    {
        MainWindow = new PanelWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupWait.Cancel();
        _quitWait?.Unregister(null);
        _quitSignal?.Dispose();
        base.OnExit(e);
    }
}
