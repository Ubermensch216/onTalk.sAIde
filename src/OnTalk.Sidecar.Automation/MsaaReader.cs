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
        [0x0F] = AccNode.Document, [0x1E] = "link", [0x21] = "list", [0x22] = "listitem", [0x18] = "table",
        [0x28] = "graphic", [0x29] = AccNode.StaticText, [0x2A] = AccNode.EditableText, [0x2B] = "pushbutton",
        [0x2C] = AccNode.CheckBox, [0x2E] = "combobox", [0x3B] = AccNode.Whitespace,
    };

    /// <summary>루트 요소만 읽는다. 창 종류 판별용.</summary>
    public static AccNode? ReadRoot(IntPtr renderHwnd) =>
        Root(renderHwnd) is { } acc ? Read(acc, []) : null;

    public static AccNode? ReadTree(IntPtr renderHwnd)
    {
        if (Root(renderHwnd) is not { } root)
        {
            return null;
        }
        var budget = MaxNodes;
        return ReadSubtree(root, ref budget);
    }

    private static IAccessible? Root(IntPtr renderHwnd)
    {
        var iid = typeof(IAccessible).GUID;
        return Win32.AccessibleObjectFromWindow(renderHwnd, Win32.OBJID_CLIENT, ref iid, out var obj) == 0
            ? obj as IAccessible
            : null;
    }

    private static AccNode ReadSubtree(IAccessible acc, ref int budget)
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
                        children.Add(ReadSubtree(child, ref budget));
                    }
                }
            }
        }
        return Read(acc, children);
    }

    private static AccNode Read(IAccessible acc, IReadOnlyList<AccNode> children)
    {
        var role = Safe(() => acc.get_accRole(Self)) switch
        {
            int id => RoleNames.TryGetValue(id, out var name) ? name : $"role{id:X}",
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
        return new AccNode(
            role,
            Safe(() => acc.get_accName(Self)) ?? "",
            Safe(() => acc.get_accValue(Self)) ?? "",
            Safe(() => acc.get_accState(Self)) is int state ? state : 0,
            children) { Bounds = bounds };
    }

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
