using System.Runtime.InteropServices;
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
        // 조직도·친구의 확인란은 체크 여부를 MSAA 상태로 알리지 않고 CSS 클래스(ant-*-checkbox-checked)로만 바뀔 수 있다.
        var readClasses = context is { Surface: OnTalk.Sidecar.Core.OnTalkSurface.Main, Menu: OnTalk.Sidecar.Core.MainMenu.Organization or OnTalk.Sidecar.Core.MainMenu.Friends };
        var budget = MaxNodes;
        var tree = ReadSubtree(root, "", ref budget, readEditableValues, readContentText, readClasses);
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

    private static AccNode ReadSubtree(IAccessible acc, string path, ref int budget, bool readEditableValues, bool readContentText, bool readClasses = false)
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
                        children.Add(ReadSubtree(child, path.Length == 0 ? $"{i}" : $"{path}/{i}", ref budget, readEditableValues, readContentText, readClasses));
                    }
                }
            }
        }
        var node = Read(acc, children, path, readEditableValues, readContentText);
        return readClasses && node.Role == "SPAN" ? node with { Class = ClassOf(acc) } : node;
    }

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr obj); }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAttributes(IntPtr self, out IntPtr attributes);
    private static readonly Guid IidAccessible2 = new("E89F726E-C4F4-4c19-BB19-B647D7FA8478");
    // IAccessible2::get_attributes. IUnknown 3 + IDispatch 4 + IAccessible 21 + IAccessible2 앞 17개 메서드 다음 자리.
    private const int GetAttributesSlot = 45;

    /// <summary>Chromium IAccessible2 개체 특성의 class 값. 얻지 못하면 빈 문자열.</summary>
    private static string ClassOf(IAccessible acc)
    {
        if (acc is not IServiceProvider provider) return "";
        var service = typeof(IAccessible).GUID; var iid = IidAccessible2;
        IntPtr ia2;
        try { if (provider.QueryService(ref service, ref iid, out ia2) != 0 || ia2 == IntPtr.Zero) return ""; }
        catch { return ""; }
        try
        {
            var slot = Marshal.ReadIntPtr(Marshal.ReadIntPtr(ia2), GetAttributesSlot * IntPtr.Size);
            if (Marshal.GetDelegateForFunctionPointer<GetAttributes>(slot)(ia2, out var bstr) != 0 || bstr == IntPtr.Zero) return "";
            var text = Marshal.PtrToStringBSTR(bstr); Marshal.FreeBSTR(bstr);
            return ParseClass(text);
        }
        catch { return ""; }
        finally { Marshal.Release(ia2); }
    }

    /// <summary>"display:block;tag:span;class:a b;" 형식에서 class 값만 꺼낸다. 역슬래시 이스케이프를 푼다.</summary>
    internal static string ParseClass(string attributes)
    {
        var parts = new List<string>(); var current = new System.Text.StringBuilder();
        for (var i = 0; i < attributes.Length; i++)
        {
            if (attributes[i] == '\\' && i + 1 < attributes.Length) { current.Append(attributes[++i]); continue; }
            if (attributes[i] == ';') { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(attributes[i]);
        }
        parts.Add(current.ToString());
        return parts.FirstOrDefault(p => p.StartsWith("class:", StringComparison.Ordinal))?["class:".Length..] ?? "";
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
