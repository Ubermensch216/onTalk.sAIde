using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Storage;

public sealed record SaveResult(long NoteId, bool IsNew, DateTimeOffset FirstSeenAt);

/// <summary>
/// 온톡 계정 하나의 저장소. 계정마다 폴더와 암호화 키가 따로 있어 다른 계정 자료와 섞이지 않는다.
/// DB는 SQLCipher로 암호화하고, 키는 Windows 사용자 계정(DPAPI)으로 보호한다.
/// </summary>
public sealed class ProfileStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _db;

    public string LoginId { get; }

    private ProfileStore(string loginId, SqliteConnection db)
    {
        LoginId = loginId;
        _db = db;
    }

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnTalk.sAIde", "profiles");

    public static ProfileStore Open(string root, string loginId)
    {
        if (loginId.Length == 0 || loginId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || loginId.Contains(".."))
        {
            throw new ArgumentException("폴더 이름으로 쓸 수 없는 계정 ID입니다.", nameof(loginId));
        }
        var dir = Path.Combine(root, loginId);
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "notes.db");
        var key = LoadOrCreateKey(Path.Combine(dir, "key.bin"), dbExists: File.Exists(dbPath));

        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        db.Open();
        try
        {
            // 키는 16진수 원시 키로 넘긴다. 문자열 바인딩이 되지 않는 구문이다.
            Execute(db, $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\"");
            Execute(db, "PRAGMA foreign_keys = ON");
            Migrate(db);
        }
        catch
        {
            db.Dispose();
            throw;
        }
        return new ProfileStore(loginId, db);
    }

    private static byte[] LoadOrCreateKey(string keyPath, bool dbExists)
    {
        if (File.Exists(keyPath))
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser);
        }
        if (dbExists)
        {
            // 키 없이 새 키를 만들면 기존 자료를 영영 열 수 없게 된 사실이 가려진다.
            throw new InvalidOperationException("저장소는 있는데 암호화 키 파일이 없습니다.");
        }
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyPath, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
        return key;
    }

    private static void Migrate(SqliteConnection db)
    {
        var version = Convert.ToInt32(Scalar(db, "PRAGMA user_version"));
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"더 새로운 버전의 저장소입니다 (v{version}).");
        }
        if (version < 1)
        {
            Execute(db, """
                BEGIN;
                CREATE TABLE note (
                    id              INTEGER PRIMARY KEY,
                    note_key        TEXT NOT NULL UNIQUE,
                    direction       TEXT NOT NULL,
                    sender_name     TEXT NOT NULL,
                    sender_detail   TEXT NOT NULL,
                    sent_at_text    TEXT NOT NULL,
                    sent_at         TEXT,
                    year_inferred   INTEGER NOT NULL,
                    recipient_count INTEGER,
                    body            TEXT NOT NULL,
                    first_seen_at   TEXT NOT NULL,
                    last_seen_at    TEXT NOT NULL
                );
                CREATE TABLE attachment (
                    id      INTEGER PRIMARY KEY,
                    note_id INTEGER NOT NULL REFERENCES note(id) ON DELETE CASCADE,
                    name    TEXT NOT NULL,
                    UNIQUE (note_id, name)
                );
                PRAGMA user_version = 1;
                COMMIT;
                """);
        }
    }

    /// <summary>쪽지를 저장한다. 이미 있는 쪽지면 마지막으로 본 시각만 갱신한다.</summary>
    public SaveResult SaveReceived(ReceivedNote note, DateTimeOffset seenAt)
    {
        using var tx = _db.BeginTransaction();
        var seen = seenAt.ToString("O");

        using (var find = _db.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT id, first_seen_at FROM note WHERE note_key = $key";
            find.Parameters.AddWithValue("$key", note.Key);
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                var id = reader.GetInt64(0);
                var firstSeen = DateTimeOffset.Parse(reader.GetString(1));
                reader.Close();
                using var touch = _db.CreateCommand();
                touch.Transaction = tx;
                touch.CommandText = "UPDATE note SET last_seen_at = $seen WHERE id = $id";
                touch.Parameters.AddWithValue("$seen", seen);
                touch.Parameters.AddWithValue("$id", id);
                touch.ExecuteNonQuery();
                tx.Commit();
                return new SaveResult(id, false, firstSeen);
            }
        }

        long noteId;
        using (var insert = _db.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO note (note_key, direction, sender_name, sender_detail, sent_at_text, sent_at,
                                  year_inferred, recipient_count, body, first_seen_at, last_seen_at)
                VALUES ($key, 'received', $name, $detail, $text, $at, $inferred, $count, $body, $seen, $seen)
                RETURNING id
                """;
            insert.Parameters.AddWithValue("$key", note.Key);
            insert.Parameters.AddWithValue("$name", note.SenderName);
            insert.Parameters.AddWithValue("$detail", note.SenderDetail);
            insert.Parameters.AddWithValue("$text", note.SentAtText);
            insert.Parameters.AddWithValue("$at", (object?)note.SentAt?.Value.ToString("O") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$inferred", note.SentAt?.YearInferred == true ? 1 : 0);
            insert.Parameters.AddWithValue("$count", (object?)note.RecipientCount ?? DBNull.Value);
            insert.Parameters.AddWithValue("$body", note.Body);
            insert.Parameters.AddWithValue("$seen", seen);
            noteId = (long)insert.ExecuteScalar()!;
        }
        foreach (var name in note.AttachmentNames.Distinct())
        {
            using var attach = _db.CreateCommand();
            attach.Transaction = tx;
            attach.CommandText = "INSERT INTO attachment (note_id, name) VALUES ($id, $name)";
            attach.Parameters.AddWithValue("$id", noteId);
            attach.Parameters.AddWithValue("$name", name);
            attach.ExecuteNonQuery();
        }
        tx.Commit();
        return new SaveResult(noteId, true, seenAt);
    }

    public int CountNotes() => Convert.ToInt32(Scalar(_db, "SELECT COUNT(*) FROM note"));

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose() => _db.Dispose();
}
