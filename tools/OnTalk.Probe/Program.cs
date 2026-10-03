using System.Diagnostics;
using System.Text;
using Accessibility;
using OnTalk.Probe;

// 온톡 연동 진단 도구.
//   windows                         온톡 프로세스의 최상위 창 목록 (제목은 글자 수만)
//   activate                        접근성 트리 활성화 신호 전송
//   stats [--hwnd 0x..]             창별 접근성 요소 집계 (이름·본문 미출력)
//   tree --hwnd 0x.. [--content]    요소 트리 출력. --content가 있을 때만 이름·값 출력
// 입력·클릭 등 온톡을 조작하는 동작은 하지 않는다.
//
// Chromium 78은 UIA를 직접 제공하지 않아 UIA 프록시를 거치면 매우 느리다.
// 그래서 MSAA(IAccessible)를 직접 읽는다.

Console.OutputEncoding = Encoding.UTF8;

const string ProcessName = "온톡";
const string RenderClass = "Chrome_RenderWidgetHostHWND";

var command = args.Length > 0 ? args[0] : "windows";
var hwndArg = Option("--hwnd");
var withContent = args.Contains("--content");
var maxNodes = int.TryParse(Option("--max"), out var m) ? m : 6000;

var windows = FindWindows();
if (windows.Count == 0)
{
    Console.WriteLine("온톡 창을 찾지 못했습니다.");
    return 1;
}

switch (command)
{
    case "windows":
        PrintWindows();
        return 0;
    case "activate":
        Activate();
        return 0;
    case "stats":
        foreach (var w in Selected())
        {
            Stats(w);
        }
        return 0;
    case "tree":
        if (hwndArg is null)
        {
            Console.WriteLine("tree 명령에는 --hwnd가 필요합니다.");
            return 2;
        }
        foreach (var w in Selected())
        {
            Tree(w);
        }
        return 0;
    default:
        Console.WriteLine($"알 수 없는 명령: {command}");
        return 2;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

IEnumerable<Win> Selected()
{
    if (hwndArg is null)
    {
        return windows.Where(w => w.Renders.Count > 0);
    }
    var target = new IntPtr(Convert.ToInt64(hwndArg, hwndArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10));
    return windows.Where(w => w.Hwnd == target);
}

List<Win> FindWindows()
{
    var pids = Process.GetProcessesByName(ProcessName).Select(p => (uint)p.Id).ToHashSet();
    var found = new List<Win>();
    Native.EnumWindows((hwnd, _) =>
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (!pids.Contains(pid))
        {
            return true;
        }
        var cls = Native.ClassName(hwnd);
        if (!cls.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal))
        {
            return true;
        }
        var renders = new List<IntPtr>();
        Native.EnumChildWindows(hwnd, (child, _) =>
        {
            if (Native.ClassName(child) == RenderClass)
            {
                renders.Add(child);
            }
            return true;
        }, IntPtr.Zero);
        Native.GetWindowRect(hwnd, out var rect);
        found.Add(new Win(hwnd, pid, cls, Native.IsWindowVisible(hwnd), Native.IsIconic(hwnd), rect,
            Native.WindowText(hwnd).Length, renders));
        return true;
    }, IntPtr.Zero);
    return found;
}

void PrintWindows()
{
    Console.WriteLine("hwnd        pid     visible min  size        titleLen renders");
    foreach (var w in windows)
    {
        Console.WriteLine(
            $"0x{w.Hwnd.ToInt64():X8}  {w.Pid,-6}  {(w.Visible ? "Y" : "n"),-7} {(w.Minimized ? "Y" : "n"),-3}  " +
            $"{w.Rect.Width}x{w.Rect.Height,-6} {w.TitleLength,-8} {w.Renders.Count}");
    }
}

void Activate()
{
    var sent = 0;
    foreach (var render in windows.SelectMany(w => w.Renders))
    {
        var ok = Native.SendMessageTimeoutW(render, Native.WM_GETOBJECT, IntPtr.Zero,
            new IntPtr(Native.ScreenReaderHoneyPotId), Native.SMTO_ABORTIFHUNG, 2000, out _);
        if (ok != IntPtr.Zero)
        {
            sent++;
        }
    }
    Console.WriteLine($"활성화 신호 전송: 렌더 창 {sent}개");
}

void Stats(Win w)
{
    Console.WriteLine($"== 0x{w.Hwnd.ToInt64():X8}  {w.Rect.Width}x{w.Rect.Height}  visible={w.Visible}");
    var sw = Stopwatch.StartNew();
    var byRole = new SortedDictionary<string, (int Count, int Named, int NameChars, int Valued)>();
    var total = 0;
    var maxDepth = 0;
    string? documentUrl = null;

    Walk(w, node =>
    {
        total++;
        maxDepth = Math.Max(maxDepth, node.Depth);
        byRole.TryGetValue(node.Role, out var s);
        byRole[node.Role] = (s.Count + 1, s.Named + (node.Name.Length > 0 ? 1 : 0), s.NameChars + node.Name.Length,
            s.Valued + (node.Value.Length > 0 ? 1 : 0));
        if (node.Role == "document")
        {
            documentUrl ??= AppUrl(node.Value);
        }
    });

    Console.WriteLine($"   요소 {total}개, 최대 깊이 {maxDepth}, {sw.ElapsedMilliseconds}ms" +
                      (total >= maxNodes ? " (상한 도달)" : ""));
    Console.WriteLine($"   문서: {documentUrl ?? "(확인 불가)"}");
    foreach (var (role, s) in byRole)
    {
        Console.WriteLine($"   {role,-16} {s.Count,5}  이름있음 {s.Named,5}  글자수 {s.NameChars,6}  값있음 {s.Valued}");
    }
}

