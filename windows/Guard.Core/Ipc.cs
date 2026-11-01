using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParentControl.Core;

/// <summary>
/// Service ↔ tray protocol over a local named pipe: one JSON object per line.
/// The tray only displays and reports; every decision is made by the service.
/// </summary>
public static class Ipc
{
    public const string PipeName = "ParentControlGuard";

    public static readonly JsonSerializerOptions Options = new(Json.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IpcMessage m) => JsonSerializer.Serialize(m, Options);

    public static IpcMessage? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<IpcMessage>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class IpcTypes
{
    // tray -> service
    public const string Hello = "hello";
    public const string Activity = "activity";
    public const string RequestTime = "request_time";
    public const string AckNotice = "ack_notice";

    // service -> tray
    public const string Status = "status";
    public const string Warn = "warn";
    public const string Lock = "lock";
    public const string Notice = "notice";
}

/// <summary>Flat envelope: <see cref="Type"/> says which fields are set.</summary>
public sealed record IpcMessage
{
    public required string Type { get; init; }

    // hello
    public string? Version { get; init; }

    // activity
    public string? Exe { get; init; }
    public string? Product { get; init; }
    public string? Title { get; init; }
    public int? IdleSeconds { get; init; }

    // request_time
    public int? Minutes { get; init; }
    public string? Reason { get; init; }

    // status
    public TrayStatus? Status { get; init; }

    // warn
    public int? MinutesLeft { get; init; }

    // lock / notice
    public string? Text { get; init; }
    public int? Seconds { get; init; }
    public bool? CanRequest { get; init; }

    // notice / ack_notice: "message:<id>" or "request:<id>" or "info"
    public string? NoticeId { get; init; }
}

/// <summary>Snapshot shown in the tray icon and status window.</summary>
public sealed record TrayStatus
{
    public bool Restricted { get; init; }
    public string? ChildName { get; init; }
    public string? Lock { get; init; }
    public int? RemainingMinutes { get; init; }
    /// <summary>"limit", "window" or "lock": what ends the time first.</summary>
    public string? RemainingBy { get; init; }
    public int SpentMinutes { get; init; }
    public int? AllowedMinutes { get; init; }
    public string? WindowFrom { get; init; }
    public string? WindowTo { get; init; }
    public bool Online { get; init; }
    public bool StaleSettings { get; init; }
    /// <summary>Open request: minutes asked; null if none.</summary>
    public int? RequestMinutes { get; init; }
    public bool RequestUploaded { get; init; }
}
