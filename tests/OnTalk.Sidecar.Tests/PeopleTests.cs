using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;
using static OnTalk.Sidecar.Tests.NoteFixtures;

namespace OnTalk.Sidecar.Tests;

public class MainPeopleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, KoreanNoteTime.SeoulOffset);
    private static AccNode MainTree(string route, params AccNode[] children) => Document("file:///x/windows/renderer/app.html#" + route, children);
    private static AccNode Person(string key, string name = "예시 담당자", string title = "주무관", string organization = "예시시 | 총무과", int state = 0) =>
        new(AccNode.ListItem, "", "", state, [Text(name), Text(title), Text(organization), Text("온라인")]) { Path = key };
    private static AccNode Employee(string key, string name, int state = 0) => new("outlineitem", "", "", state,
        [Text(name), Text("주무관"), Text("오프라인")]) { Path = key };
    private static AccNode Department(string key, string name, params AccNode[] children) => new("outlineitem", name, "", 0,
        [Text(name), .. children]) { Path = key };

    [Fact]
    public void 조직도에서_체크한_직원과_부서를_선택으로_읽는다()
    {
        static AccNode Checked(string path, bool on) => new(AccNode.CheckBox, "", "", on ? AccNode.StateChecked : 0, []) { Path = path };
        var employeeA = Employee("a", "체크한 직원") with { Children = [Checked("a/c", true), Text("체크한 직원"), Text("주무관"), Text("오프라인")] };
        var employeeB = Employee("b", "체크 안 한 직원") with { Children = [Checked("b/c", false), Text("체크 안 한 직원"), Text("주무관"), Text("오프라인")] };
        var dept = Department("d", "회계과", Employee("c", "부서 직원")) with { Children = [Checked("d/c", true), Text("회계과"), Employee("c", "부서 직원")] };
        var snapshot = MainPeopleParser.Parse(MainTree("/organizations", Department("i", "예시기관", employeeA, employeeB, dept)), MainMenu.Organization, Now);
        Assert.Equal(new[] { "a", "c" }, snapshot.SelectedKeys.Order());
    }

    [Fact]
    public void 친구_목록에서_체크한_친구와_그룹을_선택으로_읽는다()
    {
        static AccNode Box(string path, bool on) => new("SPAN", "", "", 0, []) { Path = path, Class = on ? "ant-checkbox ant-checkbox-checked" : "ant-checkbox" };
        AccNode Friend(string key, string name, bool on) => Person(key, name) with { Children = [Box(key + "/c", on), Text(name), Text("주무관"), Text("예시시 | 총무과"), Text("온라인")] };
        var groupA = Div(Div(new AccNode(AccNode.CheckBox, "", "", 0, []) { Path = "ga" }, Text("그룹 A"), Text("2")),
            Div(Friend("a", "체크한 친구", true), Friend("b", "체크 안 한 친구", false)));
        var groupB = Div(Div(new AccNode(AccNode.CheckBox, "", "", AccNode.StateChecked, []) { Path = "gb" }, Text("그룹 B"), Text("1")),
            Div(Friend("c", "그룹으로 체크", false)));
        var snapshot = MainPeopleParser.Parse(MainTree("/main/1", groupA, groupB), MainMenu.Friends, Now);
        Assert.Equal(new[] { "a", "c" }, snapshot.SelectedKeys.Order());
        Assert.Equal(new[] { "a", "b" }, snapshot.Scopes.Single(s => s.Name == "그룹 A").MemberKeys);
        Assert.False(snapshot.Scopes.Single(s => s.Name == "그룹 A").Selected);
    }

    [Fact]
    public void 온톡_5_4_9_친구_목록은_평평한_구조와_체크_표시_요소로_선택을_읽는다()
    {
        static AccNode Header(string path, string name, int state) => Div(Div(new AccNode(AccNode.CheckBox, "", "", state, []) { Path = path }, Text(name), Text("2")));
        AccNode Friend(string key, string name, bool on)
        {
            var photo = new AccNode("DIV", "", "", 0, [new("SPAN", "", "", 0, [])]) { HasAction = true, Bounds = new(0, 0, 36, 36) };
            var list = new AccNode("list", "", "", 0, [Person(key, name)]);
            AccNode[] kids = on
                ? [new("DIV", "", "", 0, []) { HasAction = true, Bounds = new(0, 0, 50, 44) }, photo, list]
                : [photo, list];
            return new AccNode("DIV", "", "", 0, kids);
        }
        var root = MainTree("/main/1", Div(Header("g1", "그룹 A", 0x100000), Friend("a", "체크한 친구", true), Friend("b", "친구 B", false),
            Header("g2", "그룹 B", 0x100000), Friend("c", "친구 C", false), Friend("d", "체크한 친구 D", true)));
        var snapshot = MainPeopleParser.Parse(root, MainMenu.Friends, Now);
        Assert.Equal(new[] { "a", "d" }, snapshot.SelectedKeys.Order());
        Assert.Equal(new[] { "a", "b" }, snapshot.Scopes.Single(s => s.Name == "그룹 A").MemberKeys);
        Assert.Equal(new[] { "c", "d" }, snapshot.Scopes.Single(s => s.Name == "그룹 B").MemberKeys);
        Assert.All(snapshot.Scopes, s => Assert.False(s.Selected));
        var all = MainPeopleParser.Parse(MainTree("/main/1", Div(Header("g1", "그룹 A", 0x100000), Friend("a", "체크한 친구", true), Friend("b", "체크한 친구 B", true))), MainMenu.Friends, Now);
        Assert.True(Assert.Single(all.Scopes).Selected);
    }

    [Fact]
    public void 온톡의_대화방_생성_오류_안내를_알아본다()
    {
        Assert.True(FriendActions.HasErrorNotice(Div(Div(Text("대화방 생성 중에 오류가 발생합니다.")), Text("확인"))));
        Assert.True(FriendActions.HasErrorNotice(Div(Text("대화방 생성 중 에러가 발생했습니다.\n잠시 후 다시 시도해주세요."))));
        Assert.False(FriendActions.HasErrorNotice(Div(Text("친구"), Text("확인"))));
    }

    [Fact]
    public void 우클릭_메뉴에서_단체_대화와_단체_쪽지_항목을_찾는다()
    {
        static AccNode Item(string label, string path) => new("listitem", "", "", 0, [Text(label)]) { Path = path, Bounds = new(10, 10, 80, 20) };
        var menu = Div(Item("단체 대화", "m/0"), Item("단체 쪽지", "m/1"), Item("선택 취소", "m/2"));
        Assert.Equal("m/0", FriendActions.MenuItem(menu, ["단체 대화"])!.Path);
        Assert.Equal("m/1", FriendActions.MenuItem(menu, ["단체 쪽지", "단체쪽지"])!.Path);
        Assert.Null(FriendActions.MenuItem(Div(Item("영상회의", "m/3") with { Bounds = default }), ["영상회의"]));

        // 한 명만 체크하면 메뉴 이름에 '단체'가 붙지 않는다(온톡 5.4.9: 대화·쪽지·영상회의·프로필 보기·친구 삭제).
        var single = Div(Item("대화", "s/0"), Item("쪽지", "s/1"), Item("영상회의", "s/2"));
        Assert.Equal("s/0", FriendActions.MenuItem(single, ["단체 대화", "단체대화", "대화"])!.Path);
        Assert.Equal("s/1", FriendActions.MenuItem(single, ["단체 쪽지", "단체쪽지", "쪽지"])!.Path);
        // 단체 메뉴가 있으면 같은 글자가 들어 있어도 '단체' 이름을 먼저 고른다.
        Assert.Equal("m/0", FriendActions.MenuItem(Div(Item("대화", "x/0"), menu), ["단체 대화", "대화"])!.Path);
    }

    [Fact]
    public void 실제_조직도처럼_확인란_CSS_클래스로만_체크를_알려도_선택으로_읽는다()
    {
        // 온톡 5.4.9: 확인란은 SPAN이며 MSAA 상태는 체크와 무관하고 class에 ant-tree-checkbox-checked가 붙는다.
        static AccNode Box(string path, bool on) => new("SPAN", "", "", 0, [new("table", "", "", 0x10000, [])]) { Path = path, Class = on ? "ant-tree-checkbox ant-tree-checkbox-checked" : "ant-tree-checkbox" };
        AccNode Row(string key, string name, bool on) => new("outlineitem", name, "", 0x200000, [Box(key + "/c", on), Text(name), Text("지방행정주사/주무관"), Text("오프라인")]) { Path = key };
        var dept = new AccNode("outlineitem", "icon: caret-down 감사담당관", "", 0x200000, [Box("d/c", false), Text("감사담당관"), Row("a", "직원 A", true), Row("b", "직원 B", false)]) { Path = "d" };
        var snapshot = MainPeopleParser.Parse(MainTree("/organizations", dept), MainMenu.Organization, Now);
        Assert.Equal("a", Assert.Single(snapshot.SelectedKeys));
        Assert.Equal("직원 A", snapshot.People.Single(p => p.Key == "a").Name);
    }

    [Theory]
    [InlineData("display:block;tag:span;class:ant-tree-checkbox ant-tree-checkbox-checked;", "ant-tree-checkbox ant-tree-checkbox-checked")]
    [InlineData(@"display:table;tag:<pseudo\:after>;layout-guess:true;", "")]
    [InlineData(@"class:a\;b;tag:span", "a;b")]
    public void IA2_특성에서_class만_꺼낸다(string attributes, string expected) => Assert.Equal(expected, MsaaReader.ParseClass(attributes));

    [Theory]
    [InlineData("/main/1", MainMenu.Friends)]
    [InlineData("/organizations", MainMenu.Organization)]
    [InlineData("/organizations/?q=abc", MainMenu.Organization)]
    public void 실제_관찰한_해시_경로로_메뉴를_확정한다(string route, MainMenu menu) => Assert.Equal(menu, WindowContextParser.Parse(MainTree(route)).Menu);

    [Fact]
    public void 오래된_해시가_남아도_로그인_화면을_읽지_않는다() => Assert.Equal(OnTalkSurface.Login,
        WindowContextParser.Parse(MainTree("/main/1", Text("로그인"), Text("아이디 저장"))).Surface);

    [Fact]
    public void 친구_목록에서_자기_프로필과_숨긴_사람은_포함하지_않는다()
    {
        var root = MainTree("/main/1", new AccNode(AccNode.ListItem, "", "", 0, [Text("자기 이름"), Text("주무관")]),
            Person("1", "사람 A"), Person("2", "숨긴 사람", state: 0x8000),
            new AccNode("tooltip", "", "", 0x8000, [Text("사람 A"), Text("주무관"), Text("담당업무"), Text("숨겨진 업무")]));
        var snapshot = MainPeopleParser.Parse(root, MainMenu.Friends, Now);
        Assert.Equal("사람 A", Assert.Single(snapshot.People).Name);
        Assert.Empty(snapshot.People[0].Duty);
        Assert.Empty(snapshot.SelectedKeys);
    }

    [Fact]
    public void 선택_가능과_실제_선택은_다르다()
    {
        var snapshot = MainPeopleParser.Parse(MainTree("/main/1", Person("A", state: 0x200000), Person("B", state: AccNode.StateSelected)), MainMenu.Friends, Now);
        Assert.Equal("B", Assert.Single(snapshot.SelectedKeys));
    }

    [Fact]
    public void 그룹_체크는_그_그룹에서_읽은_사람만_연결한다()
    {
        var first = Div(Div(new AccNode(AccNode.CheckBox, "", "", AccNode.StateChecked, []) { Path = "c1" }, Text("그룹 A"), Text("5")), Div(Person("A")));
        var second = Div(Div(new AccNode(AccNode.CheckBox, "", "", 0, []) { Path = "c2" }, Text("그룹 B"), Text("1")), Div(Person("B")));
        var snapshot = MainPeopleParser.Parse(MainTree("/main/1", first, second), MainMenu.Friends, Now);
        Assert.Equal(2, snapshot.Scopes.Count);
        var scope = snapshot.Scopes.Single(s => s.Name == "그룹 A");
        Assert.Equal(5, scope.DeclaredCount); Assert.Equal("A", Assert.Single(scope.MemberKeys));
        Assert.Equal("A", Assert.Single(snapshot.SelectedKeys));
        Assert.DoesNotContain("B", scope.MemberKeys);
    }

    [Fact]
    public void 접힌_그룹을_전체_구성원으로_추정하지_않는다()
    {
        var group = Div(new AccNode(AccNode.CheckBox, "", "", AccNode.StateChecked, []) { Path = "g" }, Text("접힌 그룹"), Text("8"));
        var snapshot = MainPeopleParser.Parse(MainTree("/main/1", group), MainMenu.Friends, Now);
        Assert.Empty(Assert.Single(snapshot.Scopes).MemberKeys); Assert.Empty(snapshot.SelectedKeys);
    }

    [Fact]
    public void 조직도_부서_경로와_그_아래_관찰한_구성원만_연결한다()
    {
        var dept = Department("D", "총무과", Employee("A", "직원 A")) with { State = AccNode.StateSelected };
        var root = MainTree("/organizations", Department("I", "예시기관", dept, Department("E", "회계과", Employee("B", "직원 B"))));
        var snapshot = MainPeopleParser.Parse(root, MainMenu.Organization, Now);
        Assert.Equal(new[] { "예시기관", "총무과" }, snapshot.People.Single(p => p.Key == "A").Organization);
        Assert.Equal("A", Assert.Single(snapshot.SelectedKeys));
        Assert.Equal("A", Assert.Single(snapshot.Scopes.Single(s => s.Key == "D").MemberKeys));
    }

    [Fact]
    public void 읽기_한도에_도달하면_일부_목록으로_표시한다()
    {
        var root = MainTree("/main/1", Person("A")) with { IsTruncated = true };
        Assert.Contains("일부", MainPeopleParser.Parse(root, MainMenu.Friends, Now).Coverage);
    }

    [Fact]
    public void 상세_담당업무를_유일한_표시_행에_연결하지만_선택으로_취급하지_않는다()
    {
        var tip = new AccNode("tooltip", "", "", 0, [Text("예시 담당자"), Text("주무관"), Text("직장전화"), Text("000-000-0000"), Text("담당업무"), Text("여비 정산")]);
        var snapshot = MainPeopleParser.Parse(MainTree("/main/1", Person("A"), tip), MainMenu.Friends, Now);
        var person = Assert.Single(snapshot.People);
        Assert.Equal("여비 정산", person.Duty); Assert.Equal(Now, person.DetailsObservedAt); Assert.Empty(snapshot.SelectedKeys);
        var duplicate = MainPeopleParser.Parse(MainTree("/main/1", Person("A"), Person("B"), tip), MainMenu.Friends, Now);
        Assert.All(duplicate.People, p => Assert.Empty(p.Duty));
        Assert.Contains("합치지", ContactSupport.IdentityWarning(duplicate.People));
    }

    [Fact]
    public void 사용자검색이나_미확인_메뉴에서_사람을_수집하지_않는다()
    {
        var root = Document("file:///x/windows/userSearch/app.html", Person("A"));
        Assert.Empty(MainPeopleParser.Parse(root, MainMenu.Friends, Now).People);
        Assert.Empty(MainPeopleParser.Parse(MainTree("/unknown", Person("A")), MainMenu.Friends, Now).People);
    }

    [Fact]
    public void 온라인_상태나_상세_변화는_선택을_유지하고_사람이_교체되면_유지하지_않는다()
    {
        var person = new ContactPerson("A", "담당자", "주무관", ["총무과"]);
        Assert.Equal(person.SelectionKey, (person with { Status = "온라인", Duty = "여비" }).SelectionKey);
        Assert.NotEqual(person.SelectionKey, (person with { Name = "다른 사람" }).SelectionKey);
        Assert.NotEqual(person.SelectionKey, (person with { Organization = ["회계과"] }).SelectionKey);
    }
}

