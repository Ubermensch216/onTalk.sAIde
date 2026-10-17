using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OnTalk.Sidecar.Core;

public sealed record PersonalizedDataRow(int Line, string Name, string Organization, string Title, IReadOnlyDictionary<string, string> Values)
{
    public string this[string header] => Values.GetValueOrDefault(header, "");
}

public sealed record PersonalizedImportResult(IReadOnlyList<string> Headers, IReadOnlyList<PersonalizedDataRow> Rows, IReadOnlyList<string> Errors)
{
    public bool Ready => Errors.Count == 0;
}

/// <summary>Reads only the first worksheet. File values are data and never executable formulas or templates.</summary>
public static class PersonalizedImport
{
    public const int MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxRows = 10_000;
    private const int MaxXmlBytes = 12 * 1024 * 1024;
    private static readonly string[] Required = NotePersonalization.IdentityFields;

    public static PersonalizedImportResult ReadFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return new([], [], ["파일을 찾지 못했습니다."]);
        if (file.Length > MaxFileBytes) return new([], [], ["파일이 5MB를 초과합니다."]);
        try
        {
            var extension = file.Extension.ToLowerInvariant();
            return extension switch
            {
                ".csv" => FromTable(ReadDelimited(ReadText(path), ',')),
                ".tsv" => FromTable(ReadDelimited(ReadText(path), '\t')),
                ".xlsx" => FromTable(ReadWorkbook(path)),
                _ => new([], [], ["CSV, TSV 또는 XLSX 파일을 선택하세요."])
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or DecoderFallbackException or ArgumentException)
        { return new([], [], ["파일을 읽지 못했습니다: " + ex.Message]); }
    }

