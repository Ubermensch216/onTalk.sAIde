using System.Diagnostics;
using System.Text;
using Accessibility;
using OnTalk.Probe;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

// 온톡 연동 진단 도구.
//   windows                         온톡 프로세스의 최상위 창 목록 (제목은 글자 수만)
//   activate                        접근성 트리 활성화 신호 전송
//   stats [--hwnd 0x..]             창별 접근성 요소 집계 (이름·본문 미출력)
//   tree --hwnd 0x.. [--content]    요소 트리 출력. --content가 있을 때만 이름·값 출력
//   notes [--content]               사이드카 어댑터가 해석한 쪽지 창 결과
//   analyze                         열린 받은 쪽지를 로컬 AI로 분석해 검증 결과 출력
//   semantic                        합성 쪽지로 뜻 검색 점수 분포 확인 (온톡과 무관)
//   apply --hwnd 0x..               사이드카의 작성창 반영 기능 시험: 시험 문장을 넣고 확인한 뒤 비운다
//   clip                            클립보드 보존 기능 시험: 떠 두기, 빌려 쓰기, 되돌리기 후 비교
//   write                           합성 쪽지로 AI 답장 초안 시험 (온톡과 무관)
//   look --hwnd 0x.. [--content]    라이브러리로 읽은 트리(경로 포함). 탐색용
//   paste --hwnd 0x.. --path P --text T [--enter]  입력창을 클릭해 글자를 붙여넣는다. 사용자 허락 범위에서만
//   do --hwnd 0x.. --path 0/1/2     해당 요소의 기본 동작(클릭) 실행. 사용자 허락 범위에서만
//   reserve --hwnd 0x.. --at "2026-10-05 09:05"   예약 일시를 설정하고 확인한 뒤 취소한다 (전송 없음)
//   people --hwnd 0x.. --name 이름   받는 사람 추가 창에서 검색만 한다 (선택·전송 없음)
//   group --hwnd 0x.. --name 이름 --title 직급   그 사람 한 명짜리 그룹을 수신자로 넣는 흐름 시험 (전송 없음)
//   backupcheck                     실제 보관 자료를 백업해 임시 폴더에 되살려 보고 건수를 비교한다 (원본은 건드리지 않음)
//   prepare --hwnd 0x.. --text T --at "yyyy-MM-dd HH:mm"   본문과 예약을 넣어 전송 직전 상태로 만든다 (전송 없음)
//   setvalue --hwnd 0x.. --path P --text T   접근성 기능으로 입력창 값을 바꾸는 실험 (창을 앞으로 가져오지 않음)
//   input --hwnd 0x..               빈 새 쪽지 창에 붙여넣기 시험 (사용자 허락 후에만 실행)
// input 명령을 빼면 입력·클릭 등 온톡을 조작하는 동작은 하지 않는다.
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

