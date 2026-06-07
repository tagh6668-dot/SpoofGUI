using Microsoft.Data.Sqlite;
using SpoofGUI.Models;

namespace SpoofGUI.Database;

public sealed class SubscriptionRepository
{
    private readonly DatabaseConnection _db;
    public SubscriptionRepository(DatabaseConnection db) => _db = db;

    public IReadOnlyList<Subscription> All()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, url, auto_update, last_updated, last_count
FROM subscriptions ORDER BY id ASC;";
        var list = new List<Subscription>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Subscription
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                Url = r.GetString(2),
                AutoUpdate = r.GetInt64(3) != 0,
                LastUpdated = r.GetString(4),
                LastCount = (int)r.GetInt64(5),
            });
        }
        return list;
    }

    public void Upsert(Subscription sub)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO subscriptions (id, name, url, auto_update, last_updated, last_count)
VALUES ($id, $name, $url, $auto_update, $last_updated, $last_count)
ON CONFLICT(id) DO UPDATE SET
    name=excluded.name,
    url=excluded.url,
    auto_update=excluded.auto_update,
    last_updated=excluded.last_updated,
    last_count=excluded.last_count;
SELECT CASE WHEN $id IS NULL THEN last_insert_rowid() ELSE $id END;";
        cmd.Parameters.AddWithValue("$id", sub.Id == 0 ? DBNull.Value : sub.Id);
        cmd.Parameters.AddWithValue("$name", sub.Name);
        cmd.Parameters.AddWithValue("$url", sub.Url);
        cmd.Parameters.AddWithValue("$auto_update", sub.AutoUpdate ? 1 : 0);
        cmd.Parameters.AddWithValue("$last_updated", sub.LastUpdated);
        cmd.Parameters.AddWithValue("$last_count", sub.LastCount);
        var result = cmd.ExecuteScalar();
        if (result is long id) sub.Id = id;
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM subscriptions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
