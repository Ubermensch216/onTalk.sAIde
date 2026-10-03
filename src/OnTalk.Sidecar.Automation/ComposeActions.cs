using System.Diagnostics;

namespace OnTalk.Sidecar.Automation;

public enum ActionStatus
{
    Ok,
    /// <summary>작성 창이 아니거나 닫혔다.</summary>
    NotCompose,
    /// <summary>눌러야 할 단추나 기다리던 화면이 나오지 않았다. 온톡 화면 구성이 달라졌을 수 있다.</summary>
    UiNotFound,
    /// <summary>입력창에 포커스를 주지 못했다(가려짐, 창이 앞에 오지 않음, 조합 키 눌림).</summary>
    InputBlocked,
    /// <summary>조작 뒤 다시 읽은 값이 기대와 다르다. 더 진행하지 않았다.</summary>
    Mismatch,
}

public sealed record SearchOutcome(ActionStatus Status, IntPtr SearchWindow, IReadOnlyList<PersonRow> Rows);

/// <summary>전송 직전 온톡 작성 창이 이 내용과 같아야 한다.</summary>
public sealed record ExpectedCompose(IReadOnlyList<string> Recipients, string Body, DateTime? Reservation, bool SmsChecked);

public enum SendStatus
{
    /// <summary>전송 단추를 눌렀고 작성 창이 닫혔다.</summary>
    WindowClosed,
    /// <summary>전송 단추를 눌렀지만 작성 창이 그대로 있다.</summary>
    StillOpen,
    /// <summary>작성 창의 내용이 확인한 내용과 달라 누르지 않았다.</summary>
    Mismatch,
    /// <summary>작성 창이나 전송 단추를 찾지 못해 누르지 않았다.</summary>
    NotSent,
}

public sealed record SendOutcome(SendStatus Status, string Detail);