if (command == "soak")
{
    // 감시 루프의 메모리 사용을 본다.
    using var watcher = new NoteWatcher();
    var ticks = 0;
    watcher.Updated += _ => Interlocked.Increment(ref ticks);
    watcher.Start();
    for (var i = 0; i < 12; i++)
    {
        Thread.Sleep(2000);
        using var self = Process.GetCurrentProcess();
        Console.WriteLine($"ticks={ticks} private={self.PrivateMemorySize64 / 1048576}MB gcHeap={GC.GetTotalMemory(false) / 1048576}MB gen2={GC.CollectionCount(2)}");
    }
    return 0;
}
if (command == "prepare")
{
    var compose = new IntPtr(Convert.ToInt64(hwndArg!, 16));
    var applied = ComposeWriter.ApplyBody(compose, Option("--text")!, "");
    Console.WriteLine($"본문 반영: {applied.Status}, 클립보드 복원={applied.ClipboardRestored}");
    if (applied.Status != ApplyStatus.Applied)
    {
        return 4;
    }
    var set = ComposeActions.SetReservation(compose, DateTime.Parse(Option("--at")!));
    var state = ComposeWriter.Read(compose);
    Console.WriteLine($"예약 설정: {set}");
    Console.WriteLine($"전송 직전 상태: 수신 [{string.Join(", ", state?.Recipients ?? [])}], {state?.UsedBytes}/{state?.LimitBytes} Bytes, 예약 {state?.Reservation:yyyy-MM-dd HH:mm}, SMS={state?.SmsChecked}, 대화상자={state?.DialogOpen}");
    return 0;
}
if (command == "reserve")
{
    var compose = new IntPtr(Convert.ToInt64(hwndArg!, 16));
    var when = DateTime.Parse(Option("--at")!);
    var sw = Stopwatch.StartNew();
    var set = ComposeActions.SetReservation(compose, when);
    Console.WriteLine($"설정 {when:yyyy-MM-dd HH:mm}: {set} ({sw.ElapsedMilliseconds}ms), 작성 창 표시 = {ComposeWriter.Read(compose)?.Reservation:yyyy-MM-dd HH:mm}");
    var cancel = ComposeActions.CancelReservation(compose);
    var after = ComposeWriter.Read(compose);
    Console.WriteLine($"취소: {cancel}, 예약 남음={after?.Reservation is not null}, 대화상자={after?.DialogOpen}");
    return 0;
}
if (command == "dispatches")
{
    // 발송 기록의 상태만 본다. 본문과 수신자는 출력하지 않는다.
    using var store = ProfileStore.Open(ProfileStore.DefaultRoot, OnTalkAccount.DetectCandidate()!);
    foreach (var d in store.GetDispatches(5))
    {
        Console.WriteLine($"{d.CreatedAt.ToLocalTime():MM-dd HH:mm:ss}  예약={d.ReserveAt:MM-dd HH:mm}  {d.Status}  수신 {d.RecipientNames.Count}명, {NoteBytes.Count(d.Body)} Bytes {d.Detail}");
    }
    return 0;
}
if (command == "backupcheck")
{
    var account = OnTalkAccount.DetectCandidate();
    if (account is null || !Directory.Exists(Path.Combine(ProfileStore.DefaultRoot, account)))
    {
        Console.WriteLine("보관 자료가 없습니다.");
        return 1;
    }
    var temp = Path.Combine(Path.GetTempPath(), "ontalk-saide-backupcheck-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var zip = Path.Combine(temp, "backup.zip");
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        int notes, tasks, dispatches;
        using (var store = ProfileStore.Open(ProfileStore.DefaultRoot, account))
        {
            (notes, tasks, dispatches) = (store.CountNotes(), store.GetTasks().Count, store.GetDispatches().Count);
            var sw = Stopwatch.StartNew();
            ProfileBackup.Create(store, zip, password);
            Console.WriteLine($"백업: {new FileInfo(zip).Length / 1024}KB, {sw.ElapsedMilliseconds}ms");
        }
        var result = ProfileBackup.Restore(Path.Combine(temp, "restored"), account, zip, password);
        using var restored = ProfileStore.Open(Path.Combine(temp, "restored"), account);
        Console.WriteLine($"원본: 쪽지 {notes}, 업무 {tasks}, 발송 기록 {dispatches}");
        Console.WriteLine($"되살림: 쪽지 {restored.CountNotes()}, 업무 {restored.GetTasks().Count}, 발송 기록 {restored.GetDispatches().Count}");
        Console.WriteLine(result.Notes == notes && restored.GetTasks().Count == tasks && restored.GetDispatches().Count == dispatches ? "결과: 일치" : "결과: 다름");
    }
    finally
    {
        Directory.Delete(temp, recursive: true);
    }
    return 0;
}
if (command == "group")
{
    var compose = new IntPtr(Convert.ToInt64(hwndArg!, 16));
    var found = ComposeActions.SearchRecipient(compose, Option("--name")!);
    var me = found.Rows.Where(r => r.Title == Option("--title") && r.Details.Any(d => d.Contains(Option("--dept") ?? ""))).ToList();
    if (found.Status != ActionStatus.Ok || me.Count != 1)
    {
        Console.WriteLine($"중단: 검색 {found.Status}, 그 직급의 사람 {me.Count}명");
        return 3;
    }
    var member = me[0].ToRecipient();
    var stale = member with { Details = [.. member.Details.Take(1), "없는 부서", .. member.Details.Skip(2)] };
    var wrong = ComposeActions.AddRecipients(compose, [stale]);
    Console.WriteLine($"소속이 달라진 구성원: 넣은 수 {wrong.Added}, 상태 {wrong.Status} (넣지 않고 멈춰야 함)");
    var before = ComposeWriter.Read(compose)?.Recipients.Count;
    var result = ComposeActions.AddRecipients(compose, [member]);
    var after = ComposeWriter.Read(compose);
    Console.WriteLine($"정확히 같은 구성원: 넣은 수 {result.Added}, 상태 {result.Status}, 수신자 {before} -> {after?.Recipients.Count}, 이름 일치={after?.Recipients.Contains(member.Name)}");
    return 0;
}
if (command == "people")
{
    var compose = new IntPtr(Convert.ToInt64(hwndArg!, 16));
    var found = ComposeActions.SearchRecipient(compose, Option("--name")!);
    Console.WriteLine($"검색: {found.Status}, 같은 이름 {found.Rows.Count}명");
    foreach (var row in found.Rows)
    {
        // 다른 사람의 정보는 출력하지 않는다. 구분에 쓸 정보가 몇 칸 있는지만 본다.
        Console.WriteLine($"   직급 {row.Title.Length}자, 세부 {row.Details.Count}칸");
    }
    Console.WriteLine($"서로 구분됨: {found.Rows.Select(r => (r.Title, string.Join("|", r.Details))).Distinct().Count() == found.Rows.Count}");
    return 0;
}
if (command == "setvalue")
{
    var target = new IntPtr(Convert.ToInt64(hwndArg!, 16));
    var render = Win32.FindRenderChild(target) ?? IntPtr.Zero;
    var node = MsaaReader.ReadTree(render)?.DescendantsAndSelf().FirstOrDefault(n => n.Path == Option("--path"));
    if (node is null || node.Role != AccNode.EditableText)
    {
        Console.WriteLine("그 경로에 입력창이 없습니다.");
        return 1;
    }
    var text = (Option("--text") ?? "").Replace("\\n", "\n");
    Console.WriteLine($"값 설정 호출: {MsaaReader.SetValue(render, node, text)}");
    Thread.Sleep(700);
    var state = ComposeWriter.Read(target);
    Console.WriteLine(state is null
        ? "작성 창 상태를 읽지 못함"
        : $"다시 읽음: 본문 일치={state.Body == text}, 표시 {state.UsedBytes}/{state.LimitBytes} (계산 {NoteBytes.Count(text)})");
    return 0;
}
if (command == "paste")
{
    var target = new IntPtr(Convert.ToInt64(hwndArg!, 16));
    var tree = MsaaReader.ReadTree(Win32.FindRenderChild(target) ?? IntPtr.Zero);
    var node = tree?.DescendantsAndSelf().FirstOrDefault(n => n.Path == Option("--path"));
    if (node is null || node.Role != AccNode.EditableText)
    {
        Console.WriteLine("그 경로에 입력창이 없습니다.");
        return 1;
    }
    var focus = InputInjector.FocusByClick(target, node.Bounds);
    Console.WriteLine($"포커스: {focus}");
    if (focus != FocusOutcome.Focused)
    {
        return 4;
    }
    var (typed, restored) = InputInjector.ReplaceText(target, Option("--text") ?? "");
    var entered = args.Contains("--enter") && InputInjector.Tap(target, InputInjector.KeyEnter);
    Console.WriteLine($"입력={typed}, 클립보드 복원={restored}, Enter={entered}");
    return 0;
}
if (command is "look" or "do")
{
    var target = new IntPtr(Convert.ToInt64(hwndArg!, 16));
    var render = Win32.FindRenderChild(target) ?? IntPtr.Zero;
    var tree = MsaaReader.ReadTree(render);
    if (tree is null)
    {
        Console.WriteLine("읽지 못했습니다.");
        return 1;
    }
    if (command == "look")
    {
        var filter = Option("--grep");
        foreach (var n in tree.DescendantsAndSelf())
        {
            // 글자가 있거나 누를 수 있는 요소만 보여 준다.
            if (n.Name.Length == 0 && n.Value.Length == 0 && !n.HasAction || n.Bounds.IsEmpty)
            {
                continue;
            }
            var text = withContent ? $"\"{n.Name.ReplaceLineEndings("⏎")}\"" + (n.Value.Length > 0 ? $" ={n.Value.ReplaceLineEndings("⏎")}" : "") : $"[{n.Name.Length}자]";
            var line = $"{n.Path,-28} {n.Role,-11} {(n.HasAction ? "A" : " ")} {n.Bounds.X},{n.Bounds.Y} {n.Bounds.Width}x{n.Bounds.Height} s={n.State:X} {text}";
            if (filter is null || line.Contains(filter))
            {
                Console.WriteLine(line);
            }
        }
        return 0;
    }
    var node = tree.DescendantsAndSelf().FirstOrDefault(n => n.Path == Option("--path"));
    if (node is null)
    {
        Console.WriteLine("그 경로의 요소가 없습니다.");
        return 1;
    }
    Console.WriteLine($"대상: {node.Role} [{node.Name.Length}자] {node.Bounds.Width}x{node.Bounds.Height} -> 실행 {MsaaReader.Invoke(render, node)}");
    return 0;
}
if (command == "write")
{
    var source = new ReceivedNote("홍길동", "주무관 │ 총무과", "10월 2일 오전 09:00", null, 1,
        "안녕하세요. 총무과 홍길동입니다.\n부서별 보안점검 결과표를 10월 15일까지 제출해 주시기 바랍니다.", []);
    using var writer = new OllamaWriter();
    foreach (var instruction in (string[])["기한 내 제출하겠다고 답장", "담당자가 출장 중이라 며칠 늦어진다고 양해를 구하는 답장"])
    {
        var sw = Stopwatch.StartNew();
        var text = writer.WriteAsync(source, "", instruction, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"--- 요청: {instruction} ({sw.Elapsed.TotalSeconds:0.0}초, {NoteBytes.Count(text)} Bytes, 자리 표시 {DraftText.UnfilledPlaceholders(text).Count}개)");
        Console.WriteLine(text);
    }
    return 0;
}
if (command == "clip")
{
    static string Describe(ClipboardSnapshot s) =>
        string.Join(", ", s.Describe().Select(i => $"{i.Format}:{i.Bytes}B"));

    var before = ClipboardSnapshot.Capture();
    if (before is null || before.Incomplete)
    {
        Console.WriteLine("중단: 클립보드를 온전히 떠 둘 수 없어 시험하지 않습니다.");
        return 3;
    }
    Console.WriteLine($"전: {Describe(before)}");
    var borrowed = before.Borrow("클립보드 시험");
    var restored = before.Restore();
    var after = ClipboardSnapshot.Capture();
    Console.WriteLine($"빌려 쓰기={borrowed}, 되돌리기={restored}");
    Console.WriteLine($"후: {(after is null ? "(열지 못함)" : Describe(after))}");
    Console.WriteLine(after is not null && before.SameContentAs(after) ? "결과: 내용이 바이트 단위로 같다" : "결과: 달라졌다");
    return 0;
}
if (command == "apply")
{
    if (hwndArg is null)
    {
        Console.WriteLine("apply 명령에는 --hwnd가 필요합니다.");
        return 2;
    }
    var hwnd = new IntPtr(Convert.ToInt64(hwndArg, 16));
    var state = ComposeWriter.Read(hwnd);
    if (state is null)
    {
        Console.WriteLine("작성 창이 아닙니다.");
        return 1;
    }
    Console.WriteLine($"시작: 수신 {state.Recipients.Count}명, {state.UsedBytes}/{state.LimitBytes}, SMS={state.SmsChecked}, 대화상자={state.DialogOpen}");
    if (state.Body.Length > 0)
    {
        Console.WriteLine("중단: 본문이 비어 있지 않은 창에는 시험하지 않습니다.");
        return 3;
    }
    const string sample = "안녕하세요.\n반영 시험 문장입니다. ✅\n\n감사합니다.";
    var conflict = ComposeWriter.ApplyBody(hwnd, sample, "온톡에 없는 내용");
    Console.WriteLine($"충돌 감지(다른 내용을 기대): {conflict.Status}");
    var applied = ComposeWriter.ApplyBody(hwnd, sample, "");
    Console.WriteLine($"반영: {applied.Status}, 표시 {applied.After?.UsedBytes}/{applied.After?.LimitBytes} (계산 {NoteBytes.Count(sample)}), 클립보드 복원={applied.ClipboardRestored}");
    var same = ComposeWriter.ApplyBody(hwnd, sample, sample);
    Console.WriteLine($"같은 내용 다시 반영(입력 없이 통과해야 함): {same.Status}");
    var cleared = ComposeWriter.ApplyBody(hwnd, "", sample);
    Console.WriteLine($"비우기: {cleared.Status}, 표시 {cleared.After?.UsedBytes}/{cleared.After?.LimitBytes}");
    return 0;
}
if (command == "semantic")
{
    // 실제 쪽지가 아니라 지어낸 문장이다. 유사도 기준값을 정하려고 점수 분포를 본다.
    string[] notes =
    [
        "감사교육 이수 요청. 감사위원회 직원은 연간 56시간 이상 감사교육을 이수해야 합니다.",
        "10월 직원 월례조회가 대강당에서 열립니다. 전 직원 참석 바랍니다.",
        "출장 여비 정산 서류를 이번 주까지 회계팀으로 제출해 주세요.",
        "청사 주차장 도색 공사로 지하 2층 주차가 제한됩니다.",
        "정보보안 점검 결과 미조치 PC는 백신 업데이트를 실시하기 바랍니다.",
        "국정감사 요구자료 제출 요청. 최근 3년간 민원 처리 현황을 작성해 주세요.",
        "구내식당 메뉴 변경 안내. 금요일 점심은 특식으로 제공됩니다.",
        "연말정산 간소화 자료 제출 기간 안내드립니다.",
    ];
    string[] queries = ["교육 들어야 하는 시간", "차 어디에 세워야 하나", "경비 처리", "밥", "컴퓨터 바이러스", "휴가 신청 방법", "국회 자료"];
    using var embedder = new OllamaEmbedder();
    var sw = Stopwatch.StartNew();
    var vectors = notes.Select((n, i) => ((long)i, embedder.EmbedAsync(n, CancellationToken.None).GetAwaiter().GetResult())).ToList();
    Console.WriteLine($"쪽지 {notes.Length}건 임베딩 {sw.ElapsedMilliseconds}ms, 차원 {vectors[0].Item2.Length}");
    foreach (var q in queries)
    {
        sw.Restart();
        var qv = embedder.EmbedAsync(q, CancellationToken.None).GetAwaiter().GetResult();
        var top = Similarity.Top(qv, vectors, 3, 0f);
        Console.WriteLine($"\"{q}\" ({sw.ElapsedMilliseconds}ms)");
        foreach (var (id, score) in top)
        {
            Console.WriteLine($"   {score:0.00}  {notes[id][..Math.Min(28, notes[id].Length)]}");
        }
    }
    return 0;
}
if (command == "analyze")
{
    using var watcher = new NoteWatcher();
    using var done = new ManualResetEventSlim();
    WatchSnapshot? snapshot = null;
    watcher.Updated += s =>
    {
        snapshot ??= s;
        done.Set();
    };
    watcher.Start();
    done.Wait(TimeSpan.FromSeconds(20));
    if (snapshot?.Notes.Select(n => n.Content.Note).FirstOrDefault(n => n is not null) is not { } note)
    {
        Console.WriteLine("열린 받은 쪽지가 없습니다.");
        return 1;
    }
    using var analyzer = new OllamaAnalyzer();
    var sw = Stopwatch.StartNew();
    var sent = DateOnly.FromDateTime((note.SentAt?.Value ?? DateTimeOffset.Now).DateTime);
    var result = analyzer.AnalyzeAsync(note, sent, CancellationToken.None).GetAwaiter().GetResult();
    Console.WriteLine($"{analyzer.Model}, {sw.Elapsed.TotalSeconds:0.0}초, 근거 없어 버린 후보 {result.DroppedActions}개");
    Console.WriteLine($"요약: {result.Summary}");
    foreach (var a in result.Actions)
    {
        Console.WriteLine($"- {a.Title}\n    근거: {a.Evidence.ReplaceLineEndings(" ")}\n    기한: {a.DueText ?? "(원문에 없음)"} -> {a.Due?.ToString("yyyy-MM-dd") ?? "(날짜 없음)"}");
    }
    foreach (var u in result.Uncertainties)
    {
        Console.WriteLine($"? {u}");
    }
    return 0;
}
if (command == "notes")
{
    return Notes();
}

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
    case "input":
    {
        if (hwndArg is null || Selected().FirstOrDefault() is not { } target)
        {
            Console.WriteLine("input 명령에는 대상 창의 --hwnd가 필요합니다.");
            return 2;
        }
        var code = 0;
        // 클립보드는 STA 스레드에서만 쓸 수 있다.
        var sta = new Thread(() => code = InputProbe.Run(target, () =>
        {
            var list = new List<Node>();
            Walk(target, list.Add);
            return list;
        }));
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        return code;
    }
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

