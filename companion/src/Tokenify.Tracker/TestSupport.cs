using Tokenify.Core;

namespace Tokenify.Tracker;

/// <summary>
/// For the automated end-to-end check only (dev/e2e-tracker.mjs): changes the newest match the way the Matches
/// page does, without a window. It works solely on a data folder made for a test (TOKENIFY_DATA) and refuses on a
/// real install, where corrections are made in the window.
///   TOKENIFY-Tracker.exe --correct latest &lt;count | none | exclude | include&gt;
/// </summary>
internal static class TestSupport
{
    public const string Arg = "--correct";

    public static int Correct(string[] args)
    {
        if (!AppPaths.DataFolderOverridden || args.Length < 3 || args[1] != "latest") return 2;
        using var store = new Store(AppPaths.Database);
        using var tracker = new Core.Tracker(store);
        var match = store.Matches(1).FirstOrDefault();
        if (match is null) return 1;
        switch (args[2])
        {
            case "exclude":
                tracker.SetExcluded(match.Id, true);
                break;
            case "include":
                tracker.SetExcluded(match.Id, false);
                break;
            case "none":
                tracker.Correct(match.Id, null, null);
                break;
            default:
                if (!int.TryParse(args[2], out var count) || count is < 0 or > 400) return 2;
                tracker.Correct(match.Id, count, "end-to-end test");
                break;
        }
        return 0;
    }
}
