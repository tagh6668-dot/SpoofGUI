using System.Text.Json;
using SpoofGUI.Database;
using SpoofGUI.Models;

namespace SpoofGUI.Core;

public sealed class ProfileBackupService
{
    private readonly ProfileRepository _spoof;
    private readonly V2RayProfileRepository _v2ray;

    public ProfileBackupService(ProfileRepository spoof, V2RayProfileRepository v2ray)
    {
        _spoof = spoof;
        _v2ray = v2ray;
    }

    private sealed record Backup(int Version, List<SpoofDto> Spoof, List<V2RayDto> V2Ray);
    private sealed record SpoofDto(string Name, string ListenHost, int ListenPort, string ConnectIp, int ConnectPort, string FakeSni);
    private sealed record V2RayDto(string Name, string Protocol, string Mode, string Address, int Port, string UserId, string Security, string Transport, string ServerName, string RawUri);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ExportJson()
    {
        var backup = new Backup(
            1,
            _spoof.All().Select(p => new SpoofDto(p.Name, p.ListenHost, p.ListenPort, p.ConnectIp, p.ConnectPort, p.FakeSni)).ToList(),
            _v2ray.All().Select(p => new V2RayDto(p.Name, p.Protocol, p.Mode, p.Address, p.Port, p.UserId, p.Security, p.Transport, p.ServerName, p.RawUri)).ToList());
        return JsonSerializer.Serialize(backup, Options);
    }

    public (int Spoof, int V2Ray) ImportJson(string json)
    {
        var backup = JsonSerializer.Deserialize<Backup>(json) ?? throw new InvalidOperationException("not a SpoofGUI backup file");

        var hadActive = _spoof.GetActive() is not null;
        var spoof = 0;
        foreach (var d in backup.Spoof ?? [])
        {
            _spoof.Upsert(new SpoofProfile
            {
                Name = d.Name,
                ListenHost = d.ListenHost,
                ListenPort = d.ListenPort,
                ConnectIp = d.ConnectIp,
                ConnectPort = d.ConnectPort,
                FakeSni = d.FakeSni,
                IsActive = false,
            });
            spoof++;
        }

        if (!hadActive && _spoof.GetActive() is null && _spoof.All().FirstOrDefault() is { } first)
            _spoof.SetActive(first.Id);

        var v2ray = 0;
        foreach (var d in backup.V2Ray ?? [])
        {
            _v2ray.Upsert(new V2RayProfile
            {
                Name = d.Name,
                Protocol = d.Protocol,
                Mode = d.Mode,
                Address = d.Address,
                Port = d.Port,
                UserId = d.UserId,
                Security = d.Security,
                Transport = d.Transport,
                ServerName = d.ServerName,
                RawUri = d.RawUri,
            });
            v2ray++;
        }

        return (spoof, v2ray);
    }
}
