using System.Text.Json;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Storage;

public sealed record ContactPreparationGroup(long Id, string Name, IReadOnlyList<ContactPerson> People,
    string Source, DateTimeOffset ObservedAt, DateTimeOffset UpdatedAt)
{
    public string Label => $"{Name} ({People.Count}명)";
    public override string ToString() => Label;
}

public sealed partial class ProfileStore
{
    /// <summary>화면에서 준비한 연락 후보. 작성창에서 신원을 확인한 recipient_group과 구분한다.</summary>
    public void SaveContactGroup(string name, ContactPreparation preparation, DateTimeOffset updatedAt)
    {
        name = name.Trim();
        if (name.Length == 0 || preparation.People.Count == 0) throw new ArgumentException("이름과 연락 대상이 필요합니다.");
        using var cmd = Command("""
            INSERT INTO contact_preparation_group(name, people_json, source, observed_at, updated_at)
            VALUES($name, $people, $source, $observed, $updated)
            ON CONFLICT(name) DO UPDATE SET people_json=excluded.people_json, source=excluded.source,
                observed_at=excluded.observed_at, updated_at=excluded.updated_at
            """, ("$name", name), ("$people", JsonSerializer.Serialize(preparation.People)), ("$source", preparation.Source),
            ("$observed", preparation.ObservedAt.ToString("O")), ("$updated", updatedAt.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    public List<ContactPreparationGroup> GetContactGroups()
    {
        using var cmd = Command("SELECT id,name,people_json,source,observed_at,updated_at FROM contact_preparation_group ORDER BY name");
        using var reader = cmd.ExecuteReader();
        var groups = new List<ContactPreparationGroup>();
        while (reader.Read()) groups.Add(new(reader.GetInt64(0), reader.GetString(1),
            JsonSerializer.Deserialize<List<ContactPerson>>(reader.GetString(2)) ?? [], reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5))));
        return groups;
    }

    public void DeleteContactGroup(long id)
    {
        using var cmd = Command("DELETE FROM contact_preparation_group WHERE id=$id", ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>발신자 이름의 정확한 일치만 조회한다. 같은 사람의 자료라는 신원 확증은 아니다.</summary>
    public List<StoredNote> GetContactNoteCandidates(string name, int limit = 8)
    {
        using var cmd = Command($"SELECT {NoteColumns} FROM note n WHERE n.sender_name=$name ORDER BY COALESCE(n.sent_at,n.first_seen_at) DESC LIMIT $limit",
            ("$name", name), ("$limit", limit));
        return ReadNotes(cmd);
    }
}
