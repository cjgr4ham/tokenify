using System.Reflection;
using System.Text;

namespace Tokenify.Companion;

/// <summary>Version and server settings. The publishable API key is public by design (the website ships the
/// same one); every request is checked on the server against this PC's own device secret.</summary>
internal static class AppInfo
{
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
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
    public static bool TestCopy => CustomServer || Env("TOKENIFY_COMPANION_DATA") != null;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

internal static class AppPaths
{
    public static string DataFolder { get; } = Environment.GetEnvironmentVariable("TOKENIFY_COMPANION_DATA") is { Length: > 0 } d
        ? Path.GetFullPath(d)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TOKENIFY Companion");

    public static string Database => Path.Combine(DataFolder, "companion.db");
    public static string LogFile => Path.Combine(DataFolder, "companion.log");

    /// <summary>Where Fortnite saves replays on Windows.</summary>
    public static string DefaultDemos { get; } = Environment.GetEnvironmentVariable("TOKENIFY_DEMOS") is { Length: > 0 } t
        ? t
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");
}

/// <summary>
/// Small rolling log (companion.log, about 1 MB kept). It records replay detection, parsing problems, uploads and
/// connection changes. It never records device secrets, pairing codes or Epic account ids.
/// </summary>
internal static class Log
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

internal static class Embedded
{
    public static Icon AppIcon(int size = 32)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("tokenify.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, size, size);
    }
}
