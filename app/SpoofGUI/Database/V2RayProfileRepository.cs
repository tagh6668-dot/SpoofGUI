using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using SpoofGUI.Models;

namespace SpoofGUI.Database;

public sealed class V2RayProfileRepository
{
    private readonly DatabaseConnection _db;
    private readonly ConcurrentDictionary<long, string> _pings = new();
    public V2RayProfileRepository(DatabaseConnection db) => _db = db;

    public void RememberPing(long id, string ping)
    {
        if (id == 0) return;
        if (string.IsNullOrEmpty(ping)) _pings.TryRemove(id, out _);
        else _pings[id] = ping;
    }

    public IReadOnlyList<V2RayProfile> All()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, name, protocol, mode, address, port, user_id, security, transport, server_name, raw_uri, subscription_id, group_name
FROM v2ray_profiles ORDER BY updated_at DESC, id DESC;";
        var list = new List<V2RayProfile>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var profile = Map(r);
            profile.Ping = _pings.GetValueOrDefault(profile.Id, "");
            profile.LatencySummary = LatencySummary(profile.Id);
            list.Add(profile);
        }
        return list;
    }

    public void Upsert(V2RayProfile p)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO v2ray_profiles (id, name, protocol, mode, address, port, user_id, security, transport, server_name, raw_uri, subscription_id, group_name, updated_at)
VALUES ($id, $name, $protocol, $mode, $address, $port, $user_id, $security, $transport, $server_name, $raw_uri, $subscription_id, $group_name, datetime('now'))
ON CONFLICT(id) DO UPDATE SET
    name=excluded.name,
    protocol=excluded.protocol,
    mode=excluded.mode,
    address=excluded.address,
    port=excluded.port,
    user_id=excluded.user_id,
    security=excluded.security,
    transport=excluded.transport,
    server_name=excluded.server_name,
    raw_uri=excluded.raw_uri,
    subscription_id=excluded.subscription_id,
    group_name=excluded.group_name,
    updated_at=datetime('now');
SELECT CASE WHEN $id IS NULL THEN last_insert_rowid() ELSE $id END;";
        cmd.Parameters.AddWithValue("$id", p.Id == 0 ? DBNull.Value : p.Id);
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$protocol", p.Protocol);
        cmd.Parameters.AddWithValue("$mode", p.Mode);
        cmd.Parameters.AddWithValue("$address", p.Address);
        cmd.Parameters.AddWithValue("$port", p.Port);
        cmd.Parameters.AddWithValue("$user_id", p.UserId);
        cmd.Parameters.AddWithValue("$security", p.Security);
        cmd.Parameters.AddWithValue("$transport", p.Transport);
        cmd.Parameters.AddWithValue("$server_name", p.ServerName);
        cmd.Parameters.AddWithValue("$raw_uri", p.RawUri);
        cmd.Parameters.AddWithValue("$subscription_id", p.SubscriptionId);
        cmd.Parameters.AddWithValue("$group_name", p.GroupName);
        var result = cmd.ExecuteScalar();
        if (result is long id) p.Id = id;
    }

    public int DeleteBySubscription(long subscriptionId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM v2ray_profiles WHERE subscription_id = $sid;";
        cmd.Parameters.AddWithValue("$sid", subscriptionId);
        return cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM v2ray_profiles WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        _pings.TryRemove(id, out _);
    }

    public bool ExistsLike(V2RayProfile p)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT 1 FROM v2ray_profiles
WHERE ($raw_uri <> '' AND lower(raw_uri) = lower($raw_uri))
   OR (lower(protocol) = lower($protocol) AND lower(address) = lower($address) AND port = $port AND lower(user_id) = lower($user_id))
LIMIT 1;";
        cmd.Parameters.AddWithValue("$raw_uri", p.RawUri.Trim());
        cmd.Parameters.AddWithValue("$protocol", p.Protocol.Trim());
        cmd.Parameters.AddWithValue("$address", p.Address.Trim());
        cmd.Parameters.AddWithValue("$port", p.Port);
        cmd.Parameters.AddWithValue("$user_id", p.UserId.Trim());
        return cmd.ExecuteScalar() is not null;
    }

    public void RecordPing(long profileId, long latencyMs)
    {
        if (profileId == 0 || latencyMs <= 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO v2ray_ping_history (profile_id, latency_ms) VALUES ($id, $ms);";
            cmd.Parameters.AddWithValue("$id", profileId);
            cmd.Parameters.AddWithValue("$ms", latencyMs);
            cmd.ExecuteNonQuery();
        }
        using (var trim = conn.CreateCommand())
        {
            trim.Transaction = tx;
            trim.CommandText = @"
DELETE FROM v2ray_ping_history
WHERE profile_id = $id
  AND id NOT IN (
      SELECT id FROM v2ray_ping_history
      WHERE profile_id = $id
      ORDER BY created_at DESC, id DESC
      LIMIT 20
  );";
            trim.Parameters.AddWithValue("$id", profileId);
            trim.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public string LatencySummary(long profileId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(*), MIN(latency_ms), AVG(latency_ms)
FROM v2ray_ping_history
WHERE profile_id = $id;";
        cmd.Parameters.AddWithValue("$id", profileId);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0) || r.GetInt64(0) == 0) return "";
        var count = r.GetInt64(0);
        var best = r.GetInt64(1);
        var avg = (long)Math.Round(r.GetDouble(2));
        return $"avg {avg} ms · best {best} ms · {count}/20";
    }

    private static V2RayProfile Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Protocol = r.GetString(2),
        Mode = r.GetString(3),
        Address = r.GetString(4),
        Port = (int)r.GetInt64(5),
        UserId = r.GetString(6),
        Security = r.GetString(7),
        Transport = r.GetString(8),
        ServerName = r.GetString(9),
        RawUri = r.GetString(10),
        SubscriptionId = r.GetInt64(11),
        GroupName = r.GetString(12),
    };
}
