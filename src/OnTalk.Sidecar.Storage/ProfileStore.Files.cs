using System.Security.Cryptography;
using System.Text.Json;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Storage;

// 첨부파일 보관과 수신자 그룹.
public sealed partial class ProfileStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const long MaxFileBytes = 200L * 1024 * 1024;

    private readonly string _dir;
    private readonly byte[] _key;
    private readonly string _filesDir;
    private readonly byte[] _fileKey;

    /// <summary>미뤄 둔 기록을 DB 파일에 모두 써 넣고, 백업에 필요한 폴더와 키를 내준다.</summary>
    internal (string Dir, byte[] Key) PrepareForBackup()
    {
        Execute(_db, "PRAGMA wal_checkpoint(TRUNCATE)");
        return (_dir, _key);
    }

    /// <summary>
    /// 한 계정의 보관 자료(쪽지, 업무, 초안, 파일, 키)를 모두 지운다. 그 계정의 저장소가 열려 있으면 먼저 닫아야 한다.
    /// 온톡에 있는 쪽지에는 영향이 없다.
    /// </summary>
    public static void DeleteProfile(string root, string loginId)
    {
        var dir = Path.Combine(root, loginId);
        if (loginId.Length > 0 && loginId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !loginId.Contains("..") &&
            Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public List<AttachmentInfo> GetAttachments(long noteId)
    {
        using var cmd = Command(
            "SELECT id, note_id, name, sha256, size, stored_at, extract_status FROM attachment WHERE note_id = $id ORDER BY id",
            ("$id", noteId));
        using var reader = cmd.ExecuteReader();
        var list = new List<AttachmentInfo>();
        while (reader.Read())
        {
            list.Add(new AttachmentInfo(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Enum.Parse<ExtractStatus>(reader.GetString(6))));
        }
        return list;
    }

    /// <summary>
    /// 파일 내용을 암호화해 보관하고 첨부 기록에 연결한다. 원본 파일은 건드리지 않는다.
    /// 같은 내용은 한 번만 저장하지만 쪽지마다의 연결은 따로 남는다. 내용 추출이 실패해도 보관은 된다.
    /// </summary>
    public AttachmentInfo StoreAttachment(long attachmentId, byte[] data, DateTimeOffset at)
    {
        if (data.LongLength > MaxFileBytes)
        {
            throw new InvalidOperationException($"{MaxFileBytes / 1024 / 1024}MB를 넘는 파일은 보관하지 않습니다.");
        }
        var current = FindAttachment(attachmentId) ?? throw new InvalidOperationException("첨부 기록을 찾지 못했습니다.");
        var hash = Convert.ToHexString(SHA256.HashData(data));
        var path = BlobPath(hash);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(_filesDir);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[data.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(_fileKey, TagSize))
            {
                aes.Encrypt(nonce, data, cipher, tag);
            }
            // 쓰다가 끊겨도 반쪽 파일이 정상 파일로 남지 않게 임시 이름으로 쓰고 바꾼다.
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
            {
                stream.Write(nonce);
                stream.Write(tag);
                stream.Write(cipher);
            }
            File.Move(temp, path, overwrite: true);
        }
        var (status, text) = AttachmentText.Extract(current.Name, data);
        using (var cmd = Command("""
            UPDATE attachment SET sha256 = $hash, size = $size, stored_at = $at, extract_status = $status, text = $text
            WHERE id = $id
            """,
            ("$hash", hash), ("$size", data.LongLength), ("$at", at.ToString("O")), ("$status", status.ToString()),
            ("$text", status == ExtractStatus.Extracted ? text : null), ("$id", attachmentId)))
        {
            cmd.ExecuteNonQuery();
        }
        if (current.Sha256 is { } old && old != hash)
        {
            DeleteBlobIfUnused(old);
        }
        return FindAttachment(attachmentId)!;
    }

    /// <summary>보관한 파일 내용을 돌려준다. 저장 파일이 손상되었거나 바뀌었으면 예외를 던진다.</summary>
    public byte[] ReadAttachment(long attachmentId)
    {
        var info = FindAttachment(attachmentId);
        if (info?.Sha256 is not { } hash)
        {
            throw new InvalidOperationException("아직 확보하지 않은 파일입니다.");
        }
        var path = BlobPath(hash);
        if (!File.Exists(path))
        {
            throw new InvalidDataException("보관 파일이 없습니다.");
        }
        var raw = File.ReadAllBytes(path);
        if (raw.Length < NonceSize + TagSize)
        {
            throw new InvalidDataException("보관 파일이 손상되었습니다.");
        }
        var data = new byte[raw.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(_fileKey, TagSize);
            aes.Decrypt(raw.AsSpan(0, NonceSize), raw.AsSpan(NonceSize + TagSize), raw.AsSpan(NonceSize, TagSize), data);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("보관 파일이 손상되었거나 바뀌었습니다.", ex);
        }
        return Convert.ToHexString(SHA256.HashData(data)) == hash
            ? data
            : throw new InvalidDataException("보관 파일의 내용이 기록과 다릅니다.");
    }

    private AttachmentInfo? FindAttachment(long attachmentId)
    {
        using var cmd = Command("SELECT note_id FROM attachment WHERE id = $id", ("$id", attachmentId));
        return cmd.ExecuteScalar() is long noteId ? GetAttachments(noteId).FirstOrDefault(a => a.Id == attachmentId) : null;
    }

    private string BlobPath(string hash) => Path.Combine(_filesDir, hash + ".bin");

    private void DeleteBlobIfUnused(string hash)
    {
        using var cmd = Command("SELECT COUNT(*) FROM attachment WHERE sha256 = $hash", ("$hash", hash));
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
        {
            File.Delete(BlobPath(hash));
        }
    }

    public List<RecipientGroup> GetGroups()
    {
        var groups = new List<RecipientGroup>();
        using (var cmd = Command("SELECT id, name FROM recipient_group ORDER BY name"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                groups.Add(new RecipientGroup(reader.GetInt64(0), reader.GetString(1), []));
            }
        }
        for (var i = 0; i < groups.Count; i++)
        {
            using var cmd = Command("SELECT name, title, details FROM group_member WHERE group_id = $id ORDER BY id",
                ("$id", groups[i].Id));
            using var reader = cmd.ExecuteReader();
            var members = new List<Recipient>();
            while (reader.Read())
            {
                members.Add(new Recipient(reader.GetString(0), reader.GetString(1),
                    JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? []));
            }
            groups[i] = groups[i] with { Members = members };
        }
        return groups;
    }

    /// <summary>그룹을 저장한다. 같은 이름의 그룹이 있으면 구성원을 통째로 바꾼다.</summary>
    public void SaveGroup(string name, IReadOnlyList<Recipient> members)
    {
        using var tx = _db.BeginTransaction();
        using (var delete = Command("DELETE FROM recipient_group WHERE name = $name", ("$name", name)))
        {
            delete.Transaction = tx;
            delete.ExecuteNonQuery();
        }
        long id;
        using (var insert = Command("INSERT INTO recipient_group (name) VALUES ($name) RETURNING id", ("$name", name)))
        {
            insert.Transaction = tx;
            id = (long)insert.ExecuteScalar()!;
        }
        foreach (var member in members)
        {
            using var add = Command("INSERT INTO group_member (group_id, name, title, details) VALUES ($id, $name, $title, $details)",
                ("$id", id), ("$name", member.Name), ("$title", member.Title), ("$details", JsonSerializer.Serialize(member.Details)));
            add.Transaction = tx;
            add.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeleteGroup(long id)
    {
        using var cmd = Command("DELETE FROM recipient_group WHERE id = $id", ("$id", id));
        cmd.ExecuteNonQuery();
    }
}
