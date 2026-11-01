using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParentControl.Core;

public static class Json
{
    // snake_case on the wire, matching the Postgres RPC contract.
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

public static class HeartbeatStates
{
    public const string Active = "active";
    public const string Idle = "idle";
    public const string Locked = "locked";
    public const string NoUser = "no_user";

    // Minutes in these states count towards the daily limit.
    public static bool IsCounted(string state) => state is Active or Idle;
}

/// <summary>One sampled minute of the console session.</summary>
public sealed record Heartbeat
{
    /// <summary>UTC time truncated to the minute; unique per device.</summary>
    public required DateTimeOffset Ts { get; init; }
    public required DateOnly LocalDate { get; init; }
    public required string State { get; init; }
    public string? Sid { get; init; }
    public string? User { get; init; }
    public string? Exe { get; init; }
    public string? Product { get; init; }
    public string? Title { get; init; }
}

public sealed record DeviceCredentials(Guid DeviceId, string Secret);

public sealed class GuardOptions
{
    public string SupabaseUrl { get; set; } = "";
    public string AnonKey { get; set; } = "";
    public string Schema { get; set; } = "parent_control";
    /// <summary>Data directory; defaults to %ProgramData%\ParentControl.</summary>
    public string? DataDir { get; set; }
}

// ---------------- device_sync request / response ----------------

public sealed record SyncRequest
{
    [JsonPropertyName("p_device_id")] public required Guid DeviceId { get; init; }
    [JsonPropertyName("p_secret")] public required string Secret { get; init; }
    [JsonPropertyName("p_client_version")] public required string ClientVersion { get; init; }
    [JsonPropertyName("p_client_now")] public required DateTimeOffset ClientNow { get; init; }
    [JsonPropertyName("p_local_date")] public required DateOnly LocalDate { get; init; }
    [JsonPropertyName("p_heartbeats")] public required IReadOnlyList<Heartbeat> Heartbeats { get; init; }
    [JsonPropertyName("p_acked_commands")] public required IReadOnlyList<long> AckedCommands { get; init; }
}

public sealed record SyncResponse
{
    public DateTimeOffset ServerNow { get; init; }
    public bool DeviceEnabled { get; init; } = true;
    public int Accepted { get; init; }
    public List<AccountInfo> Accounts { get; init; } = [];
    public List<ChildInfo> Children { get; init; } = [];
    public List<LockInfo> Locks { get; init; } = [];
    public List<MessageInfo> Messages { get; init; } = [];
}

public sealed record AccountInfo(string Sid, string? User, Guid? ChildId);

public sealed record ChildInfo(Guid ChildId, string Name, List<DayInfo> Days);

public sealed record DayInfo(
    DateOnly LocalDate,
    int? LimitMinutes,
    int BonusMinutes,
    string WindowFrom,
    string WindowTo,
    int SpentMinutes);

public sealed record LockInfo(long Id, Guid ChildId, DateTimeOffset LockFrom, DateTimeOffset? LockUntil);

public sealed record MessageInfo(long Id, Guid ChildId, string Text, DateTimeOffset CreatedAt);

public sealed record RegisterResult(
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("device_secret")] string DeviceSecret);
