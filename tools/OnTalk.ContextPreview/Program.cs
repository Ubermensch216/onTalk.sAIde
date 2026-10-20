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
        var profileDir = Path.Combine(output, "synthetic-profile");
        if (Directory.Exists(profileDir)) Directory.Delete(profileDir, true);
        using var store = ProfileStore.Open(profileDir, "preview");
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
        var seeded = store.SaveReceived(new ReceivedNote(personA.Name, "주무관 · 총무과", "10월 4일 오전 10:00", null, 1, "출장 정산 자료를 공유해 주세요.", []), DateTimeOffset.Now);
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
            var peopleContent = ((ContentControl)panel.FindName("PeopleHost")).Content;
            if (people.Menu == MainMenu.Friends ? peopleContent is not HomePane : peopleContent is not PeoplePane pane || pane.Chosen.Count != people.SelectedKeys.Count)
                throw new InvalidOperationException("Home·부서 선택 문맥이 표시되지 않았습니다.");
            Render((FrameworkElement)panel.Content,Path.Combine(output,name+".png"),360,800);
            Render((FrameworkElement)panel.Content,Path.Combine(output,name+"-short.png"),360,480);
            if (name == "organization-person")
            {
                var statusText = (TextBlock)panel.FindName("StatusText");
                var origStatus = statusText.Text;
                statusText.Text = "연결됨 · 조직도";
                Invoke(panel, "UpdateFooter");
                Render((FrameworkElement)panel.Content, Path.Combine(output, "organization-panel-tall.png"), 400, 1421);
                statusText.Text = origStatus;
            }
        }
        ContactPreparation? prepared = null;
        using var contacts = new PeoplePane(p => prepared = p);
        var organization = peopleCases[2].Item2;
        var click = new RoutedEventArgs(ButtonBase.ClickEvent);
        contacts.Refresh(organization);
        if (contacts.Chosen.Single().Key != "A") throw new InvalidOperationException("온톡에서 선택한 직원이 표시되지 않았습니다.");
        contacts.Refresh(organization with { SelectedKeys=["B"] });
        if (contacts.Chosen.Single().Key != "B") throw new InvalidOperationException("같은 목록에서 바뀐 선택이 반영되지 않았습니다.");
        contacts.Refresh(organization with { SelectedKeys=["A","B"] });
        var allowed = (HashSet<string>)Get(contacts,"_presenceAllowed");
        var presenceChecks = ((WrapPanel)Get(contacts,"_presenceRow")).Children.OfType<CheckBox>().ToList();
        void SetPresence(string only) { foreach (var c in presenceChecks) { c.IsChecked = (string)c.Content == only; c.RaiseEvent(click); } }
        SetPresence("온라인");
        if (contacts.Chosen.Single().Key != "A") throw new InvalidOperationException("온라인 필터가 반영되지 않았습니다.");
        SetPresence("오프라인");
        if (contacts.Chosen.Single().Key != "B") throw new InvalidOperationException("오프라인 필터가 반영되지 않았습니다.");
        SetPresence("자리비움");
        if (contacts.Chosen.Count != 0) throw new InvalidOperationException("자리비움 필터에 다른 상태가 포함됐습니다.");
        foreach (var c in presenceChecks) { c.IsChecked = true; c.RaiseEvent(click); }
        if (contacts.Chosen.Count != 2 || allowed.Count != 5) throw new InvalidOperationException("상태 필터 해제가 반영되지 않았습니다.");
        var rows = (StackPanel)Get(contacts,"_rows");
        ((Button)((DockPanel)rows.Children[0]).Children[0]).RaiseEvent(click);
        if (contacts.Chosen.Single().Key != "B") throw new InvalidOperationException("개별 제외가 반영되지 않았습니다.");
        ((Button)Get(contacts,"_send")).RaiseEvent(click);
        if (prepared?.People.Count != 1 || prepared.People[0].Key != "B") throw new InvalidOperationException("쪽지 준비에 선택 직원이 넘어오지 않았습니다.");
        contacts.Refresh(organization with { SelectedKeys=[] });
        if (contacts.Chosen.Count != 0 || ((Button)Get(contacts,"_send")).IsEnabled) throw new InvalidOperationException("선택이 없는데 쪽지를 보낼 수 있습니다.");
        var candidate = (CheckBox)((StackPanel)Get(contacts,"_candidates")).Children[0]; candidate.IsChecked = true; candidate.RaiseEvent(click);
        if (contacts.Chosen.Single().Key != "A" || !contacts.SelectionNotice.Contains("1명")) throw new InvalidOperationException("온톡 선택이 없을 때 직접 고른 직원이 반영되지 않았습니다.");
        using var preparationWriter = new OllamaWriter();
        var preparedHelper = new ComposeWindow(store, preparationWriter, null, preparation:prepared);
        if (((ComboBox)preparedHelper.FindName("TargetBox")).SelectedItem is not null || ((Button)preparedHelper.FindName("SendButton")).IsEnabled)
            throw new InvalidOperationException("연락 후보만으로 수신자나 작성창을 자동 선택했습니다.");
        if (((TextBox)preparedHelper.FindName("RecipientName")).Text != personA.Name)
            throw new InvalidOperationException("연락 후보를 수신자 검색 준비로 넘기지 못했습니다.");
        Render((FrameworkElement)preparedHelper.Content,Path.Combine(output,"prepared-contact-compose.png"),680,780);
        Render((FrameworkElement)preparedHelper.Content,Path.Combine(output,"prepared-contact-compose-short.png"),640,520);
        var personalItem = new PersonalizedWorkItem { Person = personA, Body = "개인 본문" };
        Set(preparedHelper, "_activeItem", personalItem);
        ((TextBox)preparedHelper.FindName("Editor")).Text = personalItem.Body;
        Set(preparedHelper, "_targetSession", session); Set(preparedHelper, "_personalizedSession", session);
        ((List<Recipient>)Get(preparedHelper, "_picked")).Add(new(personA.Name, personA.Title, personA.Organization));
        bool Guard(ComposeState state) => (bool)preparedHelper.GetType().GetMethod("CheckPersonalizedTarget", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(preparedHelper, [state])!;
        var personalState = new ComposeState([personA.Name], "", 0, 4000, false, false, default);
        if (!Guard(personalState) || Guard(personalState with { Recipients = [personA.Name, personB.Name] }) || Guard(personalState with { SmsChecked = true })) throw new InvalidOperationException("개인 수신자 보호 실패");
        personalItem.DispatchId = 1;
        if (Guard(personalState)) throw new InvalidOperationException("개인 쪽지 재발송 보호 실패");
        preparedHelper.Close();
        // 쪽지 창(탭): 조직도에서 고른 직원이 ‘쪽지’의 받는 사람과 ‘개인별 쪽지’의 대상자에 똑같이 들어가야 한다.
        var hostPrep = new ContactPreparation([personA, personB], "온톡 조직도 선택", DateTimeOffset.Now);
        var host = new NoteHostWindow(store, preparationWriter, hostPrep, null, NoteTab.Note, new PreviewPersonalizedAutomation([personA, personB]));
        var hostKeys = ((List<ContactPerson>)Get(host.Note!, "_people")).Select(p => p.SelectionKey).ToList();
        var hostItems = ((PersonalizedWork)Get(host.Personalized!, "_work")).Items.Select(i => i.Person.SelectionKey).ToList();
        if (hostKeys.Count != 2 || !hostKeys.SequenceEqual(hostItems) || hostKeys[0] != personA.SelectionKey)
            throw new InvalidOperationException("쪽지 창의 두 탭에 조직도 선택 직원이 같게 들어가지 않았습니다.");
        var hostTabs = (TabControl)host.Content;
        if (hostTabs.Items.Count != 2 || ((TabItem)hostTabs.Items[0]).Header as string != "쪽지" || ((TabItem)hostTabs.Items[1]).Header as string != "개인별 쪽지")
            throw new InvalidOperationException("쪽지 창 탭 이름이 맞지 않습니다.");
        // 기본 창 크기 안에 쪽지 탭의 모든 구역이 스크롤 없이 들어가야 한다(받는 사람이 있을 때도, 없을 때도).
        var clientWidth = (int)Math.Round(NoteHostWindow.DefaultLayoutWidth * NoteHostWindow.MaxScale);
        var clientHeight = (int)Math.Round(NoteHostWindow.DefaultLayoutHeight * NoteHostWindow.MaxScale);
        var emptyHost = new NoteHostWindow(store, preparationWriter, null, null, NoteTab.Note, new PreviewPersonalizedAutomation([]));
        foreach (var (label, tabs) in new[] { ("받는 사람 2명", hostTabs), ("받는 사람 없음", (TabControl)emptyHost.Content) })
        {
            tabs.Measure(new Size(clientWidth, double.PositiveInfinity));
            if (tabs.DesiredSize.Height > clientHeight)
                throw new InvalidOperationException($"쪽지 창 기본 크기에 쪽지 탭이 다 들어가지 않습니다({label}: 필요 {tabs.DesiredSize.Height:0} > {clientHeight}).");
        }
        // 관리(설정) 창: 단축키 입력칸이 현재 조합을 보여 주는지 확인한다(전역 등록은 하지 않는다).
        var manage = new ManageWindow(store, new AppSettings(), "합성", (_, _) => { }, () => { },
            new HotkeyRegistration(() => IntPtr.Zero, new AppSettings()));
        if (((TextBox)Get(manage, "HotkeyBox")).Text != "Ctrl+Shift+Space (사용 중이 아님)") throw new InvalidOperationException("관리 창 단축키 표시 오류");
        Render((FrameworkElement)manage.Content, Path.Combine(output, "manage-hotkey.png"), 600, 1000);
        Render(hostTabs, Path.Combine(output, "note-host-note.png"), clientWidth, clientHeight);
        Render(hostTabs, Path.Combine(output, "note-host-note-min.png"), 648, 456);
        hostTabs.SelectedIndex = 1;
        Render(hostTabs, Path.Combine(output, "note-host-personalized.png"), clientWidth, clientHeight);
        Render(hostTabs, Path.Combine(output, "note-host-personalized-min.png"), 648, 456);
        var groupNote = new GroupNotePane(store, preparationWriter, new ContactPreparation([personA, personB], "온톡 조직도 선택", DateTimeOffset.Now),
            new PreviewPersonalizedAutomation([personA, personB]));
        var groupSend = (Button)Get(groupNote, "_sendButton");
        ((TextBox)Get(groupNote, "_editor")).Text = "";
        if (groupSend.IsEnabled) throw new InvalidOperationException("본문 없이 같은 쪽지를 보낼 수 있습니다.");
        ((TextBox)Get(groupNote, "_editor")).Text = "안녕하세요. 10월 정기 점검 일정을 안내드립니다.\n\n- 일시: 10월 22일(수) 14:00\n- 장소: 3층 회의실\n\n참석 여부를 회신해 주세요.";
        if (!groupSend.IsEnabled) throw new InvalidOperationException("같은 쪽지 발송 단추가 켜지지 않았습니다.");
        Render((FrameworkElement)groupNote.Content, Path.Combine(output, "group-note.png"), 760, 860);
        Render((FrameworkElement)groupNote.Content, Path.Combine(output, "group-note-short.png"), 620, 560);
        Render((FrameworkElement)groupNote.Content, Path.Combine(output, "group-note-full.png"), 600, 1100);
        store.SaveTemplate("개인별 인사평정 결과 안내", "안녕하세요.\n인사평정 결과(순위) : {평정순위}\n이의가 있는 경우 별도 연락 바랍니다.");
        Invoke(groupNote, "ReloadTemplates", "개인별 인사평정 결과 안내");
        var personal = new PersonalizedPane(
            store,
            new OllamaWriter(),
            [personA, personB],
            "{소속} {이름} ({직급})님, {과정명} 일정은 {교육일시}입니다.",
            (_, _) => Task.CompletedTask,
            new PreviewPersonalizedAutomation([personA, personB]));
        var work = (PersonalizedWork)Get(personal, "_work");
        var imported = PersonalizedImport.ReadCsv(
            "이름,소속,직급,과정명,교육일시\r\n" +
            $"{personA.Name},{string.Join(" / ", personA.Organization)},{personA.Title},개인정보 보호 교육,10월 22일 14:00\r\n" +
            $"{personB.Name},{string.Join(" / ", personB.Organization)},{personB.Title},정보보안 실무 과정,10월 23일 10:00\r\n");
        if (!imported.Ready || PersonalizedImport.Match(work, imported).Count > 0) throw new InvalidOperationException("개인별 CSV 헤더 가져오기 실패");
        Invoke(personal, "Load");
        Buttons(personal.Content).Single(b => b.Content?.ToString()!.StartsWith("서식으로") == true).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!work.Items[0].Body.Contains("개인정보 보호 교육") || !work.Items[1].Body.Contains("10월 23일 10:00")) throw new InvalidOperationException("개인별 본문 생성 실패");
        var grid = (DataGrid)Get(personal, "_grid");
        ((TextBox)Get(personal, "_preview")).Text = "개별 수정한 본문";
        grid.SelectedIndex = 1;
        if (work.Items[0].Body != "개별 수정한 본문") throw new InvalidOperationException("개별 수정 유실");
        Render((FrameworkElement)personal.Content, Path.Combine(output, "personalized-notes.png"), 600, 600);
        var keptValue = work.Items[1].Values["교육일시"]; work.Items[1].Values["교육일시"] = "";
        Invoke(personal, "RefreshGrid");
        Render((FrameworkElement)personal.Content, Path.Combine(output, "personalized-notes-empty.png"), 900, 900);
        work.Items[1].Values["교육일시"] = keptValue; Invoke(personal, "RefreshGrid");
        var savedList = new StackPanel { Background = System.Windows.Media.Brushes.White, Margin = new Thickness(8) };
        foreach (var w in new[] { work, new PersonalizedWork { Template = "안내", Items = work.Items } })
            savedList.Children.Add(new ComboBoxItem { Content = w, ContentTemplate = ((ComboBox)Get(personal, "_saved")).ItemTemplate });
        Render(savedList, Path.Combine(output, "personalized-saved-list.png"), 520, 140);
        Render((FrameworkElement)personal.Content, Path.Combine(output, "personalized-notes-full.png"), 600, 1500);
        Render((FrameworkElement)personal.Content, Path.Combine(output, "personalized-notes-short.png"), 700, 560);
        Finish(personal.RunBatchAsync());
        if (work.Items.Any(i => !i.Attempted || !i.Status.Contains("발송 완료")))
            throw new InvalidOperationException("개인별 쪽지 자동 연속 발송 검증 실패");
        var tagged = new PersonalizedWork { Draft = true, TemplateName = "교육 일정 안내", Items = [new() { Person = personA }] };
        var partial = new PersonalizedWork { TemplateName = "인사평정 결과 안내", Items = [new() { Person = personA, DispatchId = 1, Status = "발송 완료 · 작성창 닫힘" }, new() { Person = personB }] };
        var tagList = new StackPanel { Background = System.Windows.Media.Brushes.White, Margin = new Thickness(8) };
        foreach (var w in new[] { tagged, work, partial })
            tagList.Children.Add(new ComboBoxItem { Content = w, ContentTemplate = ((ComboBox)Get(personal, "_saved")).ItemTemplate });
        Render(tagList, Path.Combine(output, "personalized-saved-tags.png"), 520, 200);
        using var candidateEmbedder = new OllamaEmbedder();
        var candidateSearch = new SearchWindow(store, candidateEmbedder);
        var candidateNote = store.GetNote(seeded.NoteId)!;
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
        VerifyHome(store, output);
        panel.Close();
        Console.WriteLine($"PASS: 기본 {cases.Length}개 문맥, 사람 선택 {peopleCases.Length}종, 작성 도우미 2종, 대화 4개 탭 · {Directory.GetFiles(output, "*.png").Length}개 이미지 · 선택/원문 고정/입력 비교·충돌·만료/업무 근거/제안 수락 검사. {output}");
        return 0;
    }

    private static void VerifyHome(ProfileStore store, string output)
    {
        var now = DateTimeOffset.Now;
        var a = new ContactPerson("home:a", "예시 민경", "주무관", ["예시기관", "총무과"]);
        var b = new ContactPerson("home:b", "예시 지은", "주무관", ["예시기관", "회계과"]);
        var c = new ContactPerson("home:c", "예시 기승", "팀장", ["예시기관", "복지정책과"]);
        var people = new PeopleSnapshot(MainMenu.Friends, [a,b,c], [
            new("home:g1", "프로젝트 TF", ContactScopeKind.Group, [a.Key,b.Key], 4),
            new("home:g2", "자주 연락하는 동료", ContactScopeKind.Group, [a.Key,c.Key], 2)], [], now);
        for (var i = 0; i < 3; i++)
        {
            var person = new[] { a,b,c }[i];
            var body = new[] { "10월 점검 결과표를 오늘 중 공유해 주세요. 누락된 항목은 별도로 표시 부탁드립니다.",
                "사업비 집행 내역의 금액을 다시 확인해 주세요. 수정한 파일은 금요일까지 보내 주세요.",
                "복지 사업 회의 자료를 공유합니다. 다음 회의 안건을 검토해 주세요." }[i];
            var saved = store.SaveReceived(new(person.Name, person.Organization.Last(), "오늘 오전 09:30", null, 1, body, []), now.AddDays(-i));
            store.LinkContactNote(person, saved.NoteId, now);
            store.SaveAnalysis(saved.NoteId, "synthetic", "home-preview", new(body, [], [], 0), now);
            if (i < 2) store.AddTask(saved.NoteId, new[] { "10월 점검 결과표 공유", "사업비 집행 금액 재확인" }[i], body, null, HomeDashboard.Day(now).AddDays(i), now);
        }
        var messages = new ChatMessage[] {
            new("home:m1", a.Name, "오전 10:20", HomeDashboard.Day(now), "점검 양식은 기존 파일을 사용하면 될까요?"),
            new("home:m2", "나", "오전 10:22", HomeDashboard.Day(now), "네, 기존 양식으로 작성하고 누락 항목은 별도로 표시하겠습니다."),
            new("home:m3", a.Name, "오전 10:25", HomeDashboard.Day(now), "결과표는 오늘 중 공유 부탁드립니다.") };
        store.SaveHomeChat(a, new("점검 결과 협의", messages, now.AddHours(-1)), new("기존 양식으로 점검 결과를 정리하고, 누락 항목을 따로 표시해 오늘 공유하기로 했습니다.",
            [new("decision", "기존 양식 사용·누락 항목 별도 표시", messages[1], "기존 양식으로 작성하고 누락 항목은 별도로 표시"),
             new("action", "점검 결과표 오늘 공유", messages[2], "결과표는 오늘 중 공유")], 0), now);
        var older = messages.Select(m => m with { Key=m.Key+"older", Date=HomeDashboard.Day(now).AddDays(-2) }).ToList();
        store.SaveHomeChat(b, new("사업비 검토", older, now.AddDays(-2)), new("사업비 집행 내역의 수정 범위를 확인했습니다. 금액 재검토 후 파일을 공유하는 흐름입니다.", [], 0), now);
        var dispatch1 = store.BeginDispatch([a.Name, b.Name], "10월 점검 일정과 제출 양식을 안내드립니다. 금요일까지 회신 부탁드립니다.", null, now.AddHours(-3));
        store.FinishDispatch(dispatch1, DispatchStatus.WindowClosed, "");
        var dispatch2 = store.BeginDispatch([c.Name], "복지 사업 회의 안건 검토 요청드립니다.", now.AddDays(1).DateTime, now.AddDays(-1));
        store.FinishDispatch(dispatch2, DispatchStatus.WindowClosed, "");
        store.SaveHomeConversations([new("c1", $"{c.Name} {c.Title}", "2", "회의 자료 확인 부탁드립니다."), new("c2", "전체 공지방", "12", "시스템 점검 안내")], now.AddMinutes(-10));
        using var home = new HomePane(store, ()=>{});
        home.Refresh(people); home.SetActive(true);
        if (home.Dashboard is not { Notes.Count:3, MessageCount:6, OpenTasks.Count:2 }) throw new InvalidOperationException("Home 합성 집계 오류");
        Render(home, Path.Combine(output,"home-dashboard-full.png"),360,1520);
        Render(home, Path.Combine(output,"home-dashboard.png"),360,800);
        Render(home, Path.Combine(output,"home-dashboard-short.png"),360,480);
        Render(home, Path.Combine(output,"home-dashboard-125.png"),360,800,1.25);
        var groups = (ComboBox)Get(home,"_groups"); groups.SelectedIndex=1;
        if (home.Dashboard!.Notes.Count != 2) throw new InvalidOperationException("Home 그룹 필터 오류");
        Render(home, Path.Combine(output,"home-group.png"),360,1000);
        var friends = (ComboBox)Get(home,"_friends"); friends.SelectedIndex=1;
        if (home.Dashboard!.Notes.Count != 1 || home.Dashboard.MessageCount != 3) throw new InvalidOperationException("Home 친구 필터 오류");
        Render(home, Path.Combine(output,"home-friend.png"),360,1000);
        var panel = new PanelWindow(); Set(panel,"_store",store); Set(panel,"_dockedTo",new IntPtr(4321));
        var snapshot = new WatchSnapshot(true, []) { AccountCandidate="preview", Contexts=[new(new IntPtr(4321),Guid.NewGuid(),new(OnTalkSurface.Main,MainMenu.Friends)) { People=people }] };
        Set(panel,"_latestSnapshot",snapshot); Invoke(panel,"ApplyCurrentContext",snapshot); Invoke(panel,"UpdateFooter");
        ((TextBlock)panel.FindName("StatusText")).Text="가상 자료 · Home 화면 예시";
        Render((FrameworkElement)panel.Content,Path.Combine(output,"home-panel.png"),360,900);
        Render((FrameworkElement)panel.Content,Path.Combine(output,"home-panel-short.png"),360,480);
        Render((FrameworkElement)panel.Content,Path.Combine(output,"home-panel-150.png"),360,900,1.5);
        using var empty = new HomePane(store,()=>{}); empty.SetActive(true);
        Render(empty,Path.Combine(output,"home-empty.png"),360,600);
        // 대화 정리에서 실제 친구를 선택해 Home에 보관하고, 원문 선택을 바꾸면 저장 제안을 만료한다.
        using var analyzer = new OllamaAnalyzer(handler:new PreviewAiHandler()); using var writer = new OllamaWriter();
        var chat = new ChatSnapshot("Home 연결 검증",messages,now);
        using var pane = new ChatPane(store,analyzer,writer,Guid.NewGuid(),chat,()=>{});
        pane.SetActive(true); Invoke(pane,"SelectRecent"); Finish((Task)Call(pane,"AnalyzeAsync"));
        ((ComboBox)Get(pane,"_homeFriend")).SelectedIndex=0;
        if (!((Button)Get(pane,"_saveHome")).IsEnabled) throw new InvalidOperationException("Home 연결 단추 오류");
        ((Button)Get(pane,"_saveHome")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!store.GetHomeChats().Any(d=>d.Source.RoomHint==chat.RoomHint)) throw new InvalidOperationException("대화 Home 보관 오류");
        Invoke(pane,"SelectRecent");
        if (((Button)Get(pane,"_saveHome")).IsEnabled) throw new InvalidOperationException("선택 변경 후 오래된 Home 정리 연결");
        Set(panel,"_store",null!); panel.Close();
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

    private sealed class PreviewPersonalizedAutomation(IReadOnlyList<ContactPerson> people) : IPersonalizedAutomation
    {
        private ComposeState _state = new([], "", 0, 4000, false, false, default);
        private int _nextHwnd = 200;

        public (ActionStatus Status, IntPtr ComposeWindow) EnsureEmptyNewComposeWindow()
        {
            _state = new([], "", 0, 4000, false, false, default);
            return (ActionStatus.Ok, new IntPtr(++_nextHwnd));
        }

        public ComposeState? ReadCompose(IntPtr compose) => _state;

        public SearchOutcome SearchRecipient(IntPtr compose, string name)
        {
            var rows = people
                .Where(p => p.Name == name)
                .Select(p => new PersonRow(p.Name, p.Title, [string.Join(" / ", p.Organization)], new AccNode(AccNode.ListItem, p.Name, "", 0, [])))
                .ToList();
            return new SearchOutcome(ActionStatus.Ok, new IntPtr(901), rows);
        }

        public ActionStatus AddRecipient(IntPtr compose, IntPtr search, PersonRow person)
        {
            _state = _state with { Recipients = [.. _state.Recipients, person.Name] };
            return ActionStatus.Ok;
        }

        public void CloseUserSearchWindow(IntPtr search) { }

        public ApplyResult ApplyBody(IntPtr compose, string body, string? expectedCurrentBody)
        {
            var normalized = body.Replace("\r\n", "\n");
            _state = _state with { Body = normalized, UsedBytes = NoteBytes.Count(normalized) };
            return new ApplyResult(ApplyStatus.Applied, _state);
        }

        public ActionStatus SetReservation(IntPtr compose, DateTime when)
        {
            _state = _state with { Reservation = when };
            return ActionStatus.Ok;
        }

        public SendOutcome Send(IntPtr compose, ExpectedCompose expected) =>
            ComposeActions.Difference(_state, expected) is { } diff
                ? new SendOutcome(SendStatus.Mismatch, diff)
                : new SendOutcome(SendStatus.WindowClosed, "");
    }

    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static IEnumerable<Button> Buttons(object root)
    {
        if (root is Button button) yield return button;
        if (root is not DependencyObject node) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            foreach (var found in Buttons(child)) yield return found;
    }

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
