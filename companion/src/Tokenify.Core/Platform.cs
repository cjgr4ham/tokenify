// Child-process reading is adapted from rossalv/fortnite-chest-counter (MIT, src/ChestCounter/ReplayWorker.cs);
// efficiency mode from LED-N/Fortnite-ChestCounter (MIT, src/ChestCounter/EcoQos.cs).
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Tokenify.ReplayReader;

namespace Tokenify.Core;

/// <summary>Result of one TOKENIFY API call.</summary>
/// <param name="Error">The server's error code (e.g. "device_revoked"), or "network" / "server" for transport problems.</param>
public sealed record ApiResult(bool Ok, string? Error, JsonObject? Body)
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
public sealed class ApiClient : IDisposable
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
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TOKENIFY-Tracker", AppInfo.Version));
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
                Log.Warn($"{function}: HTTP {(int)response.StatusCode} {(text.Length > 160 ? text[..160] : text)}");
                // An RPC the server does not have yet (an older backend): reported as such so callers can skip it.
                if (response.StatusCode == HttpStatusCode.NotFound) return new ApiResult(false, "unsupported", null);
                return new ApiResult(false, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "unauthorized" : "server", null);
            }

            if (node is not JsonObject obj) return new ApiResult(false, "server", null);
            var ok = ApiResult.Bool(obj["ok"]);
            return new ApiResult(ok, ok ? null : ApiResult.Text(obj["error"]) ?? "server", obj);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return new ApiResult(false, "network", null);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// This PC's TOKENIFY device id and secret. The secret is encrypted with Windows DPAPI for the current Windows user
/// (another account on the PC, or a copy of the file on another PC, cannot decrypt it). It only allows uploading
/// match summaries for the linked account, and the website can revoke it at any time.
/// </summary>
public static class Credentials
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TOKENIFY Companion device secret v1");

    public sealed record Device(Guid Id, string Secret);

    public static Device? Load(Store store)
    {
        if (!Guid.TryParse(store.Get("device.id"), out var id) || store.Get("device.secret") is not { Length: > 0 } blob) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser);
            return new Device(id, Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Warn("The saved TOKENIFY connection could not be decrypted on this Windows account; please reconnect.");
            return null;
        }
    }

    public static void Save(Store store, Guid id, string secret)
    {
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        store.Set("device.id", id.ToString());
        store.Set("device.secret", Convert.ToBase64String(blob));
    }

    public static void Clear(Store store)
    {
        store.Set("device.id", null);
        store.Set("device.secret", null);
        store.Set("account.username", null);
        store.Set("device.name", null);
    }
}