void Tree(Win w)
{
    Walk(w, node =>
    {
        var line = new StringBuilder();
        line.Append(' ', node.Depth * 2).Append(node.Role);
        if (withContent)
        {
            if (node.Name.Length > 0)
            {
                line.Append($" \"{Clip(node.Name)}\"");
            }
            if (node.Value.Length > 0)
            {
                line.Append($" ={Clip(node.Value)}");
            }
        }
        else
        {
            if (node.Name.Length > 0)
            {
                line.Append($" [{node.Name.Length}자]");
            }
            if (node.Value.Length > 0)
            {
                line.Append($" [값 {node.Value.Length}자]");
            }
        }
        if (node.Action.Length > 0)
        {
            line.Append($" <{node.Action}>");
        }
        line.Append($" s={node.State:X} @{node.X},{node.Y} {node.W}x{node.H}");
        Console.WriteLine(line);
    });
}

void Walk(Win w, Action<Node> visit)
{
    var count = 0;
    foreach (var render in w.Renders)
    {
        var iid = typeof(IAccessible).GUID;
        if (Native.AccessibleObjectFromWindow(render, Native.OBJID_CLIENT, ref iid, out var obj) != 0 ||
            obj is not IAccessible root)
        {
            Console.WriteLine("   접근성 객체를 얻지 못했습니다.");
            continue;
        }
        var stack = new Stack<(IAccessible Acc, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && count < maxNodes)
        {
            var (acc, depth) = stack.Pop();
            count++;
            visit(Read(acc, depth));
            var n = Safe(() => acc.accChildCount);
            if (n <= 0)
            {
                continue;
            }
            var children = new object[n];
            if (Native.AccessibleChildren(acc, 0, n, children, out var got) != 0)
            {
                continue;
            }
            for (var i = got - 1; i >= 0; i--)
            {
                // 정수 자식 ID(단순 요소)는 Chromium 웹 콘텐츠에서 쓰이지 않으므로 건너뛴다.
                if (children[i] is IAccessible child)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }
    }
}

static Node Read(IAccessible acc, int depth)
{
    const int self = 0;
    var role = Safe(() => acc.get_accRole(self)) switch
    {
        int id => Roles.Name(id),
        string tag => tag,
        _ => "?",
    };
    var name = Safe(() => acc.get_accName(self)) ?? "";
    var value = Safe(() => acc.get_accValue(self)) ?? "";
    var action = Safe(() => acc.get_accDefaultAction(self)) ?? "";
    var state = Safe(() => acc.get_accState(self)) is int st ? st : 0;
    int x = 0, y = 0, width = 0, height = 0;
    try
    {
        acc.accLocation(out x, out y, out width, out height, self);
    }
    catch
    {
        // 화면에 없는 요소는 위치를 주지 않는다.
    }
    return new Node(depth, role, name, value, action, state, x, y, width, height);
}

// 앱 내부 페이지 경로만 남긴다. 그 외 주소는 내용을 드러낼 수 있어 출력하지 않는다.
static string? AppUrl(string? url)
{
    if (url is null || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }
    var i = url.IndexOf("app.asar", StringComparison.OrdinalIgnoreCase);
    var tail = i >= 0 ? url[i..] : url;
    var q = tail.IndexOfAny(['?', '#']);
    return q >= 0 ? tail[..q] : tail;
}

static string Clip(string s)
{
    s = s.Replace("\r", "").Replace("\n", "⏎");
    return s.Length > 400 ? s[..400] + "…" : s;
}

static T? Safe<T>(Func<T> f)
{
    try
    {
        return f();
    }
    catch
    {
        return default;
    }
}

internal sealed record Win(IntPtr Hwnd, uint Pid, string Class, bool Visible, bool Minimized, Native.RECT Rect,
    int TitleLength, List<IntPtr> Renders);

internal sealed record Node(int Depth, string Role, string Name, string Value, string Action, int State,
    int X, int Y, int W, int H);

internal static class Roles
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [0x09] = "window", [0x0A] = "client", [0x0B] = "menupopup", [0x0C] = "menuitem", [0x0F] = "document",
        [0x10] = "pane", [0x14] = "grouping", [0x15] = "separator", [0x16] = "toolbar", [0x18] = "table",
        [0x19] = "columnheader", [0x1A] = "rowheader", [0x1C] = "row", [0x1D] = "cell", [0x1E] = "link",
        [0x21] = "list", [0x22] = "listitem", [0x23] = "outline", [0x24] = "outlineitem", [0x25] = "pagetab",
        [0x28] = "graphic", [0x29] = "statictext", [0x2A] = "text", [0x2B] = "pushbutton", [0x2C] = "checkbox",
        [0x2D] = "radiobutton", [0x2E] = "combobox", [0x30] = "progressbar", [0x33] = "slider",
        [0x3C] = "pagetablist", [0x3E] = "splitbutton",
    };

    public static string Name(int id) => Names.TryGetValue(id, out var n) ? n : $"role{id:X}";
}
