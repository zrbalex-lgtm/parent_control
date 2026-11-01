using System.Text.Json;
using ParentControl.Core;

namespace ParentControl.Core.Tests;

/// <summary>Wire format must match the Postgres RPC contract in db/README.md.</summary>
public class JsonContractTests
{
    [Fact]
    public void Sync_request_uses_rpc_parameter_names()
    {
        var req = new SyncRequest
        {
            DeviceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            Secret = "s",
            ClientVersion = "0.1.0",
            ClientNow = new DateTimeOffset(2026, 11, 1, 14, 5, 0, TimeSpan.Zero),
            LocalDate = new DateOnly(2026, 11, 1),
            Heartbeats =
            [
                new Heartbeat
                {
                    Ts = new DateTimeOffset(2026, 11, 1, 14, 5, 0, TimeSpan.Zero),
                    LocalDate = new DateOnly(2026, 11, 1),
                    State = "active", Sid = "S-1-5-21-1", User = "misha", Exe = "javaw.exe",
                },
            ],
            AckedCommands = [13],
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(req, Json.Options));
        var root = doc.RootElement;
        foreach (var name in new[] { "p_device_id", "p_secret", "p_client_version", "p_client_now", "p_local_date", "p_heartbeats", "p_acked_commands" })
        {
            Assert.True(root.TryGetProperty(name, out _), name);
        }
        Assert.Equal("2026-11-01", root.GetProperty("p_local_date").GetString());
        var hb = root.GetProperty("p_heartbeats")[0];
        Assert.Equal("2026-11-01T14:05:00+00:00", hb.GetProperty("ts").GetString());
        Assert.Equal("2026-11-01", hb.GetProperty("local_date").GetString());
        Assert.Equal("S-1-5-21-1", hb.GetProperty("sid").GetString());
        Assert.Equal("misha", hb.GetProperty("user").GetString());
        Assert.Equal("javaw.exe", hb.GetProperty("exe").GetString());
        Assert.Equal(13, root.GetProperty("p_acked_commands")[0].GetInt64());
    }

    [Fact]
    public void Sync_response_parses_server_payload()
    {
        // Shape produced by parent_control.device_sync
        const string payload = """
        {
          "server_now": "2026-11-01T14:05:01.123456+00:00",
          "device_enabled": true,
          "accepted": 3,
          "accounts": [
            {"sid": "S-1-5-21-1", "user": "misha", "child_id": "11111111-1111-1111-1111-111111111111"},
            {"sid": "S-1-5-21-2", "user": "papa", "child_id": null}
          ],
          "children": [
            {"child_id": "11111111-1111-1111-1111-111111111111", "name": "Миша", "days": [
              {"local_date": "2026-11-01", "limit_minutes": 120, "bonus_minutes": 30,
               "window_from": "15:00", "window_to": "20:00", "spent_minutes": 47},
              {"local_date": "2026-11-02", "limit_minutes": null, "bonus_minutes": 0,
               "window_from": "00:00", "window_to": "24:00", "spent_minutes": 0,
               "requires_unlock": true, "unlocked": false}
            ]}
          ],
          "locks": [{"id": 12, "child_id": "11111111-1111-1111-1111-111111111111",
                     "lock_from": "2026-11-01T14:00:00+00:00", "lock_until": null}],
          "messages": [{"id": 13, "child_id": "11111111-1111-1111-1111-111111111111",
                        "text": "Ужинать!", "created_at": "2026-11-01T14:04:00+00:00"}]
        }
        """;
        var r = JsonSerializer.Deserialize<SyncResponse>(payload, Json.Options)!;
        Assert.True(r.DeviceEnabled);
        Assert.Equal(3, r.Accepted);
        Assert.Equal(2, r.Accounts.Count);
        Assert.Null(r.Accounts[1].ChildId);
        var day = r.Children[0].Days[0];
        Assert.Equal(new DateOnly(2026, 11, 1), day.LocalDate);
        Assert.Equal(120, day.LimitMinutes);
        Assert.Equal(30, day.BonusMinutes);
        Assert.Equal("15:00", day.WindowFrom);
        Assert.Equal(47, day.SpentMinutes);
        Assert.Null(r.Children[0].Days[1].LimitMinutes);
        Assert.Equal("24:00", r.Children[0].Days[1].WindowTo);
        Assert.False(r.Children[0].Days[0].RequiresUnlock); // absent in older payloads
        Assert.True(r.Children[0].Days[1].RequiresUnlock);
        Assert.Null(r.Locks[0].LockUntil);
        Assert.Equal("Ужинать!", r.Messages[0].Text);
    }

    [Fact]
    public void Disabled_device_payload_parses()
    {
        var r = JsonSerializer.Deserialize<SyncResponse>(
            """{"server_now":"2026-11-01T14:05:01+00:00","device_enabled":false}""", Json.Options)!;
        Assert.False(r.DeviceEnabled);
        Assert.Empty(r.Children);
    }

    [Fact]
    public void Register_result_parses()
    {
        var rows = JsonSerializer.Deserialize<List<RegisterResult>>(
            """[{"device_id":"dddddddd-dddd-dddd-dddd-dddddddddddd","device_secret":"abc"}]""", Json.Options)!;
        Assert.Equal("abc", rows[0].DeviceSecret);
    }

    [Fact]
    public void PostgREST_error_body_is_parsed()
    {
        var (code, msg) = SupabaseApi.ParseError("""{"code":"28000","details":null,"hint":null,"message":"unauthorized"}""");
        Assert.Equal("28000", code);
        Assert.Equal("unauthorized", msg);
    }

    [Fact]
    public void State_roundtrips_with_usage_dictionary()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.File("state.json"));
        var s = new GuardState();
        s.AddUsage(new DateOnly(2026, 11, 1), "S-1", new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero));
        s.AddUsage(new DateOnly(2026, 11, 1), "S-1", new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero));
        s.AddUsage(new DateOnly(2026, 11, 1), "S-2", new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero));
        store.Save(s);
        var back = store.Load();
        Assert.Equal(1, back.LocalMinutes(new DateOnly(2026, 11, 1), new HashSet<string> { "S-1", "S-2" }));
    }

    [Fact]
    public void Corrupt_state_starts_clean()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("state.json"), "{not json");
        var s = new StateStore(dir.File("state.json")).Load();
        Assert.Null(s.LastSync);
        Assert.True(File.Exists(dir.File("state.json.corrupt")));
    }
}
