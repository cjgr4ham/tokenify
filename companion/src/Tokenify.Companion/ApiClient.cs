using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tokenify.Companion;

/// <summary>Result of one TOKENIFY API call.</summary>
/// <param name="Error">The server's error code (e.g. "device_revoked"), or "network" / "server" for transport problems.</param>
internal sealed record ApiResult(bool Ok, string? Error, JsonObject? Body)
{
    public bool Network => Error is "network" or "server" or "rate_limited";
    public string? Str(string name) => Text(Body?[name]);

    public static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    public static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

/// <summary>
/// Talks to the TOKENIFY backend over HTTPS (Supabase PostgREST RPC). Every call except pairing carries this PC's
/// device id + secret; nothing else identifies the user or the PC.
/// </summary>
internal sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;

    public ApiClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TOKENIFY-Companion", AppInfo.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("apikey", AppInfo.ApiKey);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AppInfo.ApiKey);
    }

    public async Task<ApiResult> Call(string function, JsonObject args, CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent(args.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{AppInfo.ApiUrl}/rest/v1/rpc/{function}", content, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Log.Warn($"{function}: server returned {(int)response.StatusCode}");
                return new ApiResult(false, response.StatusCode == HttpStatusCode.TooManyRequests ? "rate_limited" : "server", null);
            }

            JsonNode? node = null;
            try { node = JsonNode.Parse(text); } catch (JsonException) { }
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"{function}: HTTP {(int)response.StatusCode} {Short(text)}");
                return new ApiResult(false, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "unauthorized" : "server", null);
            }

            if (node is not JsonObject obj)
            {
                return new ApiResult(false, "server", null);
            }

            var ok = ApiResult.Bool(obj["ok"]);
            return new ApiResult(ok, ok ? null : ApiResult.Text(obj["error"]) ?? "server", obj);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return new ApiResult(false, "network", null);
        }
    }

    private static string Short(string s) => s.Length > 160 ? s[..160] : s;

    public void Dispose() => _http.Dispose();
}
