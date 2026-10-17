using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

public sealed class PersonalizedHeaderTests
{
    private static PersonalizedWork Work() => new()
    {
        Template = "{이름}님 {항목}",
        Items = [new() { Person = new("r", "김예시", "주무관", ["예시시"]), Values = new() { ["항목"] = "A" } }]
    };

    [Fact]
    public void 서식에_빠진_항목을_찾는다()
    {
        Assert.Empty(NotePersonalization.MissingFields("{ 항목 } {장소}", ["항목", "장소"]));
        Assert.Equal(["장소"], NotePersonalization.MissingFields("{항목}", ["항목", "장소"]));
    }

    [Fact]
    public void 항목을_추가하면_모든_사람에게_빈값이_생기고_서식에_없으면_빠진것으로_본다()
    {
        var work = Work();
        Assert.Equal("항목2", work.NextHeaderName());
        Assert.Null(work.AddHeader("장소"));
        Assert.Equal(["항목", "장소"], work.Headers);
        Assert.Equal("", work.Items[0].Values["장소"]);
        Assert.Equal(["장소"], work.MissingInTemplate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{장소}")]
    [InlineData("이름")]
    [InlineData("항목")]
    public void 잘못된_항목_제목은_거부한다(string name) =>
        Assert.NotNull(Work().AddHeader(name));

    [Fact]
    public void 제목을_바꾸면_사람별_값과_서식_변수가_따라간다()
    {
        var work = Work();
        Assert.Null(work.RenameHeader("항목", "교육안내"));
        work.Template = NotePersonalization.RenameVariable(work.Template, "항목", "교육안내");
        Assert.Equal(["교육안내"], work.Headers);
        Assert.Equal("A", work.Items[0].Values["교육안내"]);
        Assert.False(work.Items[0].Values.ContainsKey("항목"));
        Assert.Equal("{이름}님 {교육안내}", work.Template);
        Assert.Empty(work.MissingInTemplate());
        Assert.Equal("김예시님 A", NotePersonalization.Render(work.Items[0].Person, work.Template, work.Items[0].Values).Body);
    }

    [Fact]
    public void 다른_항목과_같은_제목으로는_바꾸지_못한다()
    {
        var work = Work();
        work.AddHeader("장소");
        Assert.NotNull(work.RenameHeader("장소", "항목"));
        Assert.Null(work.RenameHeader("장소", "장소"));
    }

    [Fact]
    public void 삭제하면_값과_서식_변수가_사라지고_마지막_하나는_지울수_없다()
    {
        var work = Work();
        work.AddHeader("장소");
        Assert.Null(work.RemoveHeader("장소"));
        Assert.Equal(["항목"], work.Headers);
        Assert.False(work.Items[0].Values.ContainsKey("장소"));
        Assert.NotNull(work.RemoveHeader("항목"));
        Assert.Equal("{이름}님 ", NotePersonalization.RemoveVariable("{이름}님 {항목}", "항목"));
    }

    [Fact]
    public void 가져오기는_바꾼_항목_제목을_되살리지_않는다()
    {
        var work = Work();
        work.RenameHeader("항목", "교육안내");
        var imported = PersonalizedImport.ReadCsv("이름,소속,직급,장소\r\n김예시,예시시,주무관,대강당\r\n");
        Assert.Empty(PersonalizedImport.Match(work, imported));
        Assert.Equal(["교육안내", "장소"], work.Headers);
    }

    [Fact]
    public void 임시_저장_표시는_사용자가_저장했고_발송을_시도하지_않은_작업에만_붙는다()
    {
        var work = Work();
        Assert.False(work.IsDraft); Assert.True(work.IsUnmarked);
        Assert.Equal("(제목 없음) {이름}님 {항목}", work.Headline);
        Assert.Equal("발송 대상 1명", work.Detail);
        work.TemplateName = "교육 일정 안내";
        Assert.Equal("교육 일정 안내", work.Headline);
        work.Draft = true;
        Assert.True(work.IsDraft); Assert.False(work.IsUnmarked);
        work.Items[0].DispatchId = 7;
        Assert.False(work.IsDraft); Assert.False(work.IsUnmarked);
        Assert.Equal("발송 대상 1명 · 발송 시도 1명", work.Detail);
    }

    [Fact]
    public void 임시_저장_표시는_저장소를_거쳐도_유지된다()
    {
        var root = Path.Combine(Path.GetTempPath(), "ontalk-draft", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = OnTalk.Sidecar.Storage.ProfileStore.Open(root, "a");
            var draft = Work(); draft.Draft = true;
            store.SavePersonalizedWork(draft); store.SavePersonalizedWork(Work());
            var saved = store.GetPersonalizedWorks();
            Assert.Single(saved, w => w.IsDraft);
            Assert.Single(saved, w => w.IsUnmarked);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void 모든_대상의_발송이_끝나면_발송_완료_일부만_끝나면_일부_발송으로_본다()
    {
        var work = Work();
        work.Items.Add(new() { Person = new("r2", "이예시", "주무관", ["예시시"]) });
        Assert.False(work.IsSent); Assert.False(work.IsPartlySent);
        work.Items[0].DispatchId = 1; work.Items[0].Status = "발송 완료 · 작성창 닫힘";
        Assert.False(work.IsSent); Assert.True(work.IsPartlySent);
        work.Items[1].DispatchId = 2; work.Items[1].Status = "결과 불명 · 직접 확인 필요";
        Assert.False(work.IsSent); Assert.True(work.IsPartlySent);
        work.Items[1].Status = "예약 완료(10-20 09:00) · 작성창 닫힘";
        Assert.True(work.IsSent); Assert.False(work.IsPartlySent); Assert.False(work.IsDraft);
    }

    [Fact]
    public void 서식을_불러오면_서식에_없는_빈_항목은_지우고_값이_있는_항목은_남긴다()
    {
        var work = Work();                      // 항목(값 A) 
        work.Items[0].Values["항목"] = "";       // 비어 있는 기본 항목
        var (added, removed) = work.AlignHeadersTo("{이름}님 {평정순위}");
        Assert.Equal(["평정순위"], added);
        Assert.Equal(["항목"], removed);
        Assert.Equal(["평정순위"], work.Headers);
        Assert.False(work.Items[0].Values.ContainsKey("항목"));
        Assert.Empty(NotePersonalization.MissingFields("{이름}님 {평정순위}", work.Headers));

        var keep = Work();                      // 항목에 입력한 값 A가 있으면 데이터 보호를 위해 남긴다
        var (_, none) = keep.AlignHeadersTo("{이름}님 {평정순위}");
        Assert.Empty(none);
        Assert.Equal(["항목", "평정순위"], keep.Headers);
    }

    [Fact]
    public void 서식에_변수가_없어도_항목은_하나_남는다()
    {
        var work = Work(); work.Items[0].Values["항목"] = "";
        work.AlignHeadersTo("변수 없는 서식");
        Assert.Equal(["항목"], work.Headers);
    }
}
