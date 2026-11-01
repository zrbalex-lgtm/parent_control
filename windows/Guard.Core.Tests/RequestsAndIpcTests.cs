using Microsoft.Extensions.Logging.Abstractions;
using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class RequestsAndIpcTests : IDisposable
{
    private static readonly Guid Misha = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero));
    private readonly FakeApi _api = new();
    private readonly GuardEngine _engine;

    public RequestsAndIpcTests()
    {
        var creds = new CredentialStore(_dir.File("device.json"));
        creds.Save(new DeviceCredentials(Guid.NewGuid(), "s"));
        _engine = new GuardEngine(_api, new HeartbeatQueue(_dir.File("q.jsonl")), new StateStore(_dir.File("state.json")),
            creds, _time, NullLogger<GuardEngine>.Instance, "test");
    }

    public void Dispose() => _dir.Dispose();

    private SyncResponse Server(List<RequestInfo>? requests = null, List<MessageInfo>? messages = null) => new()
    {
        Accounts = [new AccountInfo("S-1", "misha", Misha), new AccountInfo("S-PAPA", "papa", null)],
        Children = [new ChildInfo(Misha, "Миша", [new DayInfo(new DateOnly(2026, 11, 1), 60, 0, "00:00", "24:00", 60)])],
        Requests = requests ?? [],
        Messages = messages ?? [],
    };

    private static RequestInfo Req(long id, string status, int? granted = null) =>
        new(id, Guid.NewGuid(), Misha, 30, granted, status, DateTimeOffset.UtcNow, status == RequestStatuses.Pending ? null : DateTimeOffset.UtcNow);

    [Fact]
    public async Task Request_is_uploaded_once_then_tracked_as_pending_on_server()
    {
        _api.Respond = _ => Server();
        await _engine.SyncAsync(default);

        Assert.Equal(RequestTimeResult.Queued, _engine.RequestTime("S-1", 30, "  доиграть  "));
        Assert.Equal(new OpenRequest(30, Uploaded: false), _engine.OpenRequestFor(Misha));
        Assert.Equal(RequestTimeResult.AlreadyPending, _engine.RequestTime("S-1", 15, null));

        _api.Respond = r => Server([Req(7, RequestStatuses.Pending)]);
        await _engine.SyncAsync(default);
        var sent = Assert.Single(_api.Requests[^1].TimeRequests);
        Assert.Equal("доиграть", sent.Reason);
        Assert.Equal(new OpenRequest(30, Uploaded: true), _engine.OpenRequestFor(Misha));

        await _engine.SyncAsync(default);
        Assert.Empty(_api.Requests[^1].TimeRequests);
        Assert.Equal(RequestTimeResult.AlreadyPending, _engine.RequestTime("S-1", 15, null));
    }

    [Fact]
    public async Task Request_survives_offline_and_restart()
    {
        _api.Respond = _ => Server();
        await _engine.SyncAsync(default);
        _api.Throw = new ApiException("offline");
        _engine.RequestTime("S-1", 60, null);
        await _engine.SyncAsync(default);

        var restarted = new GuardEngine(_api, new HeartbeatQueue(_dir.File("q.jsonl")), new StateStore(_dir.File("state.json")),
            new CredentialStore(_dir.File("device.json")), _time, NullLogger<GuardEngine>.Instance, "test");
        Assert.Equal(new OpenRequest(60, Uploaded: false), restarted.OpenRequestFor(Misha));
    }

    [Fact]
    public async Task Unbound_account_cannot_request()
    {
        _api.Respond = _ => Server();
        await _engine.SyncAsync(default);
        Assert.Equal(RequestTimeResult.NotAllowed, _engine.RequestTime("S-PAPA", 30, null));
        Assert.Equal(RequestTimeResult.NotAllowed, _engine.RequestTime(null, 30, null));
    }

    [Fact]
    public async Task Decided_request_is_notified_once()
    {
        _api.Respond = _ => Server([Req(7, RequestStatuses.Approved, granted: 20)]);
        await _engine.SyncAsync(default);
        var r = Assert.Single(_engine.UnnotifiedResults(Misha));
        Assert.Equal("Добавлено 20 мин.", Texts.RequestResult(r));
        _engine.MarkRequestNotified(7);
        Assert.Empty(_engine.UnnotifiedResults(Misha));
        Assert.Null(_engine.OpenRequestFor(Misha));
    }

    [Fact]
    public async Task Message_is_shown_once_and_acked_on_next_sync()
    {
        _api.Respond = _ => Server(messages: [new MessageInfo(13, Misha, "Ужинать!", DateTimeOffset.UtcNow)]);
        await _engine.SyncAsync(default);
        Assert.Single(_engine.UnshownMessages(Misha));
        _engine.MarkMessageShown(13);
        Assert.Empty(_engine.UnshownMessages(Misha));

        await _engine.SyncAsync(default);
        Assert.Equal(new long[] { 13 }, _api.Requests[^1].AckedCommands);
    }

    [Fact]
    public async Task Unlock_request_is_uploaded_with_kind_and_reported()
    {
        _api.Respond = _ => Server();
        await _engine.SyncAsync(default);
        Assert.Equal(RequestTimeResult.Queued, _engine.RequestTime("S-1", 0, "уроки сделал", RequestKinds.Unlock));
        Assert.Equal(RequestKinds.Unlock, _engine.OpenRequestFor(Misha)!.Kind);

        _api.Respond = _ => Server([new RequestInfo(9, Guid.NewGuid(), Misha, 5, null, RequestStatuses.Approved,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, RequestKinds.Unlock)]);
        await _engine.SyncAsync(default);
        Assert.Equal(RequestKinds.Unlock, Assert.Single(_api.Requests[^1].TimeRequests).Kind);
        Assert.Equal("Доступ на сегодня открыт.", Texts.RequestResult(Assert.Single(_engine.UnnotifiedResults(Misha))));
    }

    [Fact]
    public void Ipc_roundtrip_omits_nulls()
    {
        var line = Ipc.Serialize(new IpcMessage
        {
            Type = IpcTypes.Status,
            Status = new TrayStatus { Restricted = true, ChildName = "Миша", RemainingMinutes = 42, Online = true },
        });
        Assert.DoesNotContain("null", line);
        Assert.DoesNotContain("\n", line);
        var back = Ipc.Parse(line)!;
        Assert.Equal(IpcTypes.Status, back.Type);
        Assert.Equal(42, back.Status!.RemainingMinutes);
        Assert.Null(Ipc.Parse("{garbage"));
    }

    [Fact]
    public void Remaining_text()
    {
        Assert.Equal("0:42", Texts.Remaining(42));
        Assert.Equal("2:05", Texts.Remaining(125));
        Assert.Equal("без ограничений", Texts.Remaining(null));
    }
}
