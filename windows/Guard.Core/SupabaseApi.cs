using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ParentControl.Core;

public interface IGuardApi
{
    Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken ct);
    Task<RegisterResult> RegisterAsync(string code, string machineName, string clientVersion, CancellationToken ct);
}

/// <summary>The server rejected the device secret or registration code (SQLSTATE 28000).</summary>
public sealed class DeviceUnauthorizedException(string message) : Exception(message);

/// <summary>Any other failure talking to the server (network, 5xx, bad payload).</summary>
public sealed class ApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Calls the parent_control RPCs through Supabase PostgREST.</summary>
public sealed class SupabaseApi : IGuardApi
{
    private readonly HttpClient _http;
    private readonly GuardOptions _options;

    public SupabaseApi(HttpClient http, GuardOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SupabaseUrl) || string.IsNullOrWhiteSpace(options.AnonKey))
        {
            throw new ArgumentException("SupabaseUrl and AnonKey must be configured");
        }
        _http = http;
        _options = options;
        if (_http.Timeout > TimeSpan.FromSeconds(30)) _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken ct) =>
        PostRpcAsync<SyncRequest, SyncResponse>("device_sync", request, ct);

    public async Task<RegisterResult> RegisterAsync(string code, string machineName, string clientVersion, CancellationToken ct)
    {
        var body = new Dictionary<string, string>
        {
            ["p_code"] = code,
            ["p_machine_name"] = machineName,
            ["p_client_version"] = clientVersion,
        };
        var rows = await PostRpcAsync<Dictionary<string, string>, List<RegisterResult>>("device_register", body, ct);
        return rows.Count == 1 ? rows[0] : throw new ApiException("device_register returned no row");
    }

    private async Task<TResponse> PostRpcAsync<TRequest, TResponse>(string fn, TRequest body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.SupabaseUrl.TrimEnd('/')}/rest/v1/rpc/{fn}")
        {
            Content = JsonContent.Create(body, options: Json.Options),
        };
        req.Headers.TryAddWithoutValidation("apikey", _options.AnonKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AnonKey);
        // PostgREST picks the schema from these headers (schema must be exposed in Supabase).
        req.Headers.TryAddWithoutValidation("Content-Profile", _options.Schema);
        req.Headers.TryAddWithoutValidation("Accept-Profile", _options.Schema);

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ApiException($"network: {ex.Message}", ex);
        }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
            {
                try
                {
                    return JsonSerializer.Deserialize<TResponse>(text, Json.Options)
                           ?? throw new ApiException($"{fn}: empty response");
                }
                catch (JsonException ex)
                {
                    throw new ApiException($"{fn}: bad response: {ex.Message}", ex);
                }
            }

            var (code, message) = ParseError(text);
            if (code == "28000" || res.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new DeviceUnauthorizedException($"{fn}: {message}");
            }
            throw new ApiException($"{fn}: HTTP {(int)res.StatusCode} {code} {message}".Trim());
        }
    }

    // PostgREST error body: {"code":"28000","message":"...","details":null,"hint":null}
    internal static (string? Code, string Message) ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.ToString() : null;
            var msg = root.TryGetProperty("message", out var m) ? m.ToString() : body;
            return (code, msg);
        }
        catch (JsonException)
        {
            return (null, body.Length > 200 ? body[..200] : body);
        }
    }
}
