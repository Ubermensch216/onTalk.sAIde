using System.Text.Json;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Storage;

public sealed partial class ProfileStore
{
    public void SaveHomePeople(PeopleSnapshot snapshot)
    {
        if (snapshot.Menu != MainMenu.Friends || snapshot.People.Count == 0) return;
        using var cmd = Command("INSERT OR REPLACE INTO home_people(id,snapshot_json) VALUES(1,$json)",
            ("$json", JsonSerializer.Serialize(snapshot)));
        cmd.ExecuteNonQuery();
    }

    public PeopleSnapshot? GetHomePeople()
    {
        using var cmd = Command("SELECT snapshot_json FROM home_people WHERE id=1");
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<PeopleSnapshot>(json) : null;
    }

    public long SaveHomeChat(ContactPerson person, ChatSnapshot source, ChatAnalysis analysis, DateTimeOffset at)
    {
        if (source.Messages.Count == 0 || source.Messages.Count > 80 || source.Messages.Sum(m => m.Body.Length) > 12000 ||
            string.IsNullOrWhiteSpace(analysis.Summary) || analysis.Points.Any(p =>
                !source.Messages.Contains(p.Message) || p.Evidence.Length < 2 || !p.Message.Body.Contains(p.Evidence, StringComparison.Ordinal)))
            throw new ArgumentException("선택 원문과 근거가 일치하는 대화 정리가 필요합니다.");
        var key = ChatSnapshot.Hash(source.RoomHint + "\n" + source.Fingerprint);
        using var tx = _db.BeginTransaction();
        using var cmd = Command("""
            INSERT INTO home_chat(digest_key,source_json,analysis_json,saved_at) VALUES($key,$source,$analysis,$at)
            ON CONFLICT(digest_key) DO UPDATE SET analysis_json=excluded.analysis_json,saved_at=excluded.saved_at
            RETURNING id
            """, ("$key", key), ("$source", JsonSerializer.Serialize(source)), ("$analysis", JsonSerializer.Serialize(analysis)), ("$at", at.ToString("O")));
        cmd.Transaction = tx;
        var id = (long)cmd.ExecuteScalar()!;
        using var link = Command("INSERT OR IGNORE INTO home_chat_contact(chat_id,display_identity) VALUES($id,$identity)",
            ("$id", id), ("$identity", person.DisplayIdentity));
        link.Transaction = tx; link.ExecuteNonQuery(); tx.Commit(); return id;
    }

    public List<HomeChatDigest> GetHomeChats()
    {
        var links = new Dictionary<long, List<string>>();
        using (var cmd = Command("SELECT chat_id,display_identity FROM home_chat_contact"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!links.TryGetValue(id, out var list)) links[id] = list = [];
                list.Add(reader.GetString(1));
            }
        using var chats = Command("SELECT id,source_json,analysis_json,saved_at FROM home_chat ORDER BY saved_at DESC");
        using var rows = chats.ExecuteReader();
        var result = new List<HomeChatDigest>();
        while (rows.Read()) result.Add(new(rows.GetInt64(0), JsonSerializer.Deserialize<ChatSnapshot>(rows.GetString(1))!,
            JsonSerializer.Deserialize<ChatAnalysis>(rows.GetString(2))!, DateTimeOffset.Parse(rows.GetString(3)),
            links.GetValueOrDefault(rows.GetInt64(0)) ?? []));
        return result;
    }

    public HomeDashboard GetHomeDashboard(IReadOnlyList<ContactPerson> people, DateTimeOffset now, int? days)
    {
        if (people.Count == 0) return HomeDashboard.Build(people, [], [], [], [], new Dictionary<long, ChatTaskSource>(), now, days);
        var identities = people.Select(p => p.DisplayIdentity).ToHashSet();
        var links = new Dictionary<long, List<string>>();
        using (var cmd = Command("SELECT note_id,display_identity FROM contact_note_link"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                if (!identities.Contains(reader.GetString(1))) continue;
                var id = reader.GetInt64(0);
                if (!links.TryGetValue(id, out var list)) links[id] = list = [];
                list.Add(reader.GetString(1));
            }
        var names = people.Select(p => p.Name).Distinct().ToList();
        var args = names.Select((name, i) => ("$name" + i, (object?)name)).ToList();
        var ids = links.Keys.ToList();
        args.AddRange(ids.Select((id, i) => ("$id" + i, (object?)id)));
        using var find = Command($"SELECT {NoteColumns} FROM note n WHERE n.sender_name IN ({string.Join(',', names.Select((_, i) => "$name" + i))})" +
            (ids.Count > 0 ? $" OR n.id IN ({string.Join(',', ids.Select((_, i) => "$id" + i))})" : ""), [.. args]);
        var notes = ReadNotes(find);
        var analyses = new Dictionary<long, NoteAnalysis>();
        using (var cmd = Command("SELECT note_id,result_json FROM analysis"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                if (links.ContainsKey(reader.GetInt64(0)) && JsonSerializer.Deserialize<NoteAnalysis>(reader.GetString(1)) is { } analysis)
                    analyses[reader.GetInt64(0)] = analysis;
        var tasks = GetTasks();
        var chatTasks = tasks.Where(t => t.NoteId is null).Select(t => (t.Id, Source: GetChatTaskSource(t.Id)))
            .Where(t => t.Source is not null).ToDictionary(t => t.Id, t => t.Source!);
        return HomeDashboard.Build(people, notes.Where(n => links.ContainsKey(n.Id)).Select(n =>
            new HomeNote(n, analyses.GetValueOrDefault(n.Id), links[n.Id])).ToList(), notes, GetHomeChats(), tasks, chatTasks, now, days);
    }

    public void UnlinkHomeChat(ContactPerson person, long chatId)
    {
        using var tx = _db.BeginTransaction();
        using var unlink = Command("DELETE FROM home_chat_contact WHERE chat_id=$id AND display_identity=$identity",
            ("$id", chatId), ("$identity", person.DisplayIdentity));
        unlink.Transaction = tx; unlink.ExecuteNonQuery();
        using var cleanup = Command("DELETE FROM home_chat WHERE id=$id AND NOT EXISTS(SELECT 1 FROM home_chat_contact WHERE chat_id=$id)", ("$id", chatId));
        cleanup.Transaction = tx; cleanup.ExecuteNonQuery(); tx.Commit();
    }
}
