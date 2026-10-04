using System.Text.Json;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Storage;

public sealed partial class ProfileStore
{
    public TaskItem AddChatTask(string title, DateOnly? due, ChatTaskSource source, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(title) || source.Evidence.Length < 2 || !source.Body.Contains(source.Evidence, StringComparison.Ordinal))
            throw new ArgumentException("대화 원문에 포함된 근거와 업무 제목이 필요합니다.");
        using var tx = _db.BeginTransaction();
        using var insert = Command("""
            INSERT INTO task(title, evidence, due_date, created_at) VALUES($title,$evidence,$due,$at) RETURNING id
            """, ("$title", title.Trim()), ("$evidence", source.Evidence), ("$due", due?.ToString("yyyy-MM-dd")), ("$at", at.ToString("O")));
        insert.Transaction = tx;
        var id = (long)insert.ExecuteScalar()!;
        using var origin = Command("INSERT INTO task_chat_source(task_id,source_json) VALUES($id,$json)",
            ("$id", id), ("$json", JsonSerializer.Serialize(source)));
        origin.Transaction = tx; origin.ExecuteNonQuery(); tx.Commit();
        return new TaskItem(id, null, title.Trim(), source.Evidence, null, due, false, at, 0);
    }

    public ChatTaskSource? GetChatTaskSource(long taskId)
    {
        using var cmd = Command("SELECT source_json FROM task_chat_source WHERE task_id=$id", ("$id", taskId));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ChatTaskSource>(json) : null;
    }

    public void LinkContactNote(ContactPerson person, long noteId, DateTimeOffset at)
    {
        using var cmd = Command("INSERT OR REPLACE INTO contact_note_link(display_identity,note_id,linked_at) VALUES($identity,$note,$at)",
            ("$identity", person.DisplayIdentity), ("$note", noteId), ("$at", at.ToString("O")));
        cmd.ExecuteNonQuery();
    }
    public DateTimeOffset? ContactNoteLinkedAt(ContactPerson person, long noteId)
    {
        using var cmd = Command("SELECT linked_at FROM contact_note_link WHERE display_identity=$identity AND note_id=$note",
            ("$identity", person.DisplayIdentity), ("$note", noteId));
        return cmd.ExecuteScalar() is string time ? DateTimeOffset.Parse(time) : null;
    }
    public void UnlinkContactNote(ContactPerson person, long noteId)
    {
        using var cmd = Command("DELETE FROM contact_note_link WHERE display_identity=$identity AND note_id=$note",
            ("$identity", person.DisplayIdentity), ("$note", noteId)); cmd.ExecuteNonQuery();
    }
    public List<StoredNote> GetContactLinkedNotes(ContactPerson person)
    {
        using var cmd = Command($"SELECT {NoteColumns} FROM note n JOIN contact_note_link l ON l.note_id=n.id WHERE l.display_identity=$identity ORDER BY l.linked_at DESC LIMIT 50", ("$identity", person.DisplayIdentity));
        return ReadNotes(cmd);
    }
}
