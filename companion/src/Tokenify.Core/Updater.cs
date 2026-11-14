using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Tokenify.Core;

public sealed record UpdateInfo(string Version, string Notes, string ExeUrl, string SignatureUrl, long Size, string PageUrl);

/// <summary>What an update check found. <see cref="Succeeded"/> false means GitHub could not be asked (offline,
/// rate limit): that is not the same as "nothing newer", and an update found earlier is then kept.</summary>
public sealed record UpdateCheck(bool Succeeded, UpdateInfo? Update);

/// <summary>
/// Updates, on the player's click only. The tracker asks GitHub whether a newer release exists (this can be
/// switched off entirely). A download is installed only if its signature matches the key built into this
/// program: a file that was not signed with TOKENIFY's release key is deleted and never run. HTTPS throughout,
/// the local database is not touched, and the previous exe is kept next to the new one until the new one has
/// started properly.
/// </summary>
public static class Updater
{
    /// <summary>The release-signing public key (ECDSA P-256, SubjectPublicKeyInfo, base64).</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEo4maz+Ke3Pn57d+RiEymYYoiTgcwI1f/7CWO6oErgbUhPeT7NJ745M+88ygb1K+nMNPYXKnGIX/JA9kpuo8Pdw==";

    private const string TagPrefix = "tracker-v";
    private const long MaxBytes = 300L * 1024 * 1024;
    /// <summary>A download that delivers nothing for this long has stalled (sleep, a dropped connection).</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    /// <summary>Always three parts: "2.1" and "2.1.0" are the same release, and "2.1.0.7" is 2.1.0.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>The version of this program, as three parts.</summary>
    public static Version CurrentVersion => Version.TryParse(AppInfo.Version, out var v) ? Normalize(v) : new Version(0, 0, 0);

