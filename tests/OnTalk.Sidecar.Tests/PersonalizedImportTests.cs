using System.IO.Compression;
using System.Text;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

public sealed class PersonalizedImportTests
{
    private static PersonalizedWork Work() => new() { Items =
    [
        new() { Person = new("a", "김예시", "주무관", ["예시시", "총무과"]) },
        new() { Person = new("b", "김예시", "주무관", ["예시시", "회계과"]) }
    ] };

    [Fact]
    public void 사용자가_추가한_임의의_CSV_헤더와_따옴표_줄바꿈을_읽고_본문에_반영한다()
    {
        const string csv = "이름,소속,직급,교육과정,참석일시,비고\r\n" +
            "김예시,예시시 / 총무과,주무관,정보보안 과정,10월 22일 14:00,\"첫째 줄\n둘째 줄\"\r\n" +
            "김예시,예시시 / 회계과,주무관,개인정보 과정,10월 23일 10:00,\"자료 \"\"확인\"\"\"\r\n";
        var imported = PersonalizedImport.ReadCsv(csv);
        Assert.True(imported.Ready);
        Assert.Equal(["교육과정", "참석일시", "비고"], imported.Headers);
        var work = Work(); Assert.Empty(PersonalizedImport.Match(work, imported));
        Assert.Equal(["항목", "교육과정", "참석일시", "비고"], work.Headers);
        Assert.Equal("정보보안 과정", work.Items[0]["교육과정"]);
        Assert.Equal("첫째 줄\n둘째 줄", work.Items[0]["비고"]);
        Assert.Equal("개인정보 과정", work.Items[1]["교육과정"]);
        Assert.Equal("자료 \"확인\"", work.Items[1]["비고"]);

        var rendered = NotePersonalization.Render(work.Items[0].Person, "{소속} {이름}({직급})님, [{교육과정}] {참석일시} 안내: {비고}", work.Items[0].Values);
        Assert.True(rendered.Ready);
        Assert.Equal("예시시 · 총무과 김예시(주무관)님, [정보보안 과정] 10월 22일 14:00 안내: 첫째 줄\n둘째 줄", rendered.Body);
    }

    [Fact]
    public void 누락과_중복이_있으면_일부도_반영하지_않는다()
    {
        var work = Work(); work.Items[0]["안내"] = "기존";
        var rows = new[]
        {
            new PersonalizedDataRow(2,"김예시","예시시 / 총무과","주무관",new Dictionary<string, string> { ["안내"] = "새값1" }),
            new PersonalizedDataRow(3,"김예시","예시시 / 총무과","주무관",new Dictionary<string, string> { ["안내"] = "새값2" })
        };
        var errors = PersonalizedImport.Match(work, rows, ["안내"]);
        Assert.Contains(errors, e => e.Contains("두 번"));
        Assert.Contains(errors, e => e.Contains("데이터가 없습니다"));
        Assert.Equal("기존", work.Items[0]["안내"]);
        Assert.Equal("", work.Items[1]["안내"]);
    }

    [Fact]
    public void 발송을_시도한_사람의_데이터는_다시_가져오지_않는다()
    {
        var work = Work(); work.Items[0]["지급액"] = "100만원"; work.Items[0].DispatchId = 12;
        var rows = new[]
        {
            new PersonalizedDataRow(2,"김예시","예시시 / 총무과","주무관",new Dictionary<string, string> { ["지급액"] = "10만원" }),
            new PersonalizedDataRow(3,"김예시","예시시 / 회계과","주무관",new Dictionary<string, string> { ["지급액"] = "80만원" })
        };
        Assert.Empty(PersonalizedImport.Match(work, rows, ["지급액"]));
        Assert.Equal("100만원", work.Items[0]["지급액"]); Assert.Equal("80만원", work.Items[1]["지급액"]);
    }

    [Fact]
    public void 내보낸_양식에_사용자가_헤더와_값을_추가해_다시_읽는다()
    {
        var work = Work();
        var exported = PersonalizedImport.ExampleCsv(work.Items.Select(i => i.Person));
        Assert.StartsWith("이름,소속,직급,항목\r\n", exported);
        var withAddedHeaders = "이름,소속,직급,항목,장소,일정\r\n" +
            "\"김예시\",\"예시시 / 총무과\",\"주무관\",\"필참\",\"대회의실\",\"14:00\"\r\n" +
            "\"김예시\",\"예시시 / 회계과\",\"주무관\",\"필참\",\"소회의실\",\"15:30\"\r\n";
        var imported = PersonalizedImport.ReadCsv(withAddedHeaders);
        Assert.True(imported.Ready);
        Assert.Empty(PersonalizedImport.Match(work, imported));
        Assert.Equal(["항목", "장소", "일정"], work.Headers);
        Assert.Equal("대회의실", work.Items[0]["장소"]);
        Assert.Equal("15:30", work.Items[1]["일정"]);
    }

