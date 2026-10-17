using System.Diagnostics;
using OnTalk.Sidecar.Core;
using MainMenu = OnTalk.Sidecar.Core.MainMenu;

namespace OnTalk.Sidecar.Automation;

public enum GroupOpenKind { Chat, Note }

public enum GroupOpenStatus
{
    /// <summary>새 대화창 또는 쪽지 작성 창이 열렸다.</summary>
    Opened,
    /// <summary>온톡 본창이 친구 화면이 아니다.</summary>
    NotFriends,
    /// <summary>온톡 친구 목록에서 체크한 친구를 읽지 못했다.</summary>
    NoSelection,
    /// <summary>체크한 친구 행이 가려져 있거나 사용자가 조합 키를 누르고 있어 우클릭하지 않았다.</summary>
    Covered,
    /// <summary>우클릭 메뉴에 단체 대화·단체 쪽지가 나오지 않았다.</summary>
    MenuNotFound,
    /// <summary>메뉴를 눌렀지만 새 창을 확인하지 못했다.</summary>
    NotOpened,
}

/// <summary>
/// 온톡 친구 목록에서 사용자가 체크한 친구로 단체 대화·단체 쪽지를 연다. 온톡에서 직접 하는 순서
/// (체크한 친구 우클릭 → 단체 대화/단체 쪽지)를 그대로 따른다. 체크 상태는 바꾸지 않는다.
/// </summary>
public static class FriendActions
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(4);

    public static GroupOpenStatus OpenGroup(GroupOpenKind kind)
    {
        if (FindFriendsWindow() is not { } main) return GroupOpenStatus.NotFriends;
        var (hwnd, render) = main;
        if (MsaaReader.ReadTree(render) is not { } tree) return GroupOpenStatus.NotFriends;
        var snapshot = MainPeopleParser.Parse(tree, MainMenu.Friends, DateTimeOffset.Now);
        // 화면 안에 보이는 체크한 친구 행 하나를 우클릭한다. 체크하지 않은 행을 누르면 온톡이 선택을 바꿀 수 있다.
        var row = snapshot.SelectedKeys.Select(key => tree.DescendantsAndSelf().FirstOrDefault(n => n.Path == key))
            .FirstOrDefault(n => n is { Bounds.IsEmpty: false } && Inside(hwnd, n.Bounds));
        if (row is null) return GroupOpenStatus.NoSelection;
        var before = Win32.VisibleOnTalkWindows().Select(w => w.Hwnd).ToHashSet();
        var popupsBefore = PopupWindows();
        if (InputInjector.RightClick(hwnd, row.Bounds) is not (FocusOutcome.Focused or FocusOutcome.NotForeground))
            return GroupOpenStatus.Covered;
        var labels = kind == GroupOpenKind.Chat ? new[] { "단체 대화", "단체대화" } : ["단체 쪽지", "단체쪽지"];
        // 온톡 5.4.9의 우클릭 메뉴는 본창 안이 아니라 별도의 작은 팝업 창(Chrome_WidgetWin)이다.
        // 항목은 그 창 자체의 접근성 트리에 있고 클릭 동작을 알리지 않아 좌표로 누른다.
        var invoked = Until(() => ClickPopupMenu(popupsBefore, labels), ok => ok);
        if (!invoked)
        {
            InputInjector.Tap(hwnd, InputInjector.KeyEscape);
            return GroupOpenStatus.MenuNotFound;
        }
        var opened = Until(() => Win32.VisibleOnTalkWindows().Any(w => !before.Contains(w.Hwnd) && IsTarget(w.Render, kind)), ok => ok);
        return opened ? GroupOpenStatus.Opened : GroupOpenStatus.NotOpened;
    }

    private static List<IntPtr> PopupWindows()
    {
        var pids = System.Diagnostics.Process.GetProcessesByName("온톡").Select(p => (uint)p.Id).ToHashSet();
        var found = new List<IntPtr>();
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains(pid) && Win32.IsWindowVisible(h) && Win32.ClassName(h).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>우클릭으로 새로 뜬 팝업 창에서 글자가 같은 메뉴 항목을 찾아 누른다.</summary>
    private static bool ClickPopupMenu(IReadOnlyCollection<IntPtr> before, IReadOnlyCollection<string> labels)
    {
        foreach (var popup in PopupWindows().Where(h => !before.Contains(h)))
        {
            Win32.ActivateAccessibility(popup);
            if (MsaaReader.ReadTree(popup) is { } tree && MenuItem(tree, labels) is { } item && InputInjector.ClickPopupItem(popup, item.Bounds)) return true;
        }
        return false;
    }

    /// <summary>메뉴 항목. 글자가 정확히 같은 화면에 보이는 요소 중 가장 안쪽(글자를 감싼 요소)을 고른다.</summary>
    internal static AccNode? MenuItem(AccNode tree, IReadOnlyCollection<string> labels) =>
        tree.DescendantsAndSelf().LastOrDefault(n => !n.Bounds.IsEmpty && n.Role != AccNode.StaticText &&
            (labels.Contains(n.Name.Trim()) || n.Children.Any(c => c.Role == AccNode.StaticText && labels.Contains(c.Name.Trim()))));

    internal static (IntPtr Hwnd, IntPtr Render)? FindFriendsWindow()
    {
        foreach (var (hwnd, render) in Win32.VisibleOnTalkWindows())
        {
            Win32.ActivateAccessibility(render);
            if (MsaaReader.ReadRoot(render) is { } root && WindowContextParser.Parse(root) is { Surface: OnTalkSurface.Main } &&
                root.Value.Contains("#/main/1", StringComparison.Ordinal)) return (hwnd, render);
        }
        return null;
    }

    private static bool IsTarget(IntPtr render, GroupOpenKind kind)
    {
        Win32.ActivateAccessibility(render);
        var url = MsaaReader.ReadRoot(render)?.Value ?? "";
        return url.Contains(kind == GroupOpenKind.Chat ? "/windows/chat/" : "/windows/note/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Inside(IntPtr hwnd, AccBounds bounds) =>
        Win32.GetWindowRect(hwnd, out var rect) && bounds.Center.X > rect.Left && bounds.Center.X < rect.Right &&
        bounds.Center.Y > rect.Top && bounds.Center.Y < rect.Bottom;

    private static T Until<T>(Func<T> read, Func<T, bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var value = read();
            if (done(value) || clock.Elapsed > Wait) return value;
            Thread.Sleep(150);
        }
    }
}
