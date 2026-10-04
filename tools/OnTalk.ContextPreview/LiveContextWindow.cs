using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OnTalk.Sidecar.Automation;

namespace OnTalk.ContextPreview;

/// <summary>제품 감시/파서를 그대로 실행하는 읽기 전용 통합 검사. 본문·신원·계정을 출력하거나 저장하지 않는다.</summary>
internal sealed class LiveContextWindow : Window
{
    private readonly NoteWatcher _watcher = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22), FontSize = 17 };
    public LiveContextWindow(IntPtr target)
    {
        Title = "온톡 문맥 연동 검사"; Width = 520; Height = 420; Content = _status;
        var permitted = OnTalkAccount.DetectCandidate();
        _watcher.FollowTarget(target); _watcher.PermitAccount(permitted);
        _watcher.Updated += snapshot => Dispatcher.BeginInvoke(() =>
        {
            var context = snapshot.ForTarget(target);
            _status.Text = snapshot.AccountCandidate != permitted ? "계정 변경 · 자료 읽기 중단" : context is null ? "대상 창 확인 중" :
                $"문맥: {context.Context.Surface} / {context.Context.Menu}\n" +
                $"사람: {context.People?.People.Count ?? 0} · 그룹/부서: {context.People?.Scopes.Count ?? 0}\n" +
                $"대화 목록: {context.Conversations.Count}\n대화 텍스트: {context.Chat?.Messages.Count ?? 0}\n" +
                $"날짜 확인: {context.Chat?.Messages.Count(m => m.Date is not null) ?? 0}\n" +
                $"표시 발신자 확인: {context.Chat?.Messages.Count(m => m.Sender != "발신자 미확인") ?? 0}\n" +
                "\n본문·계정·사람 정보 저장 없음\n실제 입력·전송 없음\n제품 NoteWatcher → 파서 경로 검사";
        });
        var limit = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        limit.Tick += (_, _) => Close();
        Loaded += (_, _) => { _watcher.Start(); limit.Start(); }; Closed += (_, _) => { limit.Stop(); _watcher.Dispose(); };
    }
}