/// <summary>
/// 온톡 작성 창의 단추를 누르는 조작. 단추는 좌표가 아니라 요소를 지정해 누르므로 다른 창이 눌리지 않는다.
/// 모든 조작은 누른 뒤 화면을 다시 읽어 결과를 확인한다.
/// </summary>
public static class ComposeActions
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(4);

    /// <summary>"받는 사람 추가" 창을 열어 이름으로 검색하고 결과 행을 돌려준다. 아무도 고르지 않는다.</summary>
    public static SearchOutcome SearchRecipient(IntPtr compose, string name)
    {
        if (ComposeWriter.Read(compose) is null || Tree(compose) is not { } tree)
        {
            return new SearchOutcome(ActionStatus.NotCompose, IntPtr.Zero, []);
        }
        var search = FindUserSearchWindow();
        if (search == IntPtr.Zero)
        {
            // 수신 줄의 "추가". 첨부 파일 줄에도 같은 글자가 있어 먼저 나오는 것을 쓴다.
            if (tree.ButtonLabeled("추가") is not { } add || !Click(compose, add))
            {
                return new SearchOutcome(ActionStatus.UiNotFound, IntPtr.Zero, []);
            }
            search = Until(FindUserSearchWindow, h => h != IntPtr.Zero);
            if (search == IntPtr.Zero)
            {
                return new SearchOutcome(ActionStatus.UiNotFound, IntPtr.Zero, []);
            }
        }
        var box = Until(() => Tree(search) is { } t ? OnTalkPages.SearchBox(t) : null, b => b is not null);
        if (box is null)
        {
            return new SearchOutcome(ActionStatus.UiNotFound, search, []);
        }
        if (InputInjector.FocusByClick(search, box.Bounds) != FocusOutcome.Focused ||
            !InputInjector.ReplaceText(search, name).Typed || !InputInjector.Tap(search, InputInjector.KeyEnter))
        {
            return new SearchOutcome(ActionStatus.InputBlocked, search, []);
        }
        // 검색 전 목록(친구 목록 등)이 남아 있을 수 있어, 검색어를 이름으로 가진 행이 나올 때까지 기다린다.
        var rows = Until(() => Tree(search) is { } t ? OnTalkPages.PersonRows(t) : [], r => r.Any(p => p.Name == name));
        return new SearchOutcome(ActionStatus.Ok, search, rows.Where(p => p.Name == name).ToList());
    }

    /// <summary>검색 결과에서 한 사람을 골라 확인을 누르고, 작성 창 수신자에 그 이름이 늘었는지 확인한다.</summary>
    public static ActionStatus AddRecipient(IntPtr compose, IntPtr search, PersonRow person)
    {
        if (ComposeWriter.Read(compose) is not { } before)
        {
            return ActionStatus.NotCompose;
        }
        // 행을 다시 읽어 같은 사람을 찾는다. 그 사이 목록이 바뀌었으면 누르지 않는다.
        var row = Tree(search) is { } tree
            ? OnTalkPages.PersonRows(tree).SingleOrDefault(p => p.SamePerson(person))
            : null;
        if (row is null || OnTalkPages.SelectedCount(Tree(search)!) != 0 || !Click(search, row.Node))
        {
            return ActionStatus.UiNotFound;
        }
        var selected = Until(() => Tree(search) is { } t ? OnTalkPages.SelectedCount(t) : 0, n => n > 0);
        if (selected != 1 || Tree(search)?.ButtonLabeled("확인") is not { } confirm)
        {
            // 한 명만 골라졌다고 확인되지 않으면 확인을 누르지 않는다.
            return ActionStatus.Mismatch;
        }
        if (!Click(search, confirm))
        {
            return ActionStatus.UiNotFound;
        }
        var after = Until(() => ComposeWriter.Read(compose), s => s is not null && s.Recipients.Count == before.Recipients.Count + 1);
        return after is not null && after.Recipients.Count == before.Recipients.Count + 1 &&
               after.Recipients.Count(n => n == person.Name) == before.Recipients.Count(n => n == person.Name) + 1
            ? ActionStatus.Ok
            : ActionStatus.Mismatch;
    }

    /// <summary>
    /// 저장해 둔 사람들을 차례로 수신자에 넣는다. 이름으로 검색한 뒤 저장할 때 본 직급·소속과 정확히 같은
    /// 한 사람만 넣는다. 한 명이라도 찾지 못하거나 넣지 못하면 거기서 멈춘다.
    /// </summary>
    /// <returns>Added: 넣은 인원. Failed: 멈춘 사람(모두 넣었으면 null).</returns>
    public static (int Added, Core.Recipient? Failed, ActionStatus Status) AddRecipients(
        IntPtr compose, IReadOnlyList<Core.Recipient> members)
    {
        var added = 0;
        foreach (var member in members)
        {
            var found = SearchRecipient(compose, member.Name);
            if (found.Status != ActionStatus.Ok)
            {
                return (added, member, found.Status);
            }
            var matches = found.Rows.Where(r => r.Title == member.Title && r.Details.SequenceEqual(member.Details)).ToList();
            if (matches.Count != 1)
            {
                // 인사이동 등으로 소속이 달라졌으면 같은 이름의 다른 사람을 넣지 않고 멈춘다.
                return (added, member, ActionStatus.Mismatch);
            }
            var status = AddRecipient(compose, found.SearchWindow, matches[0]);
            if (status != ActionStatus.Ok)
            {
                return (added, member, status);
            }
            added++;
        }
        return (added, null, ActionStatus.Ok);
    }

    /// <summary>예약 전송 일시를 정한다. 대화상자에 표시된 일시와 작성 창에 표시된 일시를 모두 다시 읽어 확인한다.</summary>
    public static ActionStatus SetReservation(IntPtr compose, DateTime when)
    {
        when = new DateTime(when.Year, when.Month, when.Day, when.Hour, when.Minute, 0);
        if (ComposeWriter.Read(compose) is not { } before || Tree(compose) is not { } tree)
        {
            return ActionStatus.NotCompose;
        }
        if (before.Reservation == when)
        {
            return ActionStatus.Ok;
        }
        if (OnTalkPages.ReservationDialog(tree) is null)
        {
            var open = tree.ButtonLabeled(before.Reservation is null ? "설정" : "변경");
            if (open is null || !Click(compose, open))
            {
                return ActionStatus.UiNotFound;
            }
        }
        if (Until(() => Dialog(compose), d => d is not null) is null)
        {
            return ActionStatus.UiNotFound;
        }

        // 달 맞추기
        for (var step = 0; step < 24; step++)
        {
            if (Dialog(compose) is not { } dialog || OnTalkPages.CalendarMonth(dialog) is not { } shown)
            {
                return ActionStatus.UiNotFound;
            }
            var distance = (when.Year - shown.Year) * 12 + when.Month - shown.Month;
            if (distance == 0)
            {
                break;
            }
            var arrow = distance > 0 ? shown.Next : shown.Previous;
            if (arrow is null || !Click(compose, arrow))
            {
                return ActionStatus.UiNotFound;
            }
            Until(() => Dialog(compose) is { } d ? OnTalkPages.CalendarMonth(d) : null,
                m => m is { } now && (now.Year, now.Month) != (shown.Year, shown.Month));
        }

        // 날짜
        if (Dialog(compose) is not { } atMonth || OnTalkPages.DayCell(atMonth, when.Year, when.Month, when.Day) is not { } cell ||
            !Click(compose, cell))
        {
            return ActionStatus.UiNotFound;
        }
        Until(() => Dialog(compose) is { } d ? OnTalkPages.ReservationDialogValue(d) : null, v => v?.Date == when.Date);

        // 오전/오후, 시, 분
        var (amPm, hour, minute) = Core.ReservationTime.ComboValues(when);
        string[] wanted = [amPm, hour, minute];
        for (var i = 0; i < wanted.Length; i++)
        {
            if (Dialog(compose) is not { } d || OnTalkPages.TimeCombos(d) is not { Count: 3 } combos)
            {
                return ActionStatus.UiNotFound;
            }
            if (combos[i].Name.Trim() == wanted[i])
            {
                continue;
            }
            if (!Click(compose, combos[i]))
            {
                return ActionStatus.UiNotFound;
            }
            var value = wanted[i];
            var item = Until(
                () => Tree(compose)?.DescendantsAndSelf().FirstOrDefault(n => n.Role == AccNode.ListItem && n.Name.Trim() == value),
                n => n is not null);
            if (item is null || !Click(compose, item))
            {
                return ActionStatus.UiNotFound;
            }
            var index = i;
            Until(() => Dialog(compose) is { } now ? OnTalkPages.TimeCombos(now).ElementAtOrDefault(index)?.Name.Trim() : null,
                n => n == value);
        }

        // 대화상자가 보여 주는 일시가 요청과 같을 때만 확인을 누른다.
        if (Dialog(compose) is not { } ready || OnTalkPages.ReservationDialogValue(ready) != when ||
            ready.ButtonLabeled("확인") is not { } confirm)
        {
            return ActionStatus.Mismatch;
        }
        if (!Click(compose, confirm))
        {
            return ActionStatus.UiNotFound;
        }
        var after = Until(() => ComposeWriter.Read(compose), s => s is { DialogOpen: false } && s.Reservation == when);
        return after is { DialogOpen: false } && after.Reservation == when ? ActionStatus.Ok : ActionStatus.Mismatch;
    }

    /// <summary>걸려 있는 예약을 풀어 즉시 전송 상태로 되돌린다.</summary>
    public static ActionStatus CancelReservation(IntPtr compose)
    {
        if (ComposeWriter.Read(compose) is not { } before || Tree(compose) is not { } tree)
        {
            return ActionStatus.NotCompose;
        }
        if (before.Reservation is null)
        {
            return ActionStatus.Ok;
        }
        if (tree.ButtonLabeled("취소") is not { } cancel || !Click(compose, cancel))
        {
            return ActionStatus.UiNotFound;
        }
        var after = Until(() => ComposeWriter.Read(compose), s => s is not null && s.Reservation is null);
        return after is not null && after.Reservation is null ? ActionStatus.Ok : ActionStatus.Mismatch;
    }

    /// <summary>
    /// 작성 창의 수신자·본문·예약·SMS가 <paramref name="expected"/>와 모두 같을 때만 전송 단추를 한 번 누른다.
    /// 결과가 불확실해도 다시 누르지 않는다.
    /// </summary>
    public static SendOutcome Send(IntPtr compose, ExpectedCompose expected)
    {
        if (ComposeWriter.Read(compose) is not { } state || Tree(compose) is not { } tree)
        {
            return new SendOutcome(SendStatus.NotSent, "작성 창을 찾지 못했습니다.");
        }
        var difference = Difference(state, expected);
        if (difference is not null)
        {
            return new SendOutcome(SendStatus.Mismatch, difference);
        }
        if (tree.ButtonLabeled("전송") is not { } send)
        {
            return new SendOutcome(SendStatus.NotSent, "전송 단추를 찾지 못했습니다.");
        }
        if (!Click(compose, send))
        {
            return new SendOutcome(SendStatus.NotSent, "전송 단추를 누르지 못했습니다.");
        }
        var closed = Until(() => !Win32.IsWindow(compose) || !Win32.IsWindowVisible(compose), gone => gone);
        return closed
            ? new SendOutcome(SendStatus.WindowClosed, "")
            : new SendOutcome(SendStatus.StillOpen, "전송 단추를 눌렀지만 작성 창이 닫히지 않았습니다.");
    }

    /// <summary>작성 창이 기대와 다르면 무엇이 다른지 한 문장으로 돌려준다. 같으면 null.</summary>
    public static string? Difference(ComposeState state, ExpectedCompose expected)
    {
        if (state.DialogOpen)
        {
            return "온톡 창에 대화상자가 떠 있습니다.";
        }
        if (state.Recipients.Count == 0)
        {
            return "수신자가 없습니다.";
        }
        if (!state.Recipients.Order().SequenceEqual(expected.Recipients.Order()))
        {
            return $"수신자가 다릅니다. 온톡 창: {string.Join(", ", state.Recipients)}";
        }
        if (state.Body != expected.Body.Replace("\r\n", "\n"))
        {
            return "본문이 확인한 내용과 다릅니다.";
        }
        if (state.UsedBytes != Core.NoteBytes.Count(expected.Body) || state.UsedBytes > state.LimitBytes)
        {
            return "바이트 표시가 본문과 맞지 않습니다.";
        }
        if (state.Reservation != expected.Reservation)
        {
            return state.Reservation is { } r
                ? $"예약 시각이 다릅니다. 온톡 창: {r:yyyy-MM-dd HH:mm}"
                : "온톡 창에는 예약이 걸려 있지 않습니다.";
        }
        return state.SmsChecked != expected.SmsChecked ? "SMS 발송 선택 상태가 다릅니다." : null;
    }

    private static AccNode? Tree(IntPtr hwnd) =>
        Win32.IsWindow(hwnd) && Win32.FindRenderChild(hwnd) is { } render ? MsaaReader.ReadTree(render) : null;

    private static AccNode? Dialog(IntPtr compose) => Tree(compose) is { } t ? OnTalkPages.ReservationDialog(t) : null;

    private static bool Click(IntPtr hwnd, AccNode node) =>
        Win32.FindRenderChild(hwnd) is { } render && MsaaReader.Invoke(render, node);

    private static IntPtr FindUserSearchWindow()
    {
        foreach (var (hwnd, render) in Win32.VisibleOnTalkWindows())
        {
            if (MsaaReader.ReadRoot(render) is { } root && OnTalkPages.IsUserSearchPage(root))
            {
                return hwnd;
            }
        }
        return IntPtr.Zero;
    }

    // 조건이 맞을 때까지 다시 읽는다. 끝내 맞지 않으면 마지막으로 읽은 값을 돌려준다.
    private static T Until<T>(Func<T> read, Func<T, bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var value = read();
            if (done(value) || clock.Elapsed > Wait)
            {
                return value;
            }
            Thread.Sleep(150);
        }
    }
}
