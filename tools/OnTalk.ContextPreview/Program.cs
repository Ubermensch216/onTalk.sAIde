using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OnTalk.Sidecar.App;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;
using OnTalk.Sidecar.AI;

namespace OnTalk.ContextPreview;

/// <summary>합성 자료만 사용해 WPF 화면을 렌더한다. 온톡 창을 읽거나 클릭하지 않는다.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/context-preview");
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application();
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/OnTalk.sAIde;component/Theme.xaml", UriKind.Relative)));
        using var store = ProfileStore.Open(Path.Combine(output, "synthetic-profile"), "preview");
        store.SaveGroup("예시 업무 그룹", [new Recipient("예시 담당자", "주무관", ["예시기관 총무팀"]) ]);
        var panel = new PanelWindow();
        Set(panel, "_store", store);
        var session = Guid.NewGuid(); var hwnd = new IntPtr(123);
        Set(panel, "_dockedTo", hwnd);
        var cases = new[]
        {
            ("friends", new SidecarContext(OnTalkSurface.Main, MainMenu.Friends)),
            ("organization", new SidecarContext(OnTalkSurface.Main, MainMenu.Organization)),
            ("conversations", new SidecarContext(OnTalkSurface.Main, MainMenu.Conversations)),
            ("notes", new SidecarContext(OnTalkSurface.Main, MainMenu.Notes)),
            ("unknown-menu", new SidecarContext(OnTalkSurface.Main)),
            ("received", new SidecarContext(OnTalkSurface.NoteRead, Title: "받은 쪽지")),
            ("compose-new", new SidecarContext(OnTalkSurface.NoteCompose, ComposeKind: NoteComposeKind.New)),
            ("compose-reply", new SidecarContext(OnTalkSurface.NoteCompose, ComposeKind: NoteComposeKind.Reply)),
            ("compose-all", new SidecarContext(OnTalkSurface.NoteCompose, ComposeKind: NoteComposeKind.ReplyAll)),
            ("compose-forward", new SidecarContext(OnTalkSurface.NoteCompose, ComposeKind: NoteComposeKind.Forward)),
            ("chat", new SidecarContext(OnTalkSurface.Chat)),
            ("excluded", new SidecarContext(OnTalkSurface.Excluded)),
            ("login", new SidecarContext(OnTalkSurface.Login)),
            ("read-after-login", new SidecarContext(OnTalkSurface.NoteRead, Title: "받은 쪽지"))
        };
        var note = new ReceivedNote("예시 발신자", "총무팀", "오전 09:00", null, 1, "점검 결과표를 10월 15일까지 제출해 주세요.", []);
        foreach (var (name, context) in cases)
        {
            var quote = context.ComposeKind is NoteComposeKind.Reply or NoteComposeKind.ReplyAll or NoteComposeKind.Forward
                ? "\n==========================================\n보낸사람 : 예시 발신자\n\n점검 결과표를 제출해 주세요." : "";
            var body = "안녕하세요. 제출 방법을 알려 주시면 감사하겠습니다." + quote;
            var compose = new ComposeState(["예시 수신자"], body, NoteBytes.Count(body), 4000, false, false, default);
            var content = new NoteWindowContent(context.Surface == OnTalkSurface.NoteRead ? NoteWindowKind.Received : NoteWindowKind.Compose,
                context.Title, context.Surface == OnTalkSurface.NoteRead ? note : null, context.Surface == OnTalkSurface.NoteCompose ? compose : null)
                { ComposeKind = context.ComposeKind };
            var snapshot = new WatchSnapshot(true, [new NoteWindow(hwnd, content) { SessionId = session }])
                { AccountCandidate = "preview", Contexts = [new WindowContext(hwnd, session, context)] };
            Set(panel, "_latestSnapshot", snapshot);
            Invoke(panel, "ApplyCurrentContext", snapshot);
            var root = (FrameworkElement)panel.Content;
            Render(root, Path.Combine(output, name + ".png"), 360, 800);
            Render(root, Path.Combine(output, name + "-short.png"), 360, 480);
            if (context.Surface != OnTalkSurface.NoteRead && ((FrameworkElement)panel.FindName("NoteView")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("다른 문맥에 받은 쪽지가 남았습니다.");
            if (context.Surface == OnTalkSurface.NoteCompose && ((ContentControl)panel.FindName("ComposeHost")).Content is not NoteComposePane)
                throw new InvalidOperationException("작성창에 편집기가 표시되지 않았습니다.");
            if (name == "read-after-login" && !((Button)panel.FindName("ComposeButton")).IsEnabled)
                throw new InvalidOperationException("로그인 대기 후 작업 도구가 복구되지 않았습니다.");
        }
        var personA = new ContactPerson("A", "예시 담당자", "주무관", ["예시기관", "총무과"], "온라인", "출장 여비 정산", "000-000-0000") { DetailsObservedAt = DateTimeOffset.Now };
        var personB = new ContactPerson("B", "예시 담당자", "주무관", ["예시기관", "회계과"], "오프라인");
        store.SaveReceived(new ReceivedNote(personA.Name, "주무관 · 총무과", "10월 4일 오전 10:00", null, 1, "출장 정산 자료를 공유해 주세요.", []), DateTimeOffset.Now);
        var peopleCases = new[]
        {
            ("friends-person", new PeopleSnapshot(MainMenu.Friends, [personA, personB], [], ["A"], DateTimeOffset.Now)),
            ("friends-group", new PeopleSnapshot(MainMenu.Friends, [personA, personB], [new("G", "업무 그룹", ContactScopeKind.Group, ["A", "B"], 5, true)], ["A", "B"], DateTimeOffset.Now, true)),
            ("organization-person", new PeopleSnapshot(MainMenu.Organization, [personA, personB], [new("D", "총무과", ContactScopeKind.Department, ["A"])], ["A"], DateTimeOffset.Now)),
            ("organization-department", new PeopleSnapshot(MainMenu.Organization, [personA, personB], [new("D", "관찰한 부서", ContactScopeKind.Department, ["A", "B"], Selected:true)], ["A", "B"], DateTimeOffset.Now)),
            ("people-ambiguous", new PeopleSnapshot(MainMenu.Friends, [personA with { Duty="", Phone="", DetailsObservedAt=null }, personA with { Key="C", Duty="", Phone="", DetailsObservedAt=null }], [], ["A"], DateTimeOffset.Now)),
            ("people-no-selection", new PeopleSnapshot(MainMenu.Friends, [personA, personB], [], [], DateTimeOffset.Now))
        };
        foreach (var (name, people) in peopleCases)
        {
            var context = new SidecarContext(OnTalkSurface.Main, people.Menu);
            var snapshot = new WatchSnapshot(true, []) { AccountCandidate="preview", Contexts=[new WindowContext(hwnd,session,context) { People=people }] };
            Set(panel,"_latestSnapshot",snapshot); Invoke(panel,"ApplyCurrentContext",snapshot);
            if (((ContentControl)panel.FindName("PeopleHost")).Content is not PeoplePane pane || pane.Chosen.Count != people.SelectedKeys.Count)
                throw new InvalidOperationException("사람·부서 선택 문맥이 표시되지 않았습니다.");
            Render((FrameworkElement)panel.Content,Path.Combine(output,name+".png"),360,800);
            Render((FrameworkElement)panel.Content,Path.Combine(output,name+"-short.png"),360,480);
        }
        ContactPreparation? prepared = null;
        var contacts = new PeoplePane(store, MainMenu.Friends, p=>prepared=p, _=>{});
        contacts.Refresh(peopleCases[0].Item2);
        contacts.Refresh(peopleCases[0].Item2 with { People=[personA with { Duty="", Phone="", DetailsObservedAt=null }, personB] });
        if (contacts.Chosen.Single().Duty != personA.Duty) throw new InvalidOperationException("일시 상세가 시각과 함께 보존되지 않았습니다.");
        contacts.Refresh(peopleCases[0].Item2 with { SelectedKeys=["B"] });
        if (contacts.Chosen.Single().Key != "B") throw new InvalidOperationException("같은 목록에서 바뀐 선택이 반영되지 않았습니다.");
        contacts.Refresh(peopleCases[0].Item2 with { SelectedKeys=[] });
        var list = (StackPanel)Get(contacts,"_rows");
        var checkbox = (CheckBox)((StackPanel)list.Children[0]).Children[0]; checkbox.IsChecked=true; checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!contacts.SelectionNotice.Contains("직접 선택")) throw new InvalidOperationException("수동 선택을 온톡 선택으로 잘못 표시했습니다.");
        ((Button)Get(contacts,"_start")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (prepared?.People.Count != 1 || !prepared.InitialDraft.Contains("{문의할 내용}")) throw new InvalidOperationException("연락 준비가 누락됐습니다.");
        // 같은 행 주소에 다른 사람이 나타나면 선택을 유지하지 않는다.
        contacts.Refresh(peopleCases[0].Item2 with { People=[personA with { Name="다른 사람" },personB], SelectedKeys=[] });
        if (contacts.Chosen.Count != 0) throw new InvalidOperationException("교체된 사람에게 이전 선택이 남았습니다.");
        using var preparationWriter = new OllamaWriter();
        var preparedHelper = new ComposeWindow(store, preparationWriter, null, preparation:prepared);
        if (((ComboBox)preparedHelper.FindName("TargetBox")).SelectedItem is not null || ((Button)preparedHelper.FindName("SendButton")).IsEnabled)
            throw new InvalidOperationException("연락 후보만으로 수신자나 작성창을 자동 선택했습니다.");
        if (((TextBox)preparedHelper.FindName("RecipientName")).Text != personA.Name)
            throw new InvalidOperationException("연락 후보를 수신자 검색 준비로 넘기지 못했습니다.");
        Render((FrameworkElement)preparedHelper.Content,Path.Combine(output,"prepared-contact-compose.png"),680,780);
        Render((FrameworkElement)preparedHelper.Content,Path.Combine(output,"prepared-contact-compose-short.png"),640,520);
        preparedHelper.Close();
        using var candidateEmbedder = new OllamaEmbedder();
        var candidateSearch = new SearchWindow(store, candidateEmbedder);
        var candidateNote = store.GetContactNoteCandidates(personA.Name).First();
        candidateSearch.ShowContactCandidate(candidateNote);
        if (((TextBox)candidateSearch.FindName("DetailBody")).Text != candidateNote.Note.Body ||
            !((TextBlock)candidateSearch.FindName("DetailNotice")).Text.Contains("같은 사람인지"))
            throw new InvalidOperationException("보관 후보의 원문·신원 확인 안내가 누락됐습니다.");
        candidateSearch.Close();
        // 대상이 없는 스냅샷은 다른 창의 받은 쪽지로 대체하지 않는다.
        Invoke(panel, "ApplyCurrentContext", new WatchSnapshot(true, []) { AccountCandidate = "preview" });
        if (((FrameworkElement)panel.FindName("NoteView")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("대상 미확인 상태에 쪽지가 남았습니다.");
        using var writer = new OllamaWriter();
        var helper = new ComposeWindow(store, writer, null,
            new ComposeTarget(hwnd, "합성 답장", new ComposeState([], "", 0, 4000, false, false, default))
                { SessionId = session, Kind = NoteComposeKind.Reply }, "기존 초안");
        var revision = (long)Get(helper, "_revision");
        ((TextBox)helper.FindName("AiProposal")).Text = "AI 제안";
        Set(helper, "_proposalRevision", revision);
        ((FrameworkElement)helper.FindName("AiReview")).Visibility = Visibility.Visible;
        ((Button)helper.FindName("AcceptAiButton")).IsEnabled = true;
        Invoke(helper, "OnAcceptAi", helper, new RoutedEventArgs());
        if (helper.Draft != "AI 제안") throw new InvalidOperationException("확장 도우미의 제안 수락이 실패했습니다.");
        Invoke(helper, "OnUndo", helper, new RoutedEventArgs());
        if (helper.Draft != "기존 초안") throw new InvalidOperationException("확장 도우미의 초안 복원이 실패했습니다.");
        // 같은 글로 되돌려도 이전 버전의 제안은 수락되지 않는다.
        Invoke(helper, "OnAcceptAi", helper, new RoutedEventArgs());
        if (helper.Draft != "기존 초안") throw new InvalidOperationException("만료된 제안이 적용됐습니다.");
        Render((FrameworkElement)helper.Content, Path.Combine(output, "expanded-compose.png"), 680, 780);
        Render((FrameworkElement)helper.Content, Path.Combine(output, "expanded-compose-short.png"), 640, 520);
        helper.Close();
        panel.Close();
        Console.WriteLine($"PASS: 기본 {cases.Length}개 문맥, 사람 선택 {peopleCases.Length}종, 작성 도우미 2종 · 44개 이미지 · 선택 변경/수동 선택/상세 보존/연락 준비/제안 수락·만료 검사. {output}");
        return 0;
    }

    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static object Get(object target, string field) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Invoke(object target, string method, params object[] values) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, values);
    private static void Render(FrameworkElement root, string path, int width, int height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
