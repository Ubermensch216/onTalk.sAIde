using System.Text.Json;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Storage;

public sealed partial class ProfileStore : IPersonalizedDispatchStore
{
    public void SavePersonalizedWork(PersonalizedWork work)
    {
        // Older editor snapshots cannot erase a send attempt and its frozen body.
        using (var read = Command("SELECT body FROM personalized_work WHERE id=$id", ("$id", work.Id)))
        {
            if (read.ExecuteScalar() is string json && JsonSerializer.Deserialize<PersonalizedWork>(json) is { } previous)
                foreach (var item in work.Items)
                {
                    var saved = previous.Items.FirstOrDefault(i => i.Id == item.Id);
                    if (saved is { Attempted: true } && saved.DispatchRevision >= item.DispatchRevision)
                    {
                        item.DispatchId = saved.DispatchId; item.DispatchRevision = saved.DispatchRevision;
                        item.Values = new Dictionary<string, string>(saved.Values);
                        item.Body = saved.Body; item.Status = saved.Status;
                    }
                }
        }
        using var cmd = Command("INSERT INTO personalized_work(id,body,updated_at) VALUES($id,$body,$at) ON CONFLICT(id) DO UPDATE SET body=excluded.body,updated_at=excluded.updated_at",
            ("$id", work.Id), ("$body", JsonSerializer.Serialize(work)), ("$at", DateTimeOffset.Now.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    public List<PersonalizedWork> GetPersonalizedWorks()
    {
        using var cmd = Command("SELECT body FROM personalized_work ORDER BY updated_at DESC");
        using var reader = cmd.ExecuteReader();
        var result = new List<PersonalizedWork>();
        while (reader.Read())
            if (JsonSerializer.Deserialize<PersonalizedWork>(reader.GetString(0)) is { } work) result.Add(work);
        return result;
    }
    public void DeletePersonalizedWork(string id)
    {
        using var cmd = Command("DELETE FROM personalized_work WHERE id=$id", ("$id", id));
        cmd.ExecuteNonQuery();
    }
}
