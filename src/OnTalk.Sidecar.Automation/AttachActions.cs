using System.Diagnostics;

namespace OnTalk.Sidecar.Automation;

/// <summary>
/// 온톡 작성 창의 "첨부 파일 → 추가"로 파일을 붙인다. 온톡이 여는 Windows 파일 선택창에 경로를 넣고 "열기"를 눌러
/// 온톡이 직접 파일을 읽게 하므로, 사용자가 온톡에서 손으로 붙인 것과 같은 결과가 된다.
/// 붙인 뒤에는 작성 창을 다시 읽어 첨부 목록에 그 파일 이름이 늘었는지 확인한다.
/// </summary>
public static class AttachActions
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(6);
    private const string FileDialogClass = "#32770";

    /// <summary>첨부 줄("첨부 파일")의 "추가" 단추. 수신 줄의 "추가"와 구분한다.</summary>
    public static AccNode? FindAddButton(AccNode tree)
    {
        var nodes = tree.DescendantsAndSelf().ToList();
        var label = nodes.FindIndex(n => n.Role == AccNode.StaticText && n.Name == "첨부 파일");
        if (label < 0) return null;
        return nodes.Skip(label + 1)
            .FirstOrDefault(n => n.Children.Any(c => c.Role == AccNode.StaticText && c.Name == "추가") && !n.Bounds.IsEmpty);
    }

    /// <summary>첨부 "추가"를 눌러 열린 파일 선택창. 못 열었으면 Zero.</summary>
    public static IntPtr OpenPicker(IntPtr compose)
    {
        if (!Win32.IsWindow(compose) || Win32.FindRenderChild(compose) is not { } render ||
            MsaaReader.ReadTree(render) is not { } tree || FindAddButton(tree) is not { } add)
        {
            return IntPtr.Zero;
        }
        // 이미 열려 있는 파일 선택창이 있으면(모달이라 또 열 수 없다) 그것을 쓴다.
        if (Dialogs().FirstOrDefault() is { } open && open != IntPtr.Zero) return open;
        var before = Dialogs().ToHashSet();
        if (!MsaaReader.Invoke(render, add)) return IntPtr.Zero;
        return Until(() => Dialogs().FirstOrDefault(d => !before.Contains(d)), d => d != IntPtr.Zero);
    }

    /// <summary>열린 파일 선택창에 전체 경로를 넣고 "열기"를 누른다. 창이 닫히면 true.</summary>
    public static bool ChooseFile(IntPtr dialog, string path)
    {
        var combo = Win32.FindWindowExW(dialog, IntPtr.Zero, "ComboBoxEx32", null);
        var inner = combo == IntPtr.Zero ? IntPtr.Zero : Win32.FindWindowExW(combo, IntPtr.Zero, "ComboBox", null);
        var edit = inner == IntPtr.Zero ? IntPtr.Zero : Win32.FindWindowExW(inner, IntPtr.Zero, "Edit", null);
        if (edit == IntPtr.Zero) return false;
        Win32.SendMessageTimeoutW(edit, Win32.WM_SETTEXT, IntPtr.Zero, path, Win32.SMTO_ABORTIFHUNG, 2000, out _);
        if (Win32.WindowText(edit) != path) return false;
        // IDOK(1) 단추. 제목이 "열기"인 첫 Button을 찾는다.
        var open = IntPtr.Zero;
        foreach (var (h, cls, text) in Win32.ChildWindows(dialog))
            if (cls == "Button" && text.StartsWith("열기", StringComparison.Ordinal)) { open = h; break; }
        if (open == IntPtr.Zero) return false;
        Win32.SendMessageTimeoutW(open, Win32.BM_CLICK, IntPtr.Zero, IntPtr.Zero, Win32.SMTO_ABORTIFHUNG, 3000, out _);
        return Until(() => !Win32.IsWindow(dialog) || !Win32.IsWindowVisible(dialog), gone => gone);
    }

    /// <summary>
    /// 작성 창에 파일 하나를 붙이고, 첨부 목록에 그 파일 이름이 하나 늘었는지 다시 읽어 확인한다.
    /// 온톡이 거절하면(용량·개수·exe) 목록이 늘지 않으므로 Mismatch로 알 수 있다.
    /// </summary>
    public static ActionStatus Attach(IntPtr compose, string path)
    {
        if (ComposeWriter.Read(compose) is not { } before) return ActionStatus.NotCompose;
        if (before.DialogOpen) return ActionStatus.UiNotFound;
        var name = Path.GetFileName(path);
        var dialog = OpenPicker(compose);
        if (dialog == IntPtr.Zero) return ActionStatus.UiNotFound;
        if (!ChooseFile(dialog, path))
        {
            Cancel(dialog);
            return ActionStatus.InputBlocked;
        }
        var after = Until(() => ComposeWriter.Read(compose), s => s is not null && s.Attachments.Count > before.Attachments.Count);
        return after is not null && after.Attachments.Count == before.Attachments.Count + 1 &&
               after.Attachments.Count(n => n == name) == before.Attachments.Count(n => n == name) + 1
            ? ActionStatus.Ok
            : ActionStatus.Mismatch;
    }

    /// <summary>첨부 목록에서 이름이 <paramref name="name"/>인 파일의 지우기(x)를 눌러 뺀다.</summary>
    public static ActionStatus Remove(IntPtr compose, string name)
    {
        if (ComposeWriter.Read(compose) is not { } before) return ActionStatus.NotCompose;
        if (!before.Attachments.Contains(name)) return ActionStatus.Ok;
        if (!Win32.IsWindow(compose) || Win32.FindRenderChild(compose) is not { } render ||
            MsaaReader.ReadTree(render) is not { } tree) return ActionStatus.NotCompose;
        // 파일 줄은 [이름 글자를 담은 DIV, 지우기(x) DIV] 순서다. 이름 DIV 다음의 누를 수 있는 형제가 지우기 단추다.
        static bool HoldsName(AccNode c, string name) => c.Children.Any(g => g.Role == AccNode.StaticText && g.Name == name);
        var row = tree.DescendantsAndSelf().FirstOrDefault(n => n.Children.Any(c => HoldsName(c, name)));
        var remove = row?.Children.SkipWhile(c => !HoldsName(c, name)).Skip(1).FirstOrDefault(c => c.HasAction);
        if (remove is null || !MsaaReader.Invoke(render, remove)) return ActionStatus.UiNotFound;
        var after = Until(() => ComposeWriter.Read(compose), s => s is not null && s.Attachments.Count < before.Attachments.Count);
        return after is not null && after.Attachments.Count == before.Attachments.Count - 1 ? ActionStatus.Ok : ActionStatus.Mismatch;
    }

    private static void Cancel(IntPtr dialog)
    {
        foreach (var (h, cls, text) in Win32.ChildWindows(dialog))
            if (cls == "Button" && text == "취소")
            {
                Win32.SendMessageTimeoutW(h, Win32.BM_CLICK, IntPtr.Zero, IntPtr.Zero, Win32.SMTO_ABORTIFHUNG, 3000, out _);
                break;
            }
        Until(() => !Win32.IsWindow(dialog) || !Win32.IsWindowVisible(dialog), gone => gone);
    }

    public static List<(IntPtr Hwnd, string Class, string Text)> Describe(IntPtr dialog) => Win32.ChildWindows(dialog);

    private static List<IntPtr> Dialogs() =>
        Win32.VisibleTopLevelWindows(
            System.Diagnostics.Process.GetProcessesByName("온톡").Select(p => (uint)p.Id), FileDialogClass);

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
