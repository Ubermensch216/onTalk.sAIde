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
    public void 후보는_확인한_글자와_근거가_일치할_때만_찾는다()
    {
        var person = new ContactPerson("A", "담당자", "주무관", ["총무과"], Duty: "출장 여비 정산");
        var match = Assert.Single(ContactSupport.Find([person], "총무과 여비"));
        Assert.Contains("담당업무 글자 일치", match.Evidence); Assert.Contains("소속 글자 일치", match.Evidence);
        Assert.Empty(ContactSupport.Find([person with { Duty = "" }], "여비"));
        Assert.Empty(ContactSupport.Find([person], "인사"));
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
    public void 버전6_자료를_보존하고_백업을_남긴_뒤_연락그룹_구조를_추가한다()
    {
        var at = DateTimeOffset.Now;
        using (var store = ProfileStore.Open(_root, "migration"))
        {
            store.SaveReceived(new ReceivedNote("예시 발신자", "총무과", "오늘", null, 1, "이전 자료", []), at);
            store.SaveGroup("검증된 기존 그룹", [new Recipient("담당자", "주무관", ["총무과"]) ]);
            // 합성 DB를 실제 이전 스키마 상태로 만든다. 운영 계정의 저장소는 사용하지 않는다.
            var db = (Microsoft.Data.Sqlite.SqliteConnection)typeof(ProfileStore).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE home_chat_contact; DROP TABLE home_chat; DROP TABLE home_people; DROP TABLE contact_preparation_group; DROP TABLE task_chat_source; DROP TABLE contact_note_link; PRAGMA user_version=6;";
            cmd.ExecuteNonQuery();
        }
        using var reopened = ProfileStore.Open(_root, "migration");
        Assert.Equal(1, reopened.CountNotes()); Assert.Single(reopened.GetGroups()); Assert.Empty(reopened.GetContactGroups());
        Assert.True(File.Exists(Path.Combine(_root, "migration", "notes.db.v6.bak")));
        reopened.SaveContactGroup("새 연락 후보", new ContactPreparation("문의", [new ContactPerson("1", "예시", "주무관", ["총무과"])], "합성", at), at);
        Assert.Single(reopened.GetContactGroups());
    }
    [Fact]
    public void 연락_후보_그룹은_재시작_후_복원되고_기존_수신자_그룹과_계정에_격리된다()
    {
        var at = DateTimeOffset.Now;
        var person = new ContactPerson("screen:1", "예시 담당자", "주무관", ["총무과"], Duty: "여비 정산") { DetailsObservedAt = at };
        var prep = new ContactPreparation("문의", [person], "사이드카 직접 선택", at);
        using (var store = ProfileStore.Open(_root, "A"))
        {
            store.SaveContactGroup("연락 준비", prep, at);
            Assert.Empty(store.GetGroups());
            store.SaveContactGroup("연락 준비", prep with { People = [person with { Name = "바뀐 후보" }] }, at.AddMinutes(1));
        }
        using var reopened = ProfileStore.Open(_root, "A");
        var group = Assert.Single(reopened.GetContactGroups());
        Assert.Equal("바뀐 후보", Assert.Single(group.People).Name); Assert.Equal(at, group.People[0].DetailsObservedAt);
        using var other = ProfileStore.Open(_root, "B"); Assert.Empty(other.GetContactGroups());
        reopened.DeleteContactGroup(group.Id); Assert.Empty(reopened.GetContactGroups());
    }

    [Fact]
    public void 보관_후보는_본문의_이름_언급이_아닌_정확한_발신자_이름으로_찾는다()
    {
        using var store = ProfileStore.Open(_root, "A"); var at = DateTimeOffset.Now;
        store.SaveReceived(new ReceivedNote("예시 담당자", "다른 부서", "오늘", null, 1, "이름이 같아도 신원 미확인", []), at);
        store.SaveReceived(new ReceivedNote("다른 발신자", "총무과", "오늘", null, 1, "예시 담당자를 언급", []), at);
        Assert.Equal("다른 부서", Assert.Single(store.GetContactNoteCandidates("예시 담당자")).Note.SenderDetail);
        Assert.Empty(store.GetContactNoteCandidates("예시"));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
