using Accessibility;

namespace OnTalk.Sidecar.Automation;

/// <summary>
/// 온톡 렌더 창의 접근성 트리를 MSAA로 읽는다. Chromium 78은 UIA를 직접 제공하지 않아
/// UIA 프록시를 거치면 수십 배 느리다(docs/feasibility-results.md).
/// </summary>
internal static class MsaaReader
{
    private const int Self = 0;
    private const int MaxNodes = 3000;

    private static readonly Dictionary<int, string> RoleNames = new()
    {
        [0x0F] = AccNode.Document, [0x12] = "dialog", [0x1E] = "link", [0x21] = "list", [0x22] = AccNode.ListItem, [0x18] = "table",
        [0x28] = "graphic", [0x29] = AccNode.StaticText, [0x2A] = AccNode.EditableText, [0x2B] = "pushbutton",
        [0x2C] = AccNode.CheckBox, [0x2E] = AccNode.ComboBox, [0x3B] = AccNode.Whitespace, [0x1D] = AccNode.Cell,
        [0x25] = "pagetab",
        [0x23] = "outline", [0x24] = "outlineitem", [0x0D] = "tooltip",
    };

    /// <summary>루트 요소만 읽는다. 창 종류 판별용.</summary>
    public static AccNode? ReadRoot(IntPtr renderHwnd) =>
        Root(renderHwnd) is { } acc ? Read(acc, [], "") : null;

    public static AccNode? ReadTree(IntPtr renderHwnd, bool readEditableValues = true, bool readContentText = false)
    {
        if (Root(renderHwnd) is not { } root)
        {
            return null;
        }
        // 검증한 대화 화면의 표시 글은 포커스 불가능한 text 컨테이너 안에 있다.
        // 하위 statictext만 해석할 수 있게 구분한다. text의 값은 계속 제외한다.
        var context = readContentText ? WindowContextParser.Parse(Read(root, [], "", false)) : null;
        readContentText = context?.Surface == OnTalk.Sidecar.Core.OnTalkSurface.Chat ||
            context is { Surface: OnTalk.Sidecar.Core.OnTalkSurface.Main, Menu: OnTalk.Sidecar.Core.MainMenu.Conversations };
        var budget = MaxNodes;
        var tree = ReadSubtree(root, "", ref budget, readEditableValues, readContentText);
        return tree with { IsTruncated = budget <= 0 };
    }

    /// <summary>
    /// 앞서 읽은 요소를 다시 찾아 기본 동작(웹 요소의 클릭)을 실행한다. 좌표가 아니라 요소 자체를 지정하므로
    /// 다른 창이나 다른 요소가 눌리지 않는다. 그 사이 화면이 바뀌어 같은 자리의 요소가 달라졌으면 실행하지 않는다.
    /// </summary>
    public static bool Invoke(IntPtr renderHwnd, AccNode expected) =>
        Act(renderHwnd, expected, acc => acc.accDoDefaultAction(Self));

    /// <summary>
    /// 입력창의 값을 접근성 기능으로 바꾼다. 창을 앞으로 가져오지 않고 키보드·마우스·클립보드도 쓰지 않는다.
    /// 화면에 글자가 보여도 온톡 내부 상태가 바뀌었다는 보장은 없으므로, 부르는 쪽이 바이트 표시 등으로 다시 확인해야 한다.
    /// </summary>
    public static bool SetValue(IntPtr renderHwnd, AccNode expected, string value) =>
        Act(renderHwnd, expected, acc => acc.set_accValue(Self, value));

    private static bool Act(IntPtr renderHwnd, AccNode expected, Action<IAccessible> action)
    {
        if (Resolve(renderHwnd, expected) is not { } acc) return false;
        try { action(acc); return true; }
        catch { return false; }
    }

    private static IAccessible? Resolve(IntPtr renderHwnd, AccNode expected)
    {
        if (Root(renderHwnd) is not { } acc)
        {
            return null;
        }
        foreach (var index in expected.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse))
        {
            var count = Safe(() => acc.accChildCount);
            if (index >= count)
            {
                return null;
            }
            var raw = new object[count];
            if (Win32.AccessibleChildren(acc, 0, count, raw, out var got) != 0 || index >= got ||
                raw[index] is not IAccessible child)
            {
                return null;
            }
            acc = child;
        }
        var now = Read(acc, [], expected.Path, readEditableValues: false);
        if (now.Role != expected.Role || now.Name != expected.Name)
        {
            return null;
        }
        return acc;
    }

    private static IAccessible? Root(IntPtr renderHwnd)
    {
        var iid = typeof(IAccessible).GUID;
        return Win32.AccessibleObjectFromWindow(renderHwnd, Win32.OBJID_CLIENT, ref iid, out var obj) == 0
            ? obj as IAccessible
            : null;
    }

    private static AccNode ReadSubtree(IAccessible acc, string path, ref int budget, bool readEditableValues, bool readContentText)
    {
        budget--;
        var children = new List<AccNode>();
        var count = Safe(() => acc.accChildCount);
        if (count > 0 && budget > 0)
        {
            var raw = new object[count];
            if (Win32.AccessibleChildren(acc, 0, count, raw, out var got) == 0)
            {
                for (var i = 0; i < got && budget > 0; i++)
                {
                    // 정수 자식 ID(단순 요소)는 Chromium 웹 콘텐츠에서 쓰이지 않는다.
                    if (raw[i] is IAccessible child)
                    {
                        children.Add(ReadSubtree(child, path.Length == 0 ? $"{i}" : $"{path}/{i}", ref budget, readEditableValues, readContentText));
                    }
                }
            }
        }
        return Read(acc, children, path, readEditableValues, readContentText);
    }

    private static AccNode Read(IAccessible acc, IReadOnlyList<AccNode> children, string path, bool readEditableValues = true, bool readContentText = false)
    {
        var rawRole = Safe(() => acc.get_accRole(Self));
        var role = rawRole switch
        {
            int id => RoleNames.TryGetValue(id, out var roleName) ? roleName : $"role{id:X}",
            string tag => tag,
            _ => "?",
        };
        var bounds = default(AccBounds);
        try
        {
            acc.accLocation(out var x, out var y, out var width, out var height, Self);
            bounds = new AccBounds(x, y, width, height);
        }
        catch
        {
            // 화면에 없는 요소는 위치를 주지 않는다.
        }
        var state = Safe(() => acc.get_accState(Self)) is int nativeState ? nativeState : 0;
        var contentText = readContentText && IsDisplayText(role, state);
        var name = Safe(() => acc.get_accName(Self)) ?? "";
        var value = !readEditableValues && role == AccNode.EditableText ? "" : Safe(() => acc.get_accValue(Self)) ?? "";
        return new AccNode(
            contentText ? AccNode.ContentText : role,
            name,
            value,
            state,
            children) { Bounds = bounds, Path = path, HasAction = (Safe(() => acc.get_accDefaultAction(Self)) ?? "").Length > 0 };
    }

    internal static bool IsDisplayText(string role, int state) =>
        role == AccNode.EditableText && (state & (0x100000 | 0x8000 | 0x1 | 0x200000)) == 0;

    private static T? Safe<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch
        {
            // 읽는 도중 요소가 사라지면 COM 오류가 난다. 빈 값으로 둔다.
            return default;
        }
    }
}
