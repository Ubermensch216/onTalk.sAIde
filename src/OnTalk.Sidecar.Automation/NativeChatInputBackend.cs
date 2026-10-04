using System.Diagnostics;
using System.Windows.Automation;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

/// <summary>본문 트리는 MSAA로 읽고, 입력은 정확한 chatInputArea의 ValuePattern만 사용한다.</summary>
internal sealed class NativeChatInputBackend(IntPtr hwnd, Func<bool> canUse) : IChatInputBackend
{
    internal string Failure { get; private set; } = "검사 전";
    public ChatInputState? Read()
    {
        try { return Capture()?.State; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or
            System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or ArgumentException)
        { Failure += " · " + ex.GetType().Name; return null; }
    }

    public bool Replace(ChatInputState expected, string body)
    {
        try
        {
            // ValuePattern을 구한 직후에도 현재 값/대상을 비교한다. 다른 필드나 키 입력으로 대체하지 않는다.
            var live = Capture();
            if (live is null || !ChatInputWriter.SameTarget(expected, live.Value.State) ||
                ChatInputWriter.Normalize(expected.Body) != ChatInputWriter.Normalize(live.Value.State.Body) || !canUse()) return false;
            live.Value.Value.SetValue(body);
            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or
            System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or ArgumentException)
        { return false; }
    }

    private (ChatInputState State, ValuePattern Value)? Capture()
    {
        Failure = "계정/창 상태 확인";
        if (!canUse() || !Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd) ||
            !Win32.IsWindowEnabled(hwnd) || Win32.FindRenderChild(hwnd) is not { } render) return null;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        Failure = "프로세스/버전 확인";
        using var process = Process.GetProcessById((int)pid);
        if (process.ProcessName != "온톡" || process.MainModule?.FileVersionInfo.ProductVersion is not { } version ||
            !OnTalkAccount.IsVerified(version)) return null;
        // 로그인/검색 등의 다른 화면에서는 입력값 조회까지 진행하지 않는다.
        Failure = "대화 페이지 확인";
        var root = MsaaReader.ReadRoot(render);
        if (root is null || WindowContextParser.Parse(root).Surface != OnTalkSurface.Chat) return null;
        Failure = "대화 트리 읽는 중";
        var tree = MsaaReader.ReadTree(render, readEditableValues: false, readContentText: true);
        Failure = "대화 트리 확인";
        if (tree is null || tree.IsTruncated || WindowContextParser.Parse(tree).Surface != OnTalkSurface.Chat || tree.Value != root.Value ||
            tree.DescendantsAndSelf().Any(n => n.Role == "dialog" && (n.State & 0x8000) == 0)) return null;
        var chat = ChatWindowParser.Parse(tree, DateTimeOffset.Now);
        if (chat is null) return null;
        // Chromium 78의 UIA 프록시로 전체 트리를 검색하면 수 분 걸릴 수 있다.
        // MSAA에서 읽힌 단일 입력 영역으로 UIA 요소를 찾되, 좌표로 입력하지 않는다.
        // 겹친 다른 창이나 다른 필드가 나오면 ID/종류/프로세스 확인에서 거절한다.
        var inputs = tree.DescendantsAndSelf().Where(IsInputCandidate).ToList();
        if (inputs.Count != 1) { Failure = $"MSAA 입력 후보 {inputs.Count}개"; return null; }
        var bounds = inputs[0].Bounds;
        Failure = "UIA 입력 요소 확인";
        var field = AutomationElement.FromPoint(new System.Windows.Point(bounds.X + bounds.Width / 2d, bounds.Y + bounds.Height / 2d));
        // Chromium이 입력 글의 하위 노드를 반환하면 그 입력 요소까지만 올라간다.
        for (var depth = 0; depth < 16 && field.Current.ControlType != ControlType.Edit && field.Current.NativeWindowHandle == 0; depth++)
        {
            var parent = TreeWalker.RawViewWalker.GetParent(field);
            if (parent is null) break;
            field = parent;
        }
        // 관찰한 온톡 UIA 문서에는 입력이 직접 자식으로 노출된다.
        // 점 조회가 문서를 반환하는 버전에서도 한 단계만 조회하고 하위 트리 검색은 하지 않는다.
        if (field.Current.ControlType == ControlType.Document && BelongsTo(field, hwnd, render))
        {
            Failure = "UIA 문서의 직접 입력 확인";
            var direct = new List<AutomationElement>();
            var watch = Stopwatch.StartNew();
            var walker = TreeWalker.ControlViewWalker;
            var child = walker.GetFirstChild(field); var count = 0;
            for (; child is not null && count < 512 && watch.Elapsed < TimeSpan.FromSeconds(8); child = walker.GetNextSibling(child), count++)
                if (child.Current.ControlType == ControlType.Edit && child.Current.AutomationId == "chatInputArea") direct.Add(child);
            if (child is not null || direct.Count != 1) { Failure = $"직접 입력 요소 {direct.Count}개 · 조회 완료={child is null}"; return null; }
            field = direct[0];
        }
        var properties = field.Current;
        if (properties.AutomationId != "chatInputArea" || properties.ControlType != ControlType.Edit || properties.ProcessId != pid || !BelongsTo(field, hwnd, render))
        { Failure = $"입력 식별 실패 · type={properties.ControlType.ProgrammaticName}, id={properties.AutomationId == "chatInputArea"}, process={properties.ProcessId == pid}, window={BelongsTo(field, hwnd, render)}"; return null; }
        Failure = $"입력 지원 확인 · enabled={properties.IsEnabled}, offscreen={properties.IsOffscreen}, password={properties.IsPassword}";
        if (!properties.IsEnabled || properties.IsOffscreen || properties.IsPassword ||
            !field.TryGetCurrentPattern(ValuePattern.Pattern, out var raw) || raw is not ValuePattern value || value.Current.IsReadOnly) return null;
        var body = value.Current.Value;
        Failure = "값 재읽기 후 대상 확인";
        if (!Win32.IsWindowEnabled(hwnd) || Win32.FindRenderChild(hwnd) != render || MsaaReader.ReadRoot(render)?.Value != tree.Value) return null;
        Win32.GetWindowThreadProcessId(hwnd, out var afterPid);
        if (afterPid != pid || !canUse()) return null;
        var state = new ChatInputState(pid, render, tree.Value, string.Join('/', field.GetRuntimeId()), chat, body);
        Failure = "입력 읽기 완료";
        return (state, value);
    }

    internal static bool IsInputCandidate(AccNode node) => node.Role == AccNode.EditableText &&
        (node.State & 0x100000) != 0 && (node.State & (0x8000 | 0x1 | 0x200000)) == 0 &&
        node.Bounds.Width > 0 && node.Bounds.Height > 0;

    private static bool BelongsTo(AutomationElement field, IntPtr window, IntPtr render)
    {
        var node = field;
        for (var depth = 0; node is not null && depth < 32; depth++, node = TreeWalker.RawViewWalker.GetParent(node))
        {
            var handle = new IntPtr(node.Current.NativeWindowHandle);
            if (handle == window || handle == render) return true;
            if (handle != IntPtr.Zero) return false;
        }
        return false;
    }
}