    /// <summary>The version written into a program file by its build (null when it has none).</summary>
    public static Version? VersionOf(string file)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            return info.FileVersion is null ? null : new Version(Math.Max(info.FileMajorPart, 0), Math.Max(info.FileMinorPart, 0), Math.Max(info.FileBuildPart, 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The signature proves a file is a genuine TOKENIFY build, not which one. So the build's own version has to be
    /// the one the release claims, and newer than the one running: otherwise an old, genuinely signed build offered
    /// under a higher release number would be installed, and would then be offered the same "update" for ever.
    /// </summary>
    public static bool AcceptableVersion(Version? inFile, Version offered, Version running) =>
        inFile is not null && Normalize(inFile) == Normalize(offered) && Normalize(inFile) > Normalize(running);

    /// <summary>Whether this copy can replace its own program file (and why not).</summary>
    public static bool CanApply(out string reason)
    {
        reason = "";
        if (AppInfo.IsTrackerApp && Environment.ProcessPath is { } path && File.Exists(path)) return true;
        reason = "This copy of the tracker can't replace its own file (it was moved or renamed while running, or is a test build). Download the new version from the website instead.";
        return false;
    }
    private static readonly string[] AllowedHosts = ["github.com", "githubusercontent.com"];

    private static HttpClient Client()
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TOKENIFY-Tracker", AppInfo.Version));
        return http;
    }

    private static bool Allowed(Uri? uri) => uri is { Scheme: "https" } && AllowedHosts.Any(h => uri.Host == h || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Asks GitHub for the newest published tracker release that is newer than this one.</summary>
    public static async Task<UpdateCheck> Check(CancellationToken ct = default)
    {
        try
        {
            using var http = Client();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var text = await http.GetStringAsync($"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases?per_page=20", ct).ConfigureAwait(false);
            return JsonNode.Parse(text) is JsonArray releases ? new UpdateCheck(true, Newest(releases, CurrentVersion)) : new UpdateCheck(false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            Log.Info("Update check did not succeed: " + ex.Message);
            return new UpdateCheck(false, null);
        }
    }

    /// <summary>Picks the newest usable release from GitHub's list. A release it cannot make sense of is skipped.</summary>
    public static UpdateInfo? Newest(JsonArray releases, Version current)
    {
        UpdateInfo? best = null;
        Version? bestVersion = null;
        foreach (var r in releases)
        {
            try
            {
                if (r is not JsonObject || ApiResult.Bool(r["draft"]) || ApiResult.Bool(r["prerelease"])) continue;
                var tag = ApiResult.Text(r["tag_name"]);
                if (tag is null || !tag.StartsWith(TagPrefix, StringComparison.Ordinal) || !Version.TryParse(tag[TagPrefix.Length..], out var parsed)) continue;
                var v = Normalize(parsed);
                if (v <= current || (bestVersion is not null && v <= bestVersion)) continue;
                string? exe = null, sig = null;
                long size = 0;
                foreach (var a in r["assets"] as JsonArray ?? [])
                {
                    var name = ApiResult.Text(a?["name"]);
                    var url = ApiResult.Text(a?["browser_download_url"]);
                    if (name == AppInfo.ExeName) { exe = url; size = a?["size"] is JsonValue n && n.TryGetValue<long>(out var bytes) ? bytes : 0; }
                    else if (name == AppInfo.ExeName + ".sig") sig = url;
                }
                if (exe is null || sig is null) continue;   // a release without a signature is not offered
                best = new UpdateInfo(v.ToString(3), ApiResult.Text(r["body"]) ?? "", exe, sig, size, ApiResult.Text(r["html_url"]) ?? AppInfo.Website);
                bestVersion = v;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or OverflowException)
            {
                // an oddly shaped release entry: ignore it, keep looking at the others
            }
        }
        return best;
    }

    /// <summary>Downloads the release and checks its signature. Returns the path of the verified file, or why not.</summary>
    public static async Task<(string? Path, string Message)> Download(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var folder = Path.Combine(AppPaths.DataFolder, "update");
        var target = Path.Combine(folder, AppInfo.ExeName + ".download");
        try
        {
            if (!Allowed(new Uri(info.ExeUrl)) || !Allowed(new Uri(info.SignatureUrl))) return (null, "The update is not hosted where TOKENIFY releases are published.");
            Directory.CreateDirectory(folder);
            using var http = Client();
            // Every step has to keep making progress: the timer is re-armed after each piece that arrives, so a
            // connection that dies silently (sleep, a dropped line) ends the download instead of hanging it.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallLimit);

            var signature = (await http.GetStringAsync(info.SignatureUrl, stall.Token).ConfigureAwait(false)).Trim();
            stall.CancelAfter(StallLimit);
            using (var response = await http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (!Allowed(response.RequestMessage?.RequestUri)) return (null, "The download was redirected somewhere unexpected.");
                var length = response.Content.Headers.ContentLength ?? info.Size;
                if (length > MaxBytes) return (null, "The update is larger than any real release.");
                await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                stall.CancelAfter(StallLimit);
                while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallLimit);
                    done += read;
                    if (done > MaxBytes) throw new IOException("download too large");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (length > 0) progress?.Report(Math.Min(1, (double)done / length));
                }
            }

            if (!Verify(target, signature))
            {
                File.Delete(target);
                Log.Warn($"Update {info.Version}: signature did not match; the download was deleted");
                return (null, "The download's signature did not match TOKENIFY's release key, so it was deleted and not installed.");
            }
            var inFile = VersionOf(target);
            if (!Version.TryParse(info.Version, out var offered) || !AcceptableVersion(inFile, offered, CurrentVersion))
            {
                File.Delete(target);
                Log.Warn($"Update {info.Version}: the signed file is version {inFile?.ToString(3) ?? "unknown"}, not a newer {info.Version}; the download was deleted");
                return (null, $"The download is a genuine TOKENIFY build, but it is version {inFile?.ToString(3) ?? "unknown"} and not the newer {info.Version} it was offered as. It was deleted and not installed.");
            }
            Log.Info($"Update {info.Version} downloaded and verified");
            return (target, "Verified.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or UriFormatException)
        {
            if (ct.IsCancellationRequested) throw;
            try { File.Delete(target); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return (null, ex is OperationCanceledException
                ? "The download stopped arriving, so it was given up. Check your connection and try again."
                : "The update could not be downloaded: " + ex.Message);
        }
    }

    /// <summary>True when <paramref name="signatureBase64"/> is a valid signature of the file by the release key.</summary>
    public static bool Verify(string file, string signatureBase64, string? publicKeyBase64 = null)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64 ?? PublicKey), out _);
            using var stream = File.OpenRead(file);
            return key.VerifyData(stream, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Swaps the running exe for the verified download and starts it. The old exe is kept as ".old" until the new
    /// one has run for a while (<see cref="CleanUp"/>); if starting it fails, the old one is put back.
    /// </summary>
    public static (bool Ok, string Message) Apply(string verifiedDownload)
    {
        // The file is replaced where it is and under the name it has ("TOKENIFY-Tracker (1).exe" stays that).
        if (!CanApply(out var reason) || Environment.ProcessPath is not { } current) return (false, reason);
        var old = current + ".old";
        try
        {
            File.Move(current, old, overwrite: true);   // Windows lets a running program be renamed
            try
            {
                File.Move(verifiedDownload, current);
                Process.Start(new ProcessStartInfo(current) { UseShellExecute = false, ArgumentList = { "--updated" } })?.Dispose();
            }
            catch
            {
                if (File.Exists(current)) File.Delete(current);
                File.Move(old, current);
                throw;
            }
            return (true, "Restarting…");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Update could not be installed", ex);
            return (false, "The update could not be installed here (" + ex.Message + "). Your current version keeps running.");
        }
    }

    /// <summary>Removes the previous version left by an update. Called once this version has started properly.</summary>
    public static void CleanUp()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
            var folder = Path.Combine(AppPaths.DataFolder, "update");
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still in use: removed next time
        }
    }

    // ---- release tooling (used by the test tool, never by the app) ----

    public static (string PrivatePem, string PublicBase64) NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public static string Sign(string file, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        using var stream = File.OpenRead(file);
        return Convert.ToBase64String(key.SignData(stream, HashAlgorithmName.SHA256));
    }
}
