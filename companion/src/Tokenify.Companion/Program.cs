using System.Security.Cryptography;
using System.Text;

namespace Tokenify.Companion;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Child process: read one replay and exit (see ReplayWorker).
        if (args.Length >= 2 && args[0] == ReplayWorker.Arg)
        {
            return ReplayWorker.Run(args[1]);
        }

        // Headless linking (support and automated tests): TOKENIFY-Companion.exe --pair XXXX-XXXX [--history import|skip]
        if (args.Length >= 2 && args[0] == "--pair")
        {
            return PairHeadless(args[1], args.SkipWhile(a => a != "--history").Skip(1).FirstOrDefault());
        }

        // Support: write the diagnostics report (no secrets) to a file. TOKENIFY-Companion.exe --report <file>
        if (args.Length >= 2 && args[0] == "--report")
        {
            using var store = new Store(AppPaths.Database);
            using var engine = new Engine(store);
            File.WriteAllText(args[1], Diagnostics.Report(engine) + Environment.NewLine + MatchTable(store));
            return 0;
        }

        var minimized = args.Contains("--minimized");
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.SignalExisting();   // already running: bring its window forward instead
            return 0;
        }

        ApplicationConfiguration.Initialize();
        try { Application.SetColorMode(SystemColorMode.System); } catch { /* older Windows: light theme */ }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI error", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal error", e.ExceptionObject as Exception);

        try
        {
            using var app = new CompanionApp(minimized, instance);
            Application.Run(app);
        }
        catch (Exception ex)
        {
            Log.Error("Could not start", ex);
            MessageBox.Show("TOKENIFY Companion could not start:\n\n" + ex.Message, "TOKENIFY Companion", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        return 0;
    }

    private static string MatchTable(Store store) => string.Join(Environment.NewLine,
        store.Matches(50).Select(m => $"{m.StartedLocal:yyyy-MM-dd HH:mm} {m.Chests,3} chests {m.Status,-8} {m.Upload,-13} tries={m.Attempts} next={m.NextTry} {m.PlayerHash[..8]} {m.LastError}")
            .Concat(store.Profiles().Select(p => $"profile {p.Hash[..8]} {p.Name} {p.Decision} claimed={p.Claimed} error={p.Error}")));

    private static int PairHeadless(string code, string? history)
    {
        using var store = new Store(AppPaths.Database);
        using var engine = new Engine(store);
        var (ok, message) = engine.Pair(new string(code.Where(char.IsAsciiLetterOrDigit).ToArray())).GetAwaiter().GetResult();
        if (ok && history is "import" or "skip") engine.SetHistoryChoice(history == "import");
        Log.Info("--pair: " + message);
        File.WriteAllText(Path.Combine(AppPaths.DataFolder, "pair-result.txt"), (ok ? "ok: " : "failed: ") + message);
        return ok ? 0 : 1;
    }
}

/// <summary>One copy per Windows user (and per data folder, so a test copy can run beside the real one).</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    public EventWaitHandle ShowSignal { get; }

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        ShowSignal = show;
    }

    private static string Suffix => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataFolder.ToLowerInvariant())))[..16];

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, @"Local\TOKENIFY-Companion-" + Suffix, out var created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TOKENIFY-Companion-show-" + Suffix);
        return new SingleInstance(mutex, show);
    }

    public static void SignalExisting()
    {
        try
        {
            using var show = EventWaitHandle.OpenExisting(@"Local\TOKENIFY-Companion-show-" + Suffix);
            show.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // the other copy is just starting or exiting
        }
    }

    public void Dispose()
    {
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        ShowSignal.Dispose();
    }
}
