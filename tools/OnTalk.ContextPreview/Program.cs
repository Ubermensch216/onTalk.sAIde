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
        if (args is ["--inspect", var target] && (target == "follow" || long.TryParse(target, out _)))
        {
            var inspector = new Application(); inspector.Run(new LiveContextWindow(target == "follow" ? IntPtr.Zero : new IntPtr(long.Parse(target)))); return 0;
        }
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/context-preview");
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
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
                { AccountCandidate = "preview", Contexts = [new WindowContext(hwnd, session, context)
                {
                    Chat = context.Surface == OnTalkSurface.Chat ? new ChatSnapshot("예시 대화", [new("example:1", "발신자 미확인", "오전 10:00", new DateOnly(2026,10,4), "점검 결과를 확인해 주세요.")], DateTimeOffset.Now) : null,
                    Conversations = context.Menu == MainMenu.Conversations ? [new("room:1", "점검 협의", "표시 참여자 3명", "결과 공유 안내")] : []
                }] };
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
        VerifyChat(store, output);
        panel.Close();
        Console.WriteLine($"PASS: 기본 {cases.Length}개 문맥, 사람 선택 {peopleCases.Length}종, 작성 도우미 2종, 대화 4개 탭 · {Directory.GetFiles(output, "*.png").Length}개 이미지 · 선택/원문 고정/입력 비교·충돌·만료/업무 근거/제안 수락 검사. {output}");
        return 0;
    }

    private static void VerifyChat(ProfileStore store, string output)
    {
        using var handler = new PreviewAiHandler(); using var writer = new OllamaWriter(handler: handler);
        using var analyzer = new OllamaAnalyzer(handler: new PreviewAiHandler());
        var session = Guid.NewGuid();
        var snapshot = new ChatSnapshot("예시 점검 대화 · 방 ID 미확인", [
            new("preview-chat:1", "발신자 미확인", "오전 10:00", new DateOnly(2026,10,4), "점검 결과표를 확인해 주세요."),
            new("preview-chat:2", "예시 상대", "오전 10:01", new DateOnly(2026,10,4), "제출 날짜는 아직 협의 중입니다."),
            new("preview-chat:3", "발신자 미확인", "오전 10:02", new DateOnly(2026,10,4), "자료 양식은 어디에 있나요?")], DateTimeOffset.Now);
        var changed = 0;
        var input = new PreviewChatInput(snapshot);
        using var pane = new ChatPane(store, analyzer, writer, session, snapshot, () => changed++, input);
        pane.SetActive(true); Invoke(pane, "SelectRecent");
        var editor = (TextBox)Get(pane, "_editor"); editor.Text = "제출 날짜를 확인한 뒤 답변드릴게요.";
        pane.Refresh(snapshot with { Messages = [.. snapshot.Messages, new("preview-chat:4", "예시 상대", "오전 10:03", snapshot.Messages[0].Date, "추가로 확인할 내용이 있습니다.")] });
        if (pane.Source.Messages.Count != 3 || pane.Chosen.Count != 3 || pane.Draft != editor.Text) throw new InvalidOperationException("새 메시지가 선택 원문/초안을 바꿨습니다.");
        var tabs = ((DockPanel)pane.Content).Children.OfType<TabControl>().Single();
        for (var i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            if (i == 1) Finish((Task)Call(pane, "AnalyzeAsync"));
            if (i == 2)
            {
                handler.Reply = "확인 후 알려드릴게요.";
                Finish((Task)Call(pane, "SuggestAsync"));
                Invoke(pane, "AcceptProposal"); if (pane.Draft != handler.Reply) throw new InvalidOperationException("대화 제안 수락 실패");
                ((Button)Get(pane,"_undo")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (pane.Draft != "제출 날짜를 확인한 뒤 답변드릴게요.") throw new InvalidOperationException("대화 초안 되돌리기 실패");
                handler.Delay = new TaskCompletionSource<string>();
                var late = (Task)Call(pane,"SuggestAsync"); editor.Text = "직접 고친 최신 글"; handler.Delay.SetResult("오래된 제안"); Finish(late);
                Invoke(pane,"AcceptProposal"); if (pane.Draft != "직접 고친 최신 글") throw new InvalidOperationException("대화의 늦은 제안이 덮어썼습니다.");
                handler.Delay = null;
                Finish((Task)Call(pane,"SuggestAsync"));
            }
            if (i == 3)
            {
                ((ComboBox)Get(pane,"_taskMessage")).SelectedIndex = 0;
                ((TextBox)Get(pane,"_taskTitle")).Text = "합성 대화 점검 업무";
                Invoke(pane,"Register");
                if (changed != 1) throw new InvalidOperationException("대화 업무 등록 실패");
                var task = store.GetTasks().Single(t=>t.Title == "합성 대화 점검 업무");
                if (store.GetChatTaskSource(task.Id)?.Body != snapshot.Messages[0].Body || task.NoteId is not null) throw new InvalidOperationException("대화 업무 출처가 왜곡됐습니다.");
                Invoke(pane,"Register"); if(changed != 1) throw new InvalidOperationException("대화 업무가 중복 등록됐습니다.");
                store.DeleteTask(task.Id);
            }
            Render((FrameworkElement)pane.Content,Path.Combine(output,$"chat-tab-{i}.png"),360,800);
            Render((FrameworkElement)pane.Content,Path.Combine(output,$"chat-tab-{i}-short.png"),360,480);
        }
        tabs.SelectedIndex = 2;
        Finish((Task)Call(pane, "ReviewInputAsync"));
        if (input.Writes != 0 || ((StackPanel)Get(pane, "_inputReview")).Visibility != Visibility.Visible) throw new InvalidOperationException("입력 비교가 글을 쓰거나 비교를 표시하지 않았습니다.");
        RenderReply("chat-input-review.png",800);
        RenderReply("chat-input-review-short.png",480);
        RenderReply("chat-input-review-125.png",800,1.25);
        RenderReply("chat-input-review-150.png",800,1.5);
        Finish((Task)Call(pane,"ApplyInputAsync")); if(input.Writes != 0) throw new InvalidOperationException("확인 없이 입력을 반영했습니다.");
        ((CheckBox)Get(pane,"_inputConfirmed")).IsChecked=true; input.Current=input.Current with { Body="온톡에서 직접 편집한 글" };
        Finish((Task)Call(pane,"ApplyInputAsync")); if(input.Writes != 0 || !((TextBlock)Get(pane,"_status")).Text.Contains("바뀌어")) throw new InvalidOperationException("입력 충돌 처리 실패");
        Render((FrameworkElement)pane.Content,Path.Combine(output,"chat-input-conflict.png"),360,800);
        Render((FrameworkElement)pane.Content,Path.Combine(output,"chat-input-conflict-short.png"),360,480);
        Finish((Task)Call(pane,"ReviewInputAsync")); ((CheckBox)Get(pane,"_inputConfirmed")).IsChecked=true;
        editor.Text="비교 이후 고친 글"; Finish((Task)Call(pane,"ApplyInputAsync")); if(input.Writes != 0) throw new InvalidOperationException("변경한 초안에 이전 비교를 적용했습니다.");
        input.Gate=new TaskCompletionSource<ChatInputState?>();
        var lateRead=(Task)Call(pane,"ReviewInputAsync"); editor.Text="입력 읽기 중에 고친 글"; input.Gate.SetResult(input.Current); Finish(lateRead); input.Gate=null;
        if(((StackPanel)Get(pane,"_inputReview")).Visibility==Visibility.Visible) throw new InvalidOperationException("늦은 입력 읽기 결과를 표시했습니다.");
        Finish((Task)Call(pane,"ReviewInputAsync"));
        input.Gate=new TaskCompletionSource<ChatInputState?>(); input.Gate.SetResult(null); Finish((Task)Call(pane,"ReviewInputAsync")); input.Gate=null;
        if(((StackPanel)Get(pane,"_inputReview")).Visibility==Visibility.Visible || GetNullable(pane,"_inputTicket") is not null) throw new InvalidOperationException("입력 재검사 실패 후 이전 비교가 남았습니다.");
        Set(pane,"_inputReadWait",TimeSpan.FromMilliseconds(40));
        input.Gate=new TaskCompletionSource<ChatInputState?>(); var reads=input.Reads;
        Finish((Task)Call(pane,"ReviewInputAsync"));
        if ((bool)Get(pane,"_busy") || GetNullable(pane,"_inputTicket") is not null) throw new InvalidOperationException("입력 읽기 시간 초과 후 UI/비교가 잠겼습니다.");
        Finish((Task)Call(pane,"ReviewInputAsync")); Finish((Task)Call(pane,"ImportInputAsync"));
        if (input.Reads != reads + 1) throw new InvalidOperationException("느린 입력 읽기를 중복 시작했습니다.");
        input.Gate.SetResult(input.Current); Finish((Task)Get(pane,"_pendingInputRead")); input.Gate=null;
        if (GetNullable(pane,"_inputTicket") is not null || input.Writes != 0) throw new InvalidOperationException("시간 초과 뒤 늦은 결과를 반영했습니다.");
        Set(pane,"_inputReadWait",TimeSpan.FromSeconds(10));
        Finish((Task)Call(pane,"ReviewInputAsync")); ((CheckBox)Get(pane,"_inputConfirmed")).IsChecked=true;
        Finish((Task)Call(pane,"ApplyInputAsync")); if(input.Writes != 1 || input.Current.Body != pane.Draft) throw new InvalidOperationException("확인한 입력 반영 실패");
        editor.Clear(); Finish((Task)Call(pane,"ImportInputAsync")); if(pane.Draft != input.Current.Body) throw new InvalidOperationException("입력 글 가져오기 실패");
        ((Button)Get(pane,"_undo")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); if(pane.Draft.Length != 0) throw new InvalidOperationException("가져온 입력 글 되돌리기 실패");
        editor.Text="직접 고친 최신 글";
        Finish((Task)Call(pane,"ReviewInputAsync")); ((CheckBox)Get(pane,"_inputConfirmed")).IsChecked=true;
        pane.SetActive(false); Invoke(pane,"AcceptProposal"); if(pane.Draft != "직접 고친 최신 글") throw new InvalidOperationException("비활성 대화 초안 변경");
        Finish((Task)Call(pane,"ApplyInputAsync")); if(input.Writes != 1) throw new InvalidOperationException("비활성 대화에 입력 반영");
        void RenderReply(string name, int height, double scale = 1)
        {
            var path=Path.Combine(output,name);
            Render((FrameworkElement)pane.Content,path,360,height,scale);
            ((ScrollViewer)((TabItem)tabs.SelectedItem).Content).ScrollToEnd();
            Render((FrameworkElement)pane.Content,path,360,height,scale);
        }
        pane.Dispose(); if(store.GetDraft($"chat:{session:N}") != pane.Draft) throw new InvalidOperationException("대화 초안 복구 자료 누락");
        using var copyOnly = new ChatPane(store, analyzer, writer, Guid.NewGuid(), snapshot, () => {});
        copyOnly.SetActive(true);
        ((TextBox)Get(copyOnly,"_editor")).Text="확인 후 알려드리겠습니다.";
        var copyTabs=(TabControl)((DockPanel)copyOnly.Content).Children[1]; copyTabs.SelectedIndex=2;
        var copyScroll=(ScrollViewer)((TabItem)copyTabs.SelectedItem).Content;
        var copyButtons=((StackPanel)copyScroll.Content).Children.OfType<Button>().Select(b=>b.Content?.ToString()).ToList();
        if (!copyButtons.Contains("초안 복사") || copyButtons.Any(b=>b?.Contains("온톡 입력") == true)) throw new InvalidOperationException("제품의 복사 경로에 미검증 입력 기능이 노출됐습니다.");
        foreach (var height in new[]{800,480})
        {
            var copyPath=Path.Combine(output,$"chat-reply-copy-{height}.png");
            Render((FrameworkElement)copyOnly.Content,copyPath,360,height); copyScroll.ScrollToEnd();
            Render((FrameworkElement)copyOnly.Content,copyPath,360,height);
        }
        using var isolated = new ChatPane(store, analyzer, writer, Guid.NewGuid(), snapshot with { RoomHint="같은 제목의 다른 대화" }, () => {});
        if(isolated.Draft.Length != 0 || isolated.Chosen.Count != 0) throw new InvalidOperationException("다른 대화에 이전 초안/선택이 남았습니다.");
        var emptyList = new ConversationsPane(); emptyList.Refresh([]);
        if (((StackPanel)Get(emptyList,"_rows")).Children.Count == 0) throw new InvalidOperationException("빈 대화 목록 안내 누락");
        var sourceTask = store.AddChatTask("합성 출처 검색 업무",null,new ChatTaskSource(snapshot.RoomHint,snapshot.Messages[0].Sender,snapshot.Messages[0].Time,snapshot.Messages[0].Date,snapshot.Messages[0].Key,snapshot.Messages[0].Body,snapshot.Messages[0].Body,snapshot.ObservedAt),DateTimeOffset.Now);
        using var embedder = new OllamaEmbedder(); var search = new SearchWindow(store,embedder);
        ((TextBox)search.FindName("QueryBox")).Text="합성 출처 검색 업무";
        Invoke(search,"Search");
        var results=(ListBox)search.FindName("Results");
        var taskResult=results.Items.OfType<SearchRow>().FirstOrDefault(r=>r.Task?.Id==sourceTask.Id);
        if(taskResult is null) throw new InvalidOperationException("대화 업무 검색 결과 누락");
        results.SelectedItem=taskResult;
        if(!((TextBlock)search.FindName("DetailNotice")).Text.Contains("대화에서 직접 선택") || !((TextBox)search.FindName("DetailBody")).Text.Contains(snapshot.Messages[0].Body)) throw new InvalidOperationException("검색 창의 대화 업무 출처 누락");
        search.Close(); store.DeleteTask(sourceTask.Id);
    }

    private sealed class PreviewChatInput(ChatSnapshot chat) : IChatInput
    {
        public ChatInputState Current = new(1,new IntPtr(2),"synthetic-chat","synthetic-field",chat,"온톡에서 작성 중인 합성 글");
        public int Writes, Reads;
        public TaskCompletionSource<ChatInputState?>? Gate;
        public ChatInputState? Read() { Interlocked.Increment(ref Reads); return Gate is { } gate ? gate.Task.GetAwaiter().GetResult() : Current; }
        public ChatInputResult Apply(ChatInputState expected,string body)
        {
            if(Current.Body != expected.Body) return new(ChatInputStatus.Conflict,Current);
            Writes++; Current=Current with { Body=body }; return new(ChatInputStatus.Applied,Current);
        }
    }

    private static object Call(object target, string method) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, [])!;
    private static void Finish(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (!task.IsCompleted) throw new TimeoutException("합성 AI 검사가 완료되지 않았습니다.");
        task.GetAwaiter().GetResult();
    }
    private sealed class PreviewAiHandler : System.Net.Http.HttpMessageHandler
    {
        public string Reply { get; set; } = "{\"summary\":\"제출 날짜는 협의 중이며 양식 위치를 확인하는 흐름입니다.\",\"points\":[{\"kind\":\"question\",\"title\":\"자료 양식 위치 확인\",\"message\":3,\"evidence\":\"자료 양식은 어디에 있나요?\"}]}";
        public TaskCompletionSource<string>? Delay { get; set; }
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancel)
        {
            var reply = Delay is { } delay ? await delay.Task : Reply;
            return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new { message = new { content = reply } }), System.Text.Encoding.UTF8,"application/json") };
        }
    }

    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static object Get(object target, string field) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static object? GetNullable(object target, string field) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    private static void Invoke(object target, string method, params object[] values) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, values);
    private static void Render(FrameworkElement root, string path, int width, int height, double scale = 1)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width*scale), (int)(height*scale), 96*scale, 96*scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