    [Fact]
    public void XLSX_공유문자열과_숫자를_임의_헤더로_읽는다()
    {
        var path = Path.Combine(Path.GetTempPath(), "ontalk-personalized-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("xl/sharedStrings.xml").Open()))
                    writer.Write("<sst><si><t>이름</t></si><si><t>소속</t></si><si><t>직급</t></si><si><t>배정점수</t></si><si><t>김예시</t></si><si><t>예시시 / 총무과</t></si><si><t>주무관</t></si><si><t>예시시 / 회계과</t></si></sst>");
                using (var writer = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open()))
                    writer.Write("<worksheet><sheetData>" +
                        "<row><c r='A1' t='s'><v>0</v></c><c r='B1' t='s'><v>1</v></c><c r='C1' t='s'><v>2</v></c><c r='D1' t='s'><v>3</v></c></row>" +
                        "<row><c r='A2' t='s'><v>4</v></c><c r='B2' t='s'><v>5</v></c><c r='C2' t='s'><v>6</v></c><c r='D2'><v>95</v></c></row>" +
                        "<row><c r='A3' t='s'><v>4</v></c><c r='B3' t='s'><v>7</v></c><c r='C3' t='s'><v>6</v></c><c r='D3'><v>82</v></c></row>" +
                        "</sheetData></worksheet>");
            }
            var imported = PersonalizedImport.ReadFile(path);
            Assert.True(imported.Ready, string.Join("; ", imported.Errors));
            var work = Work(); Assert.Empty(PersonalizedImport.Match(work, imported));
            Assert.Equal(["항목", "배정점수"], work.Headers);
            Assert.Equal("95", work.Items[0]["배정점수"]); Assert.Equal("82", work.Items[1]["배정점수"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CP949_CSV도_한글_헤더와_값을_읽는다()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(Path.GetTempPath(), "ontalk-personalized-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            File.WriteAllBytes(path, Encoding.GetEncoding(949).GetBytes("이름,소속,직급,담당업무\r\n김예시,예시시 / 총무과,주무관,예산편성\r\n김예시,예시시 / 회계과,주무관,지출결의\r\n"));
            var imported = PersonalizedImport.ReadFile(path);
            Assert.True(imported.Ready, string.Join("; ", imported.Errors));
            var work = Work();
            Assert.Empty(PersonalizedImport.Match(work, imported));
            Assert.Equal("예산편성", work.Items[0]["담당업무"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 엑셀의_첫_탭이_sheet2이면_그_탭을_읽는다()
    {
        var path = Path.Combine(Path.GetTempPath(), "ontalk-personalized-" + Guid.NewGuid().ToString("N") + ".xlsx");
        static void Write(ZipArchive archive, string name, string value)
        { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(value); }
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(archive, "xl/workbook.xml", "<workbook xmlns:r='urn:test'><sheets><sheet name='안내' r:id='r2'/><sheet name='기타' r:id='r1'/></sheets></workbook>");
                Write(archive, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='r2' Target='worksheets/sheet2.xml'/><Relationship Id='r1' Target='worksheets/sheet1.xml'/></Relationships>");
                Write(archive, "xl/worksheets/sheet1.xml", "<worksheet><sheetData><row><c r='A1' t='inlineStr'><is><t>잘못된 탭</t></is></c></row></sheetData></worksheet>");
                Write(archive, "xl/worksheets/sheet2.xml", "<worksheet><sheetData>" +
                    "<row><c r='A1' t='inlineStr'><is><t>이름</t></is></c><c r='B1' t='inlineStr'><is><t>소속</t></is></c><c r='C1' t='inlineStr'><is><t>직급</t></is></c><c r='D1' t='inlineStr'><is><t>안내항목</t></is></c></row>" +
                    "<row><c r='A2' t='inlineStr'><is><t>김예시</t></is></c><c r='B2' t='inlineStr'><is><t>예시시 / 총무과</t></is></c><c r='C2' t='inlineStr'><is><t>주무관</t></is></c><c r='D2'><v>95</v></c></row>" +
                    "<row><c r='A3' t='inlineStr'><is><t>김예시</t></is></c><c r='B3' t='inlineStr'><is><t>예시시 / 회계과</t></is></c><c r='C3' t='inlineStr'><is><t>주무관</t></is></c><c r='D3'><v>82</v></c></row>" +
                    "</sheetData></worksheet>");
            }
            var imported = PersonalizedImport.ReadFile(path);
            Assert.True(imported.Ready, string.Join("; ", imported.Errors));
            Assert.Empty(PersonalizedImport.Match(Work(), imported));
        }
        finally { File.Delete(path); }
    }
}
