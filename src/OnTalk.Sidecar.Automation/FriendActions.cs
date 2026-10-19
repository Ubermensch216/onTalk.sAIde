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
    /// <summary>온톡이 "대화방 생성 중에 오류가 발생합니다" 같은 오류 안내를 띄웠다. 온톡 쪽 실패다.</summary>
    OnTalkError,
    /// <summary>선택한 친구와의 대화창이 이미 열려 있어 온톡이 새 창을 만들지 않았다.</summary>
    AlreadyOpen,
}

/// <summary>
/// 온톡 친구 목록에서 사용자가 체크한 친구로 단체 대화·단체 쪽지를 연다. 온톡에서 직접 하는 순서
/// (체크한 친구 우클릭 → 단체 대화/단체 쪽지)를 그대로 따른다. 체크 상태는 바꾸지 않는다.
/// </summary>
public static class FriendActions
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(20);

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
        var name = snapshot.People.FirstOrDefault(p => p.Key == row.Path)?.Name ?? "";
        var openBefore = Win32.VisibleOnTalkWindows();
        var before = openBefore.Select(w => w.Hwnd).ToHashSet();
        // 이미 같은 상대와의 대화창이 열려 있으면 온톡은 새 창을 만들지 않는다. 그때는 그 창을 앞으로 띄운다.
        var existing = kind == GroupOpenKind.Chat && name.Length > 0
            ? openBefore.FirstOrDefault(w => w.Hwnd != hwnd && IsTarget(w.Render, kind) && Win32.WindowText(w.Hwnd).Contains(name, StringComparison.Ordinal))
            : default;
        // 같은 상대와의 대화창이 이미 열려 있으면 온톡에 다시 대화를 열게 하지 않는다. 온톡이 대화방 생성 오류를 띄우거나
        // 새 창을 만들지 않으므로, 열려 있는 그 창을 바로 앞으로 가져온다.
        if (existing.Hwnd != IntPtr.Zero)
        {
            DismissErrorNotice(render);
            Win32.BringToFront(existing.Hwnd);
            return GroupOpenStatus.AlreadyOpen;
        }
        // 이전 시도가 남긴 온톡 오류 안내창은 본창을 막고 우클릭을 받지 못하게 하므로 먼저 닫는다.
        DismissErrorNotice(render);
        var popupsBefore = PopupWindows();
        // 여러 명을 체크하면 메뉴가 '단체 대화/단체 쪽지'지만, 한 명만 체크하면 '대화/쪽지'로 나온다. 앞쪽 이름을 우선한다.
        var labels = kind == GroupOpenKind.Chat ? new[] { "단체 대화", "단체대화", "대화" } : ["단체 쪽지", "단체쪽지", "쪽지"];
        // 온톡 5.4.9의 우클릭 메뉴는 본창 안이 아니라 별도의 작은 팝업 창(Chrome_WidgetWin)이다.
        // 항목은 그 창 자체의 접근성 트리에 있고 클릭 동작을 알리지 않아 좌표로 누른다.
        // 다른 창(열려 있던 대화창 등)이 앞에 있다가 온톡 본창이 활성화되는 첫 우클릭은 메뉴가 뜨지 않을 수 있어 한 번 더 시도한다.
        var invoked = false;
        // 열린 대화창이 앞에 있으면 온톡 본창이 비활성이라 우클릭 메뉴가 뜨지 않으므로 본창을 먼저 앞으로 가져온다.
        if (Win32.GetForegroundWindow() != hwnd)
        {
            Win32.BringToFront(hwnd);
            Thread.Sleep(250);
        }
        for (var attempt = 0; attempt < 3 && !invoked; attempt++)
        {
            if (InputInjector.RightClick(hwnd, row.Bounds) is not (FocusOutcome.Focused or FocusOutcome.NotForeground))
                return GroupOpenStatus.Covered;
            invoked = Until(() => ClickPopupMenu(popupsBefore, labels), ok => ok, attempt == 0 ? TimeSpan.FromSeconds(2) : Wait);
        }
        if (!invoked)
        {
            InputInjector.Tap(hwnd, InputInjector.KeyEscape);
            return GroupOpenStatus.MenuNotFound;
        }
        // 창이 뜨거나, 온톡이 오류 안내를 띄우면 기다림을 멈춘다. 오류 안내를 새 창 없음으로 뭉뚱그리지 않는다.
        // 새 창은 화면에 뜬 뒤에도 페이지를 읽어 오는 동안 주소가 비어 있을 수 있어 넉넉히 기다린다.
        // 같은 상대와의 창이 이미 열려 있으면 온톡은 새 창 대신 그 창을 앞으로 가져오므로, 앞에 온 대상 창도 연 것으로 본다.
        // 본창 전체를 읽는 오류 안내 확인은 무거워서 자주 하면 온톡이 느려져 창이 늦게 뜬다. 가끔만 확인한다.
        var errorCheck = Stopwatch.StartNew();
        var result = Until(() =>
        {
            var windows = Win32.VisibleOnTalkWindows();
            if (windows.Any(w => !before.Contains(w.Hwnd) && IsTarget(w.Render, kind))) return GroupOpenStatus.Opened;
            var front = Win32.GetForegroundWindow();
            if (front != hwnd && windows.Any(w => w.Hwnd == front && before.Contains(w.Hwnd) && IsTarget(w.Render, kind))) return GroupOpenStatus.Opened;
            if (errorCheck.Elapsed < TimeSpan.FromSeconds(2)) return GroupOpenStatus.NotOpened;
            errorCheck.Restart();
            return HasErrorNotice(render) ? GroupOpenStatus.OnTalkError : GroupOpenStatus.NotOpened;
        }, status => status != GroupOpenStatus.NotOpened, WindowWait);
        // 오류 안내창을 남겨 두면 다음 시도가 막히므로 확인을 눌러 닫는다.
        if (result == GroupOpenStatus.OnTalkError) DismissErrorNotice(render);
        return result;
    }

    /// <summary>온톡이 띄운 오류 안내창의 확인 단추를 눌러 닫는다. 안내창이 없으면 아무것도 하지 않는다.</summary>
    private static bool DismissErrorNotice(IntPtr render)
    {
        if (MsaaReader.ReadTree(render) is not { } tree || !HasErrorNotice(tree)) return false;
        var button = tree.DescendantsAndSelf().FirstOrDefault(n => n.Role == "pushbutton" && n.Name.Trim() == "확인" && !n.Bounds.IsEmpty);
        if (button is null || !MsaaReader.Invoke(render, button)) return false;
        return Until(() => !HasErrorNotice(render), gone => gone, TimeSpan.FromSeconds(2));
    }

    /// <summary>온톡 본창에 오류 안내("…오류가 발생합니다")가 떠 있는지.</summary>
    internal static bool HasErrorNotice(IntPtr render) =>
        MsaaReader.ReadTree(render) is { } tree && HasErrorNotice(tree);

    internal static bool HasErrorNotice(AccNode tree) => tree.DescendantsAndSelf().Any(n =>
        n.Role == AccNode.StaticText && (n.Name.Contains("오류가 발생", StringComparison.Ordinal) || n.Name.Contains("에러가 발생", StringComparison.Ordinal)));

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

    /// <summary>
    /// 메뉴 항목. 글자가 정확히 같은 화면에 보이는 요소 중 가장 안쪽(글자를 감싼 요소)을 고른다.
    /// <paramref name="labels"/>는 우선순위 순서이며, 앞쪽 이름의 항목이 있으면 뒤쪽 이름은 보지 않는다.
    /// </summary>
    internal static AccNode? MenuItem(AccNode tree, IReadOnlyCollection<string> labels) =>
        labels.Select(label => tree.DescendantsAndSelf().LastOrDefault(n => !n.Bounds.IsEmpty && n.Role != AccNode.StaticText &&
            (n.Name.Trim() == label || n.Children.Any(c => c.Role == AccNode.StaticText && c.Name.Trim() == label))))
            .FirstOrDefault(item => item is not null);

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

    private static T Until<T>(Func<T> read, Func<T, bool> done, TimeSpan? limit = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var value = read();
            if (done(value) || clock.Elapsed > (limit ?? Wait)) return value;
            Thread.Sleep(150);
        }
    }
}

