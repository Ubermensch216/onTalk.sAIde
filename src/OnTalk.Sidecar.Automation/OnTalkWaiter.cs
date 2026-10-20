using System.Diagnostics;

namespace OnTalk.Sidecar.Automation;

/// <summary>
/// Windows 시작과 함께 뜰 때 온톡이 먼저 작동하기를 기다린다.
/// 온톡 프로세스가 보이면 창과 화면이 갖춰질 시간을 조금 더 준 뒤 시작해도 된다고 알린다.
/// </summary>
public sealed class OnTalkWaiter(
    Func<bool> isRunning,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);
    // 프로세스만 떠 있고 창이 아직 없는 동안 붙으면 화면을 못 찾는다.
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(3);

    public static OnTalkWaiter ForSystem() => new(IsProcessRunning, Task.Delay);

    /// <summary>온톡(프로세스 이름 "온톡")이 떠 있는지. 감시 코드와 같은 이름 규칙이다.</summary>
    public static bool IsProcessRunning()
    {
        var processes = Process.GetProcessesByName("온톡");
        foreach (var process in processes)
        {
            process.Dispose();
        }
        return processes.Length > 0;
    }

    /// <summary>
    /// 온톡이 뜰 때까지 주기적으로 확인한다. 뜬 것을 확인했으면 true, 제한 시간이 지났으면 false.
    /// 취소하면 <see cref="OperationCanceledException"/>이 난다.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeSpan interval, TimeSpan settle, CancellationToken cancel)
    {
        var waited = TimeSpan.Zero;
        while (!isRunning())
        {
            cancel.ThrowIfCancellationRequested();
            if (waited >= timeout)
            {
                return false;
            }
            var remaining = timeout - waited;
            var step = interval > TimeSpan.Zero && interval < remaining ? interval : remaining;
            await delay(step, cancel);
            waited += step;
        }
        if (settle > TimeSpan.Zero)
        {
            await delay(settle, cancel);
        }
        return true;
    }

    public Task<bool> WaitAsync(CancellationToken cancel) =>
        WaitAsync(DefaultTimeout, DefaultInterval, DefaultSettle, cancel);
}