int Notes()
{
    using var watcher = new NoteWatcher();
    using var done = new ManualResetEventSlim();
    WatchSnapshot? snapshot = null;
    watcher.Updated += s =>
    {
        snapshot ??= s;
        done.Set();
    };
    var sw = Stopwatch.StartNew();
    watcher.Start();
    if (!done.Wait(TimeSpan.FromSeconds(20)) || snapshot is null)
    {
        Console.WriteLine("시간 안에 읽지 못했습니다.");
        return 1;
    }
    Console.WriteLine($"온톡 실행={snapshot.OnTalkRunning}, 쪽지 창 {snapshot.Notes.Count}개, {sw.ElapsedMilliseconds}ms");
    Console.WriteLine($"계정 후보 확인됨={OnTalkAccount.DetectCandidate() is not null}");
    foreach (var window in snapshot.Notes)
    {
        Console.WriteLine($"== 0x{window.Hwnd.ToInt64():X8} {window.Content.Kind} \"{window.Content.Title}\"");
        if (window.Content.Note is not { } note)
        {
            continue;
        }
        Console.WriteLine($"   시각 원문 \"{note.SentAtText}\" -> {note.SentAt?.Value:O} (연도 추정={note.SentAt?.YearInferred})");
        Console.WriteLine($"   수신 {note.RecipientCount}, 본문 {note.Body.Length}자 {note.Body.Split('\n').Length}줄, 첨부 {note.AttachmentNames.Count}개");
        if (withContent)
        {
            Console.WriteLine($"   발신 \"{note.SenderName}\" / \"{note.SenderDetail}\"");
            Console.WriteLine($"   첨부 {string.Join(", ", note.AttachmentNames)}");
            Console.WriteLine("   ---- 본문");
            Console.WriteLine(note.Body);
            Console.WriteLine("   ----");
        }
    }
    return 0;
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