public sealed class ContactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ontalk-saide-tests", Guid.NewGuid().ToString("N"));
    [Fact]
    public void 버전6_자료를_보존하고_백업을_남긴_뒤_저장소_구조를_올린다()
    {
        var at = DateTimeOffset.Now;
        using (var store = ProfileStore.Open(_root, "migration"))
        {
            store.SaveReceived(new ReceivedNote("예시 발신자", "총무과", "오늘", null, 1, "이전 자료", []), at);
            store.SaveGroup("검증된 기존 그룹", [new Recipient("담당자", "주무관", ["총무과"]) ]);
            // 합성 DB를 실제 이전 스키마 상태로 만든다. 운영 계정의 저장소는 사용하지 않는다.
            var db = (Microsoft.Data.Sqlite.SqliteConnection)typeof(ProfileStore).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE personalized_work; DROP TABLE home_chat_contact; DROP TABLE home_chat; DROP TABLE home_people; DROP TABLE contact_preparation_group; DROP TABLE task_chat_source; DROP TABLE contact_note_link; PRAGMA user_version=6;";
            cmd.ExecuteNonQuery();
        }
        using var reopened = ProfileStore.Open(_root, "migration");
        Assert.Equal(1, reopened.CountNotes()); Assert.Single(reopened.GetGroups()); Assert.Empty(reopened.GetPersonalizedWorks());
        Assert.True(File.Exists(Path.Combine(_root, "migration", "notes.db.v6.bak")));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
