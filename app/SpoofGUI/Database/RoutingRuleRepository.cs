using Microsoft.Data.Sqlite;
using SpoofGUI.Models;

namespace SpoofGUI.Database;

public sealed class RoutingRuleRepository
{
    private readonly DatabaseConnection _db;
    public RoutingRuleRepository(DatabaseConnection db) => _db = db;

    public IReadOnlyList<RoutingRule> All()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, kind, pattern, outbound, enabled, sort_order
FROM routing_rules ORDER BY sort_order ASC, id ASC;";
        var list = new List<RoutingRule>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RoutingRule
            {
                Id = r.GetInt64(0),
                Kind = r.GetString(1),
                Pattern = r.GetString(2),
                Outbound = r.GetString(3),
                Enabled = r.GetInt64(4) != 0,
                SortOrder = (int)r.GetInt64(5),
            });
        }
        return list;
    }

    public IReadOnlyList<RoutingRule> Enabled() => All().Where(r => r.Enabled).ToList();

    public void Upsert(RoutingRule rule)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO routing_rules (id, kind, pattern, outbound, enabled, sort_order)
VALUES ($id, $kind, $pattern, $outbound, $enabled, $sort_order)
ON CONFLICT(id) DO UPDATE SET
    kind=excluded.kind,
    pattern=excluded.pattern,
    outbound=excluded.outbound,
    enabled=excluded.enabled,
    sort_order=excluded.sort_order;
SELECT CASE WHEN $id IS NULL THEN last_insert_rowid() ELSE $id END;";
        cmd.Parameters.AddWithValue("$id", rule.Id == 0 ? DBNull.Value : rule.Id);
        cmd.Parameters.AddWithValue("$kind", rule.Kind);
        cmd.Parameters.AddWithValue("$pattern", rule.Pattern.Trim());
        cmd.Parameters.AddWithValue("$outbound", rule.Outbound);
        cmd.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort_order", rule.SortOrder);
        var result = cmd.ExecuteScalar();
        if (result is long id) rule.Id = id;
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM routing_rules WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
