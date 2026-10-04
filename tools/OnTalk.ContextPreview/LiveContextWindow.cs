using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OnTalk.Sidecar.Automation;

namespace OnTalk.ContextPreview;

/// <summary>제품 감시/파서를 그대로 실행하는 읽기 전용 통합 검사. 본문·신원·계정을 출력하거나 저장하지 않는다.</summary>
internal sealed class LiveContextWindow : Window
{
    private readonly NoteWatcher _watcher = new();
    private readonly OnTalkDock _dock = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22), FontSize = 17 };
    private readonly TextBlock _inputStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 4, 22, 12) };
    private Task<ChatInputState?>? _pendingInputRead;
    public LiveContextWindow(IntPtr target)
    {
        Title = "온톡 문맥 연동 검사"; Width = 620; Height = 650;
        var body = new StackPanel(); body.Children.Add(_status);
        var inspectInput = new Button { Content = "현재 대화 입력 구조 검사 (읽기)", Margin = new Thickness(22, 0, 22, 0) };
        body.Children.Add(inspectInput); body.Children.Add(_inputStatus); Content = body;
        var inspectShape = new Button { Content = "현재 목록 구조 검사 (읽기)", Margin = new Thickness(22, 0, 22, 0) };
        var shapeStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 4, 22, 12) };
        body.Children.Add(inspectShape); body.Children.Add(shapeStatus);
        var permitted = OnTalkAccount.DetectCandidate();
        var currentTarget = target;
        WindowContext? lastContext = null;
        inspectShape.Click += async (_, _) =>
        {
            var context = lastContext;
            if (context is null || context.Context.Surface is not (OnTalk.Sidecar.Core.OnTalkSurface.Main or OnTalk.Sidecar.Core.OnTalkSurface.Chat) || permitted != OnTalkAccount.DetectCandidate()) return;
            inspectShape.IsEnabled = false;
            try
            {
                var shape = await Task.Run(() =>
                {
                    if (Win32.FindRenderChild(context.Hwnd) is not { } render ||
                        MsaaReader.ReadRoot(render) is not { } root || WindowContextParser.Parse(root).Surface != context.Context.Surface) return "구조 미확인";
                    var tree = MsaaReader.ReadTree(render, readEditableValues: false, readContentText: true);
                    if (tree is null) return "구조 미확인";
                    return $"잘림: {tree.IsTruncated}\n" + string.Join("\n", tree.DescendantsAndSelf().Where(n => n.Role is "list" or "listitem")
                        .GroupBy(n => n.Role + ": " + string.Join(',', n.Children.Select(c => c.Role + ((c.State & 0x8000) != 0 ? "(hidden)" : ""))))
                        .Select(g => $"{g.Count()} × {g.Key}")) + "\n" + string.Join("\n",tree.DescendantsAndSelf().Where(n => n.Role == AccNode.EditableText)
                            .GroupBy(n => $"text state={n.State:X} nameLength={n.Name.Length}").Select(g => $"{g.Count()} × {g.Key}")) + "\n" +
                        string.Join("\n",tree.DescendantsAndSelf().Where(n=>n.Role=="list" && n.Children.Count==2)
                            .GroupBy(n=> $"list preview role={n.Children[1].Role} name={n.Children[1].Name.Length} value={n.Children[1].Value.Length}; row labels={n.Children[0].StaticTexts().Count(t=>t.Trim().Length>0)}")
                            .Select(g=>$"{g.Count()} × {g.Key}"));
                });
                if(lastContext?.SessionId == context.SessionId) shapeStatus.Text = shape;
            }
            finally { inspectShape.IsEnabled = true; }
        };
        inspectInput.Click += async (_, _) =>
        {
            if (_pendingInputRead is { IsCompleted: false }) { _inputStatus.Text = "이전 읽기 응답 대기 중 · 중복 조회/쓰기 없음"; return; }
            if (lastContext is not { Chat: { } chat } context || permitted is null) { _inputStatus.Text = "대화창을 먼저 표시하세요."; return; }
            inspectInput.IsEnabled = false;
            try
            {
                bool Allowed() => OnTalkAccount.DetectCandidate() == permitted && Dispatcher.Invoke(() => lastContext?.SessionId == context.SessionId && currentTarget == context.Hwnd);
                var backend = new NativeChatInputBackend(context.Hwnd, Allowed);
                var reader = new ChatInputWriter(backend, chat, Allowed);
                var progress = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                progress.Tick += (_, _) => _inputStatus.Text = "읽기 진행: " + backend.Failure + " · 값 저장/쓰기 없음";
                progress.Start();
                ChatInputState? input;
                try { _pendingInputRead = Task.Run(reader.Read); input = await _pendingInputRead.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { input = null; }
                finally { progress.Stop(); }
                _inputStatus.Text = input is null ? "입력 구조 확인 실패 · " + backend.Failure + " · 값 저장/쓰기 없음" :
                    $"제품 입력 읽기 성공 · {input.Body.Length}자 · 줄바꿈 {input.Body.Count(c => c == '\n')}개\nchatInputArea 식별 · {backend.Failure} · 값 저장/쓰기 없음";
            }
            finally { inspectInput.IsEnabled = true; }
        };
        var follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        follow.Tick += (_, _) => { _dock.Adopt(); _dock.Poll(); currentTarget = _dock.Target; _watcher.FollowTarget(currentTarget); };
        _watcher.FollowTarget(currentTarget); _watcher.PermitAccount(permitted);
        _watcher.Updated += snapshot => Dispatcher.BeginInvoke(() =>
        {
            var context = snapshot.ForTarget(currentTarget);
            if (lastContext?.SessionId != context?.SessionId) _inputStatus.Text = "";
            lastContext = context;
            _status.Text = snapshot.AccountCandidate != permitted ? "계정 변경 · 자료 읽기 중단" : context is null ? "대상 창 확인 중" :
                $"문맥: {context.Context.Surface} / {context.Context.Menu}\n" +
                $"사람: {context.People?.People.Count ?? 0} · 그룹/부서: {context.People?.Scopes.Count ?? 0}\n선택 신호: {context.People?.SelectedKeys.Count ?? 0}\n" +
                $"대화 목록: {context.Conversations.Count}\n대화 텍스트: {context.Chat?.Messages.Count ?? 0}\n" +
                $"날짜 확인: {context.Chat?.Messages.Count(m => m.Date is not null) ?? 0}\n" +
                $"표시 발신자 확인: {context.Chat?.Messages.Count(m => m.Sender != "발신자 미확인") ?? 0}\n" +
                "\n본문·계정·사람 정보 저장 없음\n실제 입력·전송 없음\n제품 NoteWatcher → 파서 경로 검사";
        });
        var limit = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        limit.Tick += (_, _) => Close();
        Loaded += (_, _) => { _watcher.Start(); if(target == IntPtr.Zero) follow.Start(); limit.Start(); }; Closed += (_, _) => { follow.Stop(); limit.Stop(); _watcher.Dispose(); };
    }
}
