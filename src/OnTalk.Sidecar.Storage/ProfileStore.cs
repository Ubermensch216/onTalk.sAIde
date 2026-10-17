using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Storage;

public sealed record SaveResult(long NoteId, bool IsNew, DateTimeOffset FirstSeenAt);
public sealed record StoredDraft(string Slot, string Body, DateTimeOffset UpdatedAt);

/// <summary>
/// 온톡 계정 하나의 저장소. 계정마다 폴더와 암호화 키가 따로 있어 다른 계정 자료와 섞이지 않는다.
/// DB는 SQLCipher로 암호화하고, 키는 Windows 사용자 계정(DPAPI)으로 보호한다.
/// </summary>
public sealed partial class ProfileStore : IDisposable
{
    private const int SchemaVersion = 10;
    private readonly SqliteConnection _db;

    public string LoginId { get; }

    private ProfileStore(string loginId, SqliteConnection db, string dir, byte[] key)
    {
        LoginId = loginId;
        _db = db;
        _dir = dir;
        _key = key;
        _filesDir = Path.Combine(dir, "files");
        // 파일 암호화에는 DB 키를 그대로 쓰지 않고 거기서 갈라낸 키를 쓴다.
        _fileKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, info: "OnTalk.sAIde files"u8.ToArray());
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
            Execute(db, "PRAGMA journal_mode = WAL");
            Execute(db, "PRAGMA synchronous = NORMAL");
            BackUpBeforeMigration(db, dbPath);
            Migrate(db);
        }
        catch
        {
            db.Dispose();
            throw;
        }
        return new ProfileStore(loginId, db, dir, key);
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

    /// <summary>
    /// 이전 버전의 저장소를 새 구조로 올리기 전에 사본을 남긴다. 올리다 실패하면 이 사본으로 되돌릴 수 있다.
    /// 사본도 같은 키로 암호화되어 있다.
    /// </summary>
    private static void BackUpBeforeMigration(SqliteConnection db, string dbPath)
    {
        var version = Convert.ToInt32(Scalar(db, "PRAGMA user_version"));
        if (version is > 0 and < SchemaVersion)
        {
            Execute(db, "PRAGMA wal_checkpoint(TRUNCATE)");
            File.Copy(dbPath, $"{dbPath}.v{version}.bak", overwrite: true);
        }
    }

    public const int CurrentSchemaVersion = SchemaVersion;

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
        if (version < 2)
        {
            Execute(db, """
                BEGIN;
                CREATE TABLE analysis (
                    note_id        INTEGER PRIMARY KEY REFERENCES note(id) ON DELETE CASCADE,
                    model          TEXT NOT NULL,
                    prompt_version TEXT NOT NULL,
                    result_json    TEXT NOT NULL,
                    created_at     TEXT NOT NULL
                );
                CREATE TABLE task (
                    id             INTEGER PRIMARY KEY,
                    note_id        INTEGER REFERENCES note(id) ON DELETE SET NULL,
                    title          TEXT NOT NULL,
                    evidence       TEXT,
                    due_text       TEXT,
                    due_date       TEXT,
                    done           INTEGER NOT NULL DEFAULT 0,
                    created_at     TEXT NOT NULL,
                    done_at        TEXT,
                    reminded_stage INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX task_note ON task(note_id);
                PRAGMA user_version = 2;
                COMMIT;
                """);
        }
        if (version < 3)
        {
            Execute(db, """
                BEGIN;
                CREATE TABLE embedding (
                    note_id INTEGER PRIMARY KEY REFERENCES note(id) ON DELETE CASCADE,
                    model   TEXT NOT NULL,
                    vector  BLOB NOT NULL
                );
                CREATE INDEX note_sent ON note(sent_at);
                PRAGMA user_version = 3;
                COMMIT;
                """);
        }
        if (version < 4)
        {
            Execute(db, """
                BEGIN;
                CREATE TABLE draft (
                    slot       TEXT PRIMARY KEY,
                    body       TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE template (
                    id   INTEGER PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    body TEXT NOT NULL
                );
                PRAGMA user_version = 4;
                COMMIT;
                """);
        }
        if (version < 5)
        {
            Execute(db, """
                BEGIN;
                CREATE TABLE dispatch (
                    id         INTEGER PRIMARY KEY,
                    created_at TEXT NOT NULL,
                    recipients TEXT NOT NULL,
                    body       TEXT NOT NULL,
                    reserve_at TEXT,
                    status     TEXT NOT NULL,
                    detail     TEXT NOT NULL
                );
                PRAGMA user_version = 5;
                COMMIT;
                """);
        }
        if (version < 6)
        {
            Execute(db, """
                BEGIN;
                ALTER TABLE attachment ADD COLUMN sha256 TEXT;
                ALTER TABLE attachment ADD COLUMN size INTEGER;
                ALTER TABLE attachment ADD COLUMN stored_at TEXT;
                ALTER TABLE attachment ADD COLUMN extract_status TEXT;
                ALTER TABLE attachment ADD COLUMN text TEXT;
                CREATE TABLE recipient_group (
                    id   INTEGER PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE
                );
                CREATE TABLE group_member (
                    id       INTEGER PRIMARY KEY,
                    group_id INTEGER NOT NULL REFERENCES recipient_group(id) ON DELETE CASCADE,
                    name     TEXT NOT NULL,
                    title    TEXT NOT NULL,
                    details  TEXT NOT NULL
                );
                PRAGMA user_version = 6;
                COMMIT;
                """);
        }
        if (version < 7)
        {
            Execute(db, """
                BEGIN;
                CREATE TABLE contact_preparation_group (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    people_json TEXT NOT NULL,
                    source TEXT NOT NULL,
                    observed_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                PRAGMA user_version = 7;
                COMMIT;
                """);
        }
        if (version < 8)
        {
            Execute(db, """
                BEGIN;
                CREATE TABLE task_chat_source (
                    task_id INTEGER PRIMARY KEY REFERENCES task(id) ON DELETE CASCADE,
                    source_json TEXT NOT NULL
                );
                CREATE TABLE contact_note_link (
                    display_identity TEXT NOT NULL,
                    note_id INTEGER NOT NULL REFERENCES note(id) ON DELETE CASCADE,
                    linked_at TEXT NOT NULL,
                    PRIMARY KEY(display_identity, note_id)
                );
                PRAGMA user_version = 8;
                COMMIT;
                """);
        }
        if (version < 9)
        {
            Execute(db, """
                BEGIN;
                CREATE TABLE home_people (id INTEGER PRIMARY KEY CHECK(id=1), snapshot_json TEXT NOT NULL);
                CREATE TABLE home_chat (
                    id INTEGER PRIMARY KEY,
                    digest_key TEXT NOT NULL UNIQUE,
                    source_json TEXT NOT NULL,
                    analysis_json TEXT NOT NULL,
                    saved_at TEXT NOT NULL
                );
                CREATE TABLE home_chat_contact (
                    chat_id INTEGER NOT NULL REFERENCES home_chat(id) ON DELETE CASCADE,
                    display_identity TEXT NOT NULL,
                    PRIMARY KEY(chat_id,display_identity)
                );
                PRAGMA user_version = 9;
                COMMIT;
                """);
        }
        if (version < 10)
        {
            Execute(db, """
                BEGIN;
                CREATE TABLE personalized_work (id TEXT PRIMARY KEY, body TEXT NOT NULL, updated_at TEXT NOT NULL);
                PRAGMA user_version = 10;
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

    /// <summary>업무 목록에 출처로 보여 줄 쪽지 한 줄 설명.</summary>
    public string? DescribeNote(long noteId)
    {
        using var cmd = Command("SELECT sender_name, COALESCE(substr(sent_at, 1, 10), sent_at_text) FROM note WHERE id = $id",
            ("$id", noteId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? $"{reader.GetString(0)} · {reader.GetString(1)}" : null;
    }

    /// <summary>같은 모델·프롬프트 버전으로 만든 분석만 돌려준다. 버전이 다르면 다시 분석해야 한다.</summary>
    public NoteAnalysis? GetAnalysis(long noteId, string model, string promptVersion)
    {
        using var cmd = Command(
            "SELECT result_json FROM analysis WHERE note_id = $id AND model = $model AND prompt_version = $ver",
            ("$id", noteId), ("$model", model), ("$ver", promptVersion));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<NoteAnalysis>(json) : null;
    }

    public void SaveAnalysis(long noteId, string model, string promptVersion, NoteAnalysis analysis, DateTimeOffset at)
    {
        using var cmd = Command("""
            INSERT INTO analysis (note_id, model, prompt_version, result_json, created_at)
            VALUES ($id, $model, $ver, $json, $at)
            ON CONFLICT(note_id) DO UPDATE SET model = $model, prompt_version = $ver, result_json = $json, created_at = $at
            """,
            ("$id", noteId), ("$model", model), ("$ver", promptVersion),
            ("$json", JsonSerializer.Serialize(analysis)), ("$at", at.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    public TaskItem AddTask(long? noteId, string title, string? evidence, string? dueText, DateOnly? due, DateTimeOffset at)
    {
        using var cmd = Command("""
            INSERT INTO task (note_id, title, evidence, due_text, due_date, created_at)
            VALUES ($note, $title, $evidence, $dueText, $due, $at)
            RETURNING id
            """,
            ("$note", noteId), ("$title", title), ("$evidence", evidence), ("$dueText", dueText),
            ("$due", due?.ToString("yyyy-MM-dd")), ("$at", at.ToString("O")));
        var id = (long)cmd.ExecuteScalar()!;
        return new TaskItem(id, noteId, title, evidence, dueText, due, false, at, 0);
    }

    /// <summary>열린 업무가 먼저, 그 안에서는 기한이 빠른 순. 기한 없는 업무는 뒤로 간다.</summary>
    public List<TaskItem> GetTasks(long? noteId = null)
    {
        using var cmd = Command($"""
            SELECT id, note_id, title, evidence, due_text, due_date, done, created_at, reminded_stage
            FROM task {(noteId is null ? "" : "WHERE note_id = $note")}
            ORDER BY done, due_date IS NULL, due_date, id
            """,
            ("$note", noteId));
        using var reader = cmd.ExecuteReader();
        var tasks = new List<TaskItem>();
        while (reader.Read())
        {
            tasks.Add(new TaskItem(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd"),
                reader.GetInt64(6) != 0,
                DateTimeOffset.Parse(reader.GetString(7)),
                reader.GetInt32(8)));
        }
        return tasks;
    }

    public void SetTaskDone(long taskId, bool done, DateTimeOffset at)
    {
        using var cmd = Command("UPDATE task SET done = $done, done_at = $at WHERE id = $id",
            ("$done", done ? 1 : 0), ("$at", done ? at.ToString("O") : null), ("$id", taskId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>기한을 바꾼다. 새 기한에 맞춰 다시 알리도록 알림 단계를 되돌린다.</summary>
    public void SetTaskDue(long taskId, DateOnly? due)
    {
        using var cmd = Command("UPDATE task SET due_date = $due, reminded_stage = 0 WHERE id = $id",
            ("$due", due?.ToString("yyyy-MM-dd")), ("$id", taskId));
        cmd.ExecuteNonQuery();
    }

    public void MarkReminded(long taskId, int stage)
    {
        using var cmd = Command("UPDATE task SET reminded_stage = $stage WHERE id = $id",
            ("$stage", stage), ("$id", taskId));
        cmd.ExecuteNonQuery();
    }

    public void DeleteTask(long taskId)
    {
        using var cmd = Command("DELETE FROM task WHERE id = $id", ("$id", taskId));
        cmd.ExecuteNonQuery();
    }

    private const string NoteColumns =
        "n.id, n.sender_name, n.sender_detail, n.sent_at_text, n.sent_at, n.year_inferred, n.recipient_count, n.body, n.first_seen_at";

    public StoredNote? GetNote(long noteId)
    {
        using var cmd = Command($"SELECT {NoteColumns} FROM note n WHERE n.id = $id", ("$id", noteId));
        return ReadNotes(cmd).FirstOrDefault();
    }

    /// <summary>
    /// 발신자·본문·첨부 파일명에서 낱말을 찾는다. 낱말은 모두 들어 있어야 하고 대소문자는 구분하지 않는다.
    /// 한국어는 형태소로 나누지 않고 글자 그대로 찾으므로 두 글자 이름도 찾는다.
    /// </summary>
    public List<StoredNote> SearchNotes(NoteQuery query, int limit = 200)
    {
        var where = new List<string>();
        var parameters = new List<(string, object?)> { ("$limit", limit) };
        for (var i = 0; i < query.Terms.Count; i++)
        {
            where.Add($"""
                ((n.sender_name || ' ' || n.sender_detail || ' ' || n.body) LIKE $t{i} ESCAPE '\'
                 OR EXISTS (SELECT 1 FROM attachment a WHERE a.note_id = n.id
                            AND (a.name LIKE $t{i} ESCAPE '\' OR a.text LIKE $t{i} ESCAPE '\')))
                """);
            var escaped = query.Terms[i].Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            parameters.Add(($"$t{i}", $"%{escaped}%"));
        }
        if (query.Since is { } since)
        {
            where.Add("n.sent_at >= $since");
            parameters.Add(("$since", since.ToOffset(KoreanNoteTime.SeoulOffset).ToString("O")));
        }
        if (query.WithAttachmentOnly)
        {
            where.Add("EXISTS (SELECT 1 FROM attachment a WHERE a.note_id = n.id)");
        }
        using var cmd = Command($"""
            SELECT {NoteColumns} FROM note n
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY COALESCE(n.sent_at, n.first_seen_at) DESC
            LIMIT $limit
            """, [.. parameters]);
        return ReadNotes(cmd);
    }

    private List<StoredNote> ReadNotes(SqliteCommand cmd)
    {
        var notes = new List<StoredNote>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                NoteTime? sentAt = reader.IsDBNull(4)
                    ? null
                    : new NoteTime(DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt64(5) != 0);
                notes.Add(new StoredNote(
                    reader.GetInt64(0),
                    new ReceivedNote(reader.GetString(1), reader.GetString(2), reader.GetString(3), sentAt,
                        reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.GetString(7), []),
                    DateTimeOffset.Parse(reader.GetString(8))));
            }
        }
        for (var i = 0; i < notes.Count; i++)
        {
            using var attach = Command("SELECT name FROM attachment WHERE note_id = $id ORDER BY id", ("$id", notes[i].Id));
            using var reader = attach.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
            if (names.Count > 0)
            {
                notes[i] = notes[i] with { Note = notes[i].Note with { AttachmentNames = names } };
            }
        }
        return notes;
    }

    /// <summary>검색이 미치는 범위. 사이드카가 켜진 동안 사용자가 연 쪽지만 들어 있다.</summary>
    public CollectionRange GetCollectionRange()
    {
        using var cmd = Command("SELECT COUNT(*), MIN(first_seen_at) FROM note");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new CollectionRange(reader.GetInt32(0), reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)));
    }

    /// <summary>해당 모델의 임베딩이 아직 없는 쪽지.</summary>
    public List<StoredNote> GetNotesWithoutEmbedding(string model, int limit)
    {
        using var cmd = Command($"""
            SELECT {NoteColumns} FROM note n
            LEFT JOIN embedding e ON e.note_id = n.id AND e.model = $model
            WHERE e.note_id IS NULL
            ORDER BY n.id DESC LIMIT $limit
            """, ("$model", model), ("$limit", limit));
        return ReadNotes(cmd);
    }

    public void SaveEmbedding(long noteId, string model, float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        using var cmd = Command("""
            INSERT INTO embedding (note_id, model, vector) VALUES ($id, $model, $vector)
            ON CONFLICT(note_id) DO UPDATE SET model = $model, vector = $vector
            """, ("$id", noteId), ("$model", model), ("$vector", bytes));
        cmd.ExecuteNonQuery();
    }

    public List<(long NoteId, float[] Vector)> GetEmbeddings(string model)
    {
        using var cmd = Command("SELECT note_id, vector FROM embedding WHERE model = $model", ("$model", model));
        using var reader = cmd.ExecuteReader();
        var result = new List<(long, float[])>();
        while (reader.Read())
        {
            var bytes = (byte[])reader[1];
            var vector = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            result.Add((reader.GetInt64(0), vector));
        }
        return result;
    }

    /// <summary>제목에 낱말이 모두 들어 있는 업무.</summary>
    public List<TaskItem> SearchTasks(IReadOnlyList<string> terms) =>
        terms.Count == 0
            ? []
            : GetTasks().Where(t => terms.All(term => t.Title.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();

    public string? GetDraft(string slot)
    {
        using var cmd = Command("SELECT body FROM draft WHERE slot = $slot", ("$slot", slot));
        return cmd.ExecuteScalar() as string;
    }

    public List<StoredDraft> GetDrafts(int limit = 50)
    {
        using var cmd = Command("SELECT slot, body, updated_at FROM draft ORDER BY julianday(updated_at) DESC, slot LIMIT $limit", ("$limit", limit));
        using var reader = cmd.ExecuteReader();
        var drafts = new List<StoredDraft>();
        while (reader.Read()) drafts.Add(new StoredDraft(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2))));
        return drafts;
    }

    /// <summary>초안을 저장한다. 빈 초안은 자리를 지운다.</summary>
    public void SaveDraft(string slot, string body, DateTimeOffset at)
    {
        using var cmd = body.Length == 0
            ? Command("DELETE FROM draft WHERE slot = $slot", ("$slot", slot))
            : Command("""
                INSERT INTO draft (slot, body, updated_at) VALUES ($slot, $body, $at)
                ON CONFLICT(slot) DO UPDATE SET body = $body, updated_at = $at
                """, ("$slot", slot), ("$body", body), ("$at", at.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    public List<Template> GetTemplates()
    {
        using var cmd = Command("SELECT id, name, body FROM template ORDER BY name");
        using var reader = cmd.ExecuteReader();
        var templates = new List<Template>();
        while (reader.Read())
        {
            templates.Add(new Template(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }
        return templates;
    }

    /// <summary>같은 이름의 템플릿이 있으면 내용을 바꾼다.</summary>
    public void SaveTemplate(string name, string body)
    {
        using var cmd = Command("""
            INSERT INTO template (name, body) VALUES ($name, $body)
            ON CONFLICT(name) DO UPDATE SET body = $body
            """, ("$name", name), ("$body", body));
        cmd.ExecuteNonQuery();
    }

    public void DeleteTemplate(long id)
    {
        using var cmd = Command("DELETE FROM template WHERE id = $id", ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 전송 단추를 누르기 전에 기록을 남긴다. 누른 뒤 앱이 죽어도 "눌렀을 수 있다"는 사실이 남아
    /// 같은 쪽지를 모르고 다시 보내지 않게 된다.
    /// </summary>
    public long BeginDispatch(IReadOnlyList<string> recipientNames, string body, DateTime? reserveAt, DateTimeOffset at)
    {
        using var cmd = Command("""
            INSERT INTO dispatch (created_at, recipients, body, reserve_at, status, detail)
            VALUES ($at, $names, $body, $reserve, $status, '')
            RETURNING id
            """,
            ("$at", at.ToString("O")), ("$names", JsonSerializer.Serialize(recipientNames)), ("$body", body),
            ("$reserve", reserveAt?.ToString("yyyy-MM-ddTHH:mm:ss")), ("$status", DispatchStatus.Pending.ToString()));
        return (long)cmd.ExecuteScalar()!;
    }

    public void FinishDispatch(long id, DispatchStatus status, string detail)
    {
        using var cmd = Command("UPDATE dispatch SET status = $status, detail = $detail WHERE id = $id",
            ("$status", status.ToString()), ("$detail", detail), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>최근 발송 기록. 최신이 먼저다.</summary>
    public List<DispatchRecord> GetDispatches(int limit = 50)
    {
        using var cmd = Command(
            "SELECT id, created_at, recipients, body, reserve_at, status, detail FROM dispatch ORDER BY id DESC LIMIT $limit",
            ("$limit", limit));
        using var reader = cmd.ExecuteReader();
        var records = new List<DispatchRecord>();
        while (reader.Read())
        {
            records.Add(new DispatchRecord(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [],
                reader.GetString(3),
                reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
                Enum.Parse<DispatchStatus>(reader.GetString(5)),
                reader.GetString(6)));
        }
        return records;
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd;
    }

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
