using System.Text;

namespace Tokenify.Core;

/// <summary>Version and server settings. The publishable API key is public by design (the website ships the
/// same one); every request is checked on the server against this PC's own device secret.</summary>
public static class AppInfo
{
    public const string Name = "TOKENIFY Chest Tracker";
    public const string ExeName = "TOKENIFY-Tracker.exe";
    public const string RepoOwner = "cjgr4ham";
    public const string RepoName = "tokenify";

    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
    public static string ParserVersion => ReplayReader.ParserInfo.Version;

    private const string LiveApiUrl = "https://sbzwbxtqplblfngeqlzo.supabase.co";
    private const string LiveApiKey = "sb_publishable_gyYlA8iq-ME6uSButvZTUQ_eLFmrU6Y";
    private const string LiveWebsite = "https://cjgr4ham.github.io/tokenify/";

    // Overrides exist only for development against the local test backend (dev/local-supabase.mjs).
    public static string ApiUrl { get; } = (Env("TOKENIFY_API_URL") ?? LiveApiUrl).TrimEnd('/');
    public static string ApiKey { get; } = Env("TOKENIFY_API_KEY") ?? LiveApiKey;
    public static string Website { get; } = Env("TOKENIFY_WEBSITE") ?? LiveWebsite;
    public static bool CustomServer => Env("TOKENIFY_API_URL") != null;
    /// <summary>Running against a test server or with a test data folder.</summary>
    public static bool TestCopy => CustomServer || AppPaths.DataFolderOverridden;

    /// <summary>
    /// True inside the tracker program itself, whatever its file is called ("TOKENIFY-Tracker (1).exe" after a
    /// second download is still the tracker). False in the test runner and the command-line tools.
    /// </summary>
    public static bool IsTrackerApp { get; } = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "TOKENIFY-Tracker";

    internal static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

public static class AppPaths
{
    /// <summary>
    /// The folder keeps the name the first version (TOKENIFY Companion) used, so an existing install keeps its
    /// history and its link to the website when the tracker replaces it.
    /// </summary>
    public static string DataFolder { get; } = (AppInfo.Env("TOKENIFY_DATA") ?? AppInfo.Env("TOKENIFY_COMPANION_DATA")) is { } d
        ? Path.GetFullPath(d)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TOKENIFY Companion");

    /// <summary>
    /// True when the data folder was pointed somewhere else for a test. Anything that rewrites settings or history
    /// for a test (the self-test, the end-to-end switch) insists on this, so it can never run on a real install:
    /// a test *server* alone is not enough, because the data would still be the player's own.
    /// </summary>
    public static bool DataFolderOverridden => (AppInfo.Env("TOKENIFY_DATA") ?? AppInfo.Env("TOKENIFY_COMPANION_DATA")) is not null;

    public static string Database => Path.Combine(DataFolder, "companion.db");
    public static string LogFile => Path.Combine(DataFolder, "companion.log");
    public static string ObsPage => Path.Combine(DataFolder, "obs-overlay.html");
    public static string Exports => Path.Combine(DataFolder, "exports");

    /// <summary>Where Fortnite saves replays on Windows.</summary>
    public static string DefaultDemos { get; } = AppInfo.Env("TOKENIFY_DEMOS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");
}

/// <summary>
/// Small rolling log (companion.log, about 1 MB kept). It records replay detection, parsing problems, uploads and
/// connection changes. It never records device secrets, pairing codes or Epic account ids.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;

    public static void Info(string message) => Write("info", message);
    public static void Warn(string message) => Write("warn", message);
    public static void Error(string message, Exception? ex = null) => Write("error", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                var path = AppPaths.LogFile;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Copy(path, path + ".1", overwrite: true);
                    File.WriteAllText(path, "");
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-5} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never break the app
        }
    }

    public static string Tail(int maxBytes = 200_000)
    {
        try
        {
            lock (Gate)
            {
                var path = AppPaths.LogFile;
                if (!File.Exists(path)) return "";
                var text = File.ReadAllText(path);
                return text.Length > maxBytes ? text[^maxBytes..] : text;
            }
        }
        catch
        {
            return "";
        }
    }
}