/// <summary>
/// Reads one replay in a separate, short-lived, low-priority copy of this exe ("TOKENIFY-Tracker.exe
/// --read-replay file"). A replay the parser can't handle is then stopped after a time limit instead of hanging
/// the app, its memory is capped, and everything it used goes back to Windows as soon as the replay is done.
/// </summary>
public static class ReplayWorker
{
    public const string Arg = "--read-replay";
    /// <summary>
    /// Start of the failure text when the reader could not even be started (the program file was moved, renamed or
    /// blocked while the tracker was running). That says nothing about the replay, so it is never held against it.
    /// </summary>
    public const string UnavailablePrefix = "the replay reader could not be started: ";
    private const string WatchVariable = "TOKENIFY_READER_LIMIT_S";
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };
    /// <summary>Most the reader may allocate. A real replay needs a small fraction of this.</summary>
    private const string MemoryLimit = "0x50000000"; // 1.25 GB
    private static readonly object Gate = new();
    private static Process? _running;

    /// <summary>
    /// Child side: never outlive the tracker. The tracker keeps the child's standard input open and never writes
    /// to it, so the read below returns only when the tracker is gone (exit, update, crash); and the child has
    /// its own time limit, a little after the tracker's. Either way a reader stuck on a replay ends by itself.
    /// </summary>
    private static void StartWatchdog()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(WatchVariable), out var seconds) || seconds <= 0) return;
        var limit = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds + 20));
            Environment.Exit(4);
        }) { IsBackground = true, Name = "reader time limit" };
        limit.Start();
        var parent = new Thread(() =>
        {
            try
            {
                using var stdin = Console.OpenStandardInput();
                var one = new byte[1];
                while (stdin.Read(one, 0, 1) > 0) { }
            }
            catch (Exception)
            {
                // a broken pipe means the same thing as its end
            }
            Environment.Exit(5);
        }) { IsBackground = true, Name = "reader parent watch" };
        parent.Start();
    }

    /// <summary>Stops a read in progress (the tracker is exiting).</summary>
    public static void KillRunning()
    {
        Process? child;
        lock (Gate) { child = _running; }
        try { child?.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException or NotSupportedException) { }
    }

    /// <summary>Child entry point: parse, write the result as JSON on stdout.</summary>
    public static int Run(string path)
    {
        try
        {
            StartWatchdog();
            EcoQos.Enable();
            var result = new ChestReader().Read(path);
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(JsonSerializer.SerializeToUtf8Bytes(result, Json));
            stdout.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                using var stderr = Console.OpenStandardError();
                stderr.Write(Encoding.UTF8.GetBytes(ex.GetType().Name + ": " + ex.Message));
            }
            catch
            {
                // nothing else to do
            }
            return 2;
        }
    }

    /// <summary>Parent side. Returns null with a reason when the child failed or ran out of time.</summary>
    public static ScanResult? Read(string path, TimeSpan timeout, out string? failure)
    {
        failure = null;
        // The tracker itself (whatever its file is called) reads in a child process; the test runner and the
        // command-line tools read in-process.
        if (!AppInfo.IsTrackerApp || Environment.ProcessPath is not { } exe)
        {
            return new ChestReader().Read(path);
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,    // held open, never written: its end tells the child the tracker is gone
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(Arg);
        psi.ArgumentList.Add(path);
        psi.Environment["DOTNET_GCHeapHardLimit"] = MemoryLimit;
        psi.Environment[WatchVariable] = ((int)Math.Ceiling(timeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        Process? child;
        try
        {
            child = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            failure = UnavailablePrefix + ex.Message;
            return null;
        }
        if (child is null)
        {
            failure = UnavailablePrefix + "Windows did not start it";
            return null;
        }

        using (child)
        {
            lock (Gate) { _running = child; }
            try
            {
                try { child.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }

                using var output = new MemoryStream();
                var copyOut = child.StandardOutput.BaseStream.CopyToAsync(output);
                var readErr = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(timeout))
                {
                    try { child.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
                    failure = $"took longer than {timeout.TotalSeconds:0} s to read";
                    return null;
                }

                child.WaitForExit();
                try { copyOut.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { /* the pipe broke: handled as an empty result below */ }
                if (child.ExitCode != 0 || output.Length == 0)
                {
                    var err = "";
                    try { err = readErr.Wait(TimeSpan.FromSeconds(2)) ? readErr.Result : ""; } catch (AggregateException) { }
                    failure = $"replay reader failed ({child.ExitCode}): {(err.Length > 200 ? err[..200] : err)}";
                    return null;
                }

                return JsonSerializer.Deserialize<ScanResult>(output.ToArray(), Json);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                failure = "unreadable result from the replay reader: " + ex.Message;
                return null;
            }
            finally
            {
                lock (Gate) { _running = null; }
            }
        }
    }
}

/// <summary>Windows 10 1709+/11 power throttling ("efficiency mode") for this program's own process.</summary>
public static class EcoQos
{
    private const int ProcessPowerThrottling = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static void Enable()
    {
        try
        {
            var state = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 1 };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch
        {
            // older Windows: run normally
        }
    }
}

/// <summary>"Start with Windows": a normal per-user Run entry (no administrator rights, no service, nothing
/// that touches Fortnite). The tracker then starts quietly in the notification area.</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TOKENIFY Chest Tracker";
    private const string LegacyValueName = "TOKENIFY Companion";

    /// <summary>A test copy (pointed at a test server or a test data folder) never registers itself to start with
    /// Windows: started at sign-in it would run without its test settings, as if it were a real install.</summary>
    public static bool Available => !AppInfo.TestCopy;

    public static bool IsEnabled()
    {
        if (!Available) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && v.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the first version (TOKENIFY Companion) was set to start with Windows.</summary>
    public static bool LegacyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(LegacyValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        if (!Available)
        {
            Log.Info("Start with Windows is not available for a test copy; nothing changed");
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            // The tracker replaces the Companion: only one of them may start with Windows.
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            if (enabled && Environment.ProcessPath is { } exe)
            {
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not change Start with Windows", ex);
        }
    }
}