    public static PersonalizedImportResult ReadCsv(string text) => FromTable(ReadDelimited(text, ','));

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
        }
    }

    public static IReadOnlyList<string> Match(PersonalizedWork work, PersonalizedImportResult imported) =>
        Match(work, imported.Rows, imported.Headers);

    public static IReadOnlyList<string> Match(PersonalizedWork work, IReadOnlyList<PersonalizedDataRow> rows, IEnumerable<string>? headers = null)
    {
        var errors = new List<string>();
        var planned = new Dictionary<string, PersonalizedDataRow>();
        if (work.Items.GroupBy(i => i.Person.DisplayIdentity).Any(g => g.Count() > 1))
            errors.Add("선택 대상에 이름·직급·소속이 같은 중복 행이 있습니다. 먼저 한 명만 남기세요.");
        foreach (var row in rows)
        {
            var matches = work.Items.Where(i => Same(i.Person, row)).ToList();
            if (matches.Count != 1) { errors.Add($"{row.Line}행: 선택 대상에서 일치하는 한 명을 찾지 못했습니다 ({row.Name}, {row.Organization}, {row.Title})."); continue; }
            if (!planned.TryAdd(matches[0].Id, row)) errors.Add($"{row.Line}행: {row.Name} 대상이 파일에 두 번 있습니다.");
        }
        foreach (var item in work.Items.Where(i => !planned.ContainsKey(i.Id))) errors.Add($"{item.Name} · {item.Organization}: 파일에 데이터가 없습니다.");
        if (errors.Count > 0) return errors;
        var customHeaders = (work.Headers.Count > 0 ? work.Headers : [PersonalizedWork.DefaultHeader])
            .Concat(headers ?? rows.SelectMany(r => r.Values.Keys))
            .Select(h => h.Trim())
            .Where(h => h.Length > 0 && !Required.Contains(h))
            .Distinct()
            .ToList();
        work.Headers = customHeaders;
        foreach (var item in work.Items.Where(i => !i.Attempted))
        {
            var row = planned[item.Id];
            item.Values.Clear();
            foreach (var h in customHeaders)
                item.Values[h] = row.Values.GetValueOrDefault(h, "");
        }
        return [];
    }

    public static string ExampleCsv(IEnumerable<ContactPerson> people, IEnumerable<string>? extraHeaders = null)
    {
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        static string QuoteHeader(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? Quote(value) : value;
        var custom = (extraHeaders ?? [PersonalizedWork.DefaultHeader])
            .Select(h => h.Trim())
            .Where(h => h.Length > 0 && !Required.Contains(h))
            .Distinct()
            .ToList();
        if (custom.Count == 0) custom.Add(PersonalizedWork.DefaultHeader);
        var headerLine = string.Join(",", Required.Concat(custom).Select(QuoteHeader));
        var lines = new List<string> { headerLine };
        lines.AddRange(people.Select(p => string.Join(",",
            new[] { p.Name, string.Join(" / ", p.Organization), p.Title }
                .Concat(custom.Select(_ => ""))
                .Select(Quote))));
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static bool Same(ContactPerson person, PersonalizedDataRow row) => person.Name == row.Name && person.Title == row.Title &&
        Segments(person.Organization).SequenceEqual(Segments([row.Organization]), StringComparer.OrdinalIgnoreCase);

    private static string[] Segments(IEnumerable<string> values) => values.SelectMany(v => v.Split(['/', '|', '·', '>'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();

    private static PersonalizedImportResult FromTable(IReadOnlyList<IReadOnlyList<string>> table)
    {
        if (table.Count == 0) return new([], [], ["파일에 표가 없습니다."]);
        var maxCols = table.Max(r => r.Count);
        var header = Enumerable.Range(0, maxCols)
            .Select(index => (Value: index < table[0].Count ? table[0][index].Trim().TrimStart('\uFEFF') : "", Index: index))
            .ToList();
        var errors = new List<string>();
        foreach (var field in Required)
        {
            var count = header.Count(h => h.Value == field);
            if (count != 1) errors.Add(count == 0 ? $"'{field}' 열이 없습니다." : $"'{field}' 열이 중복되었습니다.");
        }
        foreach (var duplicate in header.Where(h => h.Value.Length > 0 && !Required.Contains(h.Value)).GroupBy(h => h.Value).Where(g => g.Count() > 1))
            errors.Add($"'{duplicate.Key}' 열이 중복되었습니다.");
        foreach (var invalid in header.Where(h => h.Value.IndexOfAny(['{', '}', '\r', '\n']) >= 0))
            errors.Add($"'{invalid.Value}' 헤더에 사용할 수 없는 문자가 있습니다.");
        foreach (var emptyHeader in header.Where(h => h.Value.Length == 0))
        {
            if (table.Skip(1).Any(r => emptyHeader.Index < r.Count && !string.IsNullOrWhiteSpace(r[emptyHeader.Index])))
                errors.Add($"{emptyHeader.Index + 1}번째 열의 헤더 이름이 비어 있습니다.");
        }
        if (errors.Count > 0) return new([], [], errors);
        var customColumns = header.Where(h => h.Value.Length > 0 && !Required.Contains(h.Value)).ToList();
        var customHeaders = customColumns.Select(h => h.Value).ToList();
        int Column(string name) => header.SingleOrDefault(h => h.Value == name, (Value: "", Index: -1)).Index;
        var result = new List<PersonalizedDataRow>();
        for (var i = 1; i < table.Count; i++)
        {
            if (result.Count >= MaxRows) { errors.Add($"최대 {MaxRows}행까지만 가져올 수 있습니다."); break; }
            var cells = table[i];
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            string ValueAt(int col) => col >= 0 && col < cells.Count ? cells[col].Trim() : "";
            string Value(string name) => ValueAt(Column(name));
            var values = new Dictionary<string, string>();
            foreach (var col in customColumns)
                values[col.Value] = ValueAt(col.Index);
            var row = new PersonalizedDataRow(i + 1, Value("이름"), Value("소속"), Value("직급"), values);
            if (row.Name.Length == 0 || row.Organization.Length == 0 || row.Title.Length == 0)
                errors.Add($"{row.Line}행: 이름·소속·직급이 모두 필요합니다.");
            result.Add(row);
        }
        if (result.Count == 0) errors.Add("가져올 사람이 없습니다.");
        return new(customHeaders, result, errors);
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadDelimited(string text, char delimiter)
    {
        var rows = new List<IReadOnlyList<string>>();
        var cells = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (quoted) quoted = false;
                else if (cell.Length == 0) quoted = true;
                else throw new InvalidDataException("CSV 따옴표 형식이 잘못되었습니다.");
            }
            else if (ch == delimiter && !quoted) { cells.Add(cell.ToString()); cell.Clear(); }
            else if ((ch == '\n' || ch == '\r') && !quoted)
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                cells.Add(cell.ToString()); cell.Clear(); rows.Add(cells); cells = [];
                if (rows.Count > MaxRows + 1) throw new InvalidDataException("행 수가 한도를 초과합니다.");
            }
            else cell.Append(ch);
        }
        if (quoted) throw new InvalidDataException("CSV 따옴표가 닫히지 않았습니다.");
        if (cell.Length > 0 || cells.Count > 0) { cells.Add(cell.ToString()); rows.Add(cells); }
        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadWorkbook(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var shared = archive.GetEntry("xl/sharedStrings.xml") is { } strings
            ? LoadXml(strings).Descendants().Where(e => e.Name.LocalName == "si")
                .Select(e => string.Concat(e.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value))).ToList() : [];
        ZipArchiveEntry? entry = null;
        if (archive.GetEntry("xl/workbook.xml") is { } workbook)
        {
            var rels = archive.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("엑셀 시트 관계를 찾지 못했습니다.");
            var first = LoadXml(workbook).Descendants().FirstOrDefault(e => e.Name.LocalName == "sheet");
            var id = first?.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
            var target = LoadXml(rels).Descendants().FirstOrDefault(e => e.Name.LocalName == "Relationship" && (string?)e.Attribute("Id") == id)?.Attribute("Target")?.Value;
            if (target is null) throw new InvalidDataException("첫 번째 엑셀 시트를 찾지 못했습니다.");
            var relative = target.TrimStart('/').Replace('\\', '/');
            if (relative.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) relative = relative[3..];
            if (relative.Split('/').Any(part => part is ".." or ".")) throw new InvalidDataException("엑셀 시트 경로가 잘못되었습니다.");
            entry = archive.GetEntry("xl/" + relative) ?? throw new InvalidDataException("첫 번째 엑셀 시트가 없습니다.");
        }
        entry ??= archive.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (entry is null) throw new InvalidDataException("엑셀 파일에 시트가 없습니다.");
        var table = new List<IReadOnlyList<string>>();
        foreach (var row in LoadXml(entry).Descendants().Where(e => e.Name.LocalName == "row"))
        {
            if (table.Count > MaxRows) throw new InvalidDataException("행 수가 한도를 초과합니다.");
            var cells = new List<string>();
            foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? "";
                var letters = new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
                var index = 0; foreach (var letter in letters) index = index * 26 + (letter - 'A' + 1);
                if (index is < 1 or > 128) throw new InvalidDataException("지원하지 않는 열 위치입니다.");
                while (cells.Count < index) cells.Add("");
                var type = (string?)cell.Attribute("t");
                var raw = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                var value = type switch
                {
                    "s" when int.TryParse(raw, out var n) && n >= 0 && n < shared.Count => shared[n],
                    "inlineStr" => string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)),
                    _ => raw
                };
                cells[index - 1] = value;
            }
            table.Add(cells);
        }
        return table;
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxXmlBytes) throw new InvalidDataException("엑셀 시트가 너무 큽니다.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes });
        return XDocument.Load(reader);
    }
}
