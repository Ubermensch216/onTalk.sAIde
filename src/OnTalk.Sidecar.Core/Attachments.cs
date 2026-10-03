using System.IO.Compression;
using System.Text;
using System.Xml;

namespace OnTalk.Sidecar.Core;

public enum ExtractStatus
{
    /// <summary>내용을 글자로 뽑아 검색할 수 있다.</summary>
    Extracted,
    /// <summary>이 형식은 내용을 뽑지 않는다. 파일 보관에는 지장이 없다.</summary>
    Unsupported,
    /// <summary>뽑다가 실패했다(손상, 암호, 크기 초과). 파일 보관에는 지장이 없다.</summary>
    Failed,
}

/// <param name="Sha256">확보한 파일 내용의 해시. 아직 확보하지 않았으면 null.</param>
public sealed record AttachmentInfo(
    long Id,
    long NoteId,
    string Name,
    string? Sha256,
    long? Size,
    DateTimeOffset? StoredAt,
    ExtractStatus? Extract);

/// <summary>저장해 두고 한꺼번에 수신자로 넣는 사람 묶음. 구성원은 넣을 때 본 소속·부서로 구분한다.</summary>
public sealed record RecipientGroup(long Id, string Name, IReadOnlyList<Recipient> Members);

/// <summary>
/// 첨부파일에서 검색용 글자를 뽑는다. 파일을 실행하거나 매크로를 돌리지 않고, 형식을 직접 읽는다.
/// 지원: 글자 파일(txt·csv·md·log), docx·xlsx·pptx. PDF와 HWP는 뽑지 않는다.
/// </summary>
public static class AttachmentText
{
    private const long MaxInputBytes = 30L * 1024 * 1024;
    private const long MaxEntryBytes = 60L * 1024 * 1024;
    private const int MaxChars = 1_000_000;

    public static (ExtractStatus Status, string Text) Extract(string fileName, byte[] data)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            if (data.LongLength > MaxInputBytes)
            {
                return (extension is ".txt" or ".csv" or ".md" or ".log" or ".docx" or ".xlsx" or ".pptx"
                    ? ExtractStatus.Failed
                    : ExtractStatus.Unsupported, "");
            }
            return extension switch
            {
                ".txt" or ".csv" or ".md" or ".log" => (ExtractStatus.Extracted, Clip(DecodeText(data))),
                ".docx" => Office(data, e => e == "word/document.xml", paragraph: "p"),
                ".pptx" => Office(data, e => e.StartsWith("ppt/slides/slide", StringComparison.Ordinal), paragraph: "p"),
                ".xlsx" => Office(data, e => e == "xl/sharedStrings.xml", paragraph: "si"),
                _ => (ExtractStatus.Unsupported, ""),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or DecoderFallbackException)
        {
            return (ExtractStatus.Failed, "");
        }
    }

    // UTF-8로 읽히지 않으면 한국어 윈도우 기본 인코딩(CP949)으로 읽는다.
    private static string DecodeText(byte[] data)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(data);
        }
    }

    private static (ExtractStatus, string) Office(byte[] data, Func<string, bool> wanted, string paragraph)
    {
        var text = new StringBuilder();
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries.Where(e => wanted(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            // 압축을 풀면 터무니없이 커지는 파일은 읽지 않는다.
            if (entry.Length > MaxEntryBytes)
            {
                return (ExtractStatus.Failed, "");
            }
            using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read() && text.Length < MaxChars)
            {
                if (reader.NodeType == XmlNodeType.Text && reader.Depth > 0)
                {
                    text.Append(reader.Value);
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == paragraph)
                {
                    text.Append('\n');
                }
            }
        }
        return (ExtractStatus.Extracted, Clip(text.ToString()));
    }

    private static string Clip(string text) => text.Length > MaxChars ? text[..MaxChars] : text;
}
