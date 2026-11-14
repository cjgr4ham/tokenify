using System.Drawing.Imaging;
using System.Text;
using Tokenify.Core;
using Tokenify.Overlay;

namespace Tokenify.Tracker;

/// <summary>
/// "TOKENIFY-Tracker.exe --selftest folder": runs the real app against a test data folder and checks what can
/// only be checked with real windows. The overlay's window styles, that it never takes the keyboard, dragging,
/// resizing, locking, position memory and off-screen recovery; every page of the main window and every setup
/// step is drawn straight into a PNG file (the screen is never captured). Refuses to run on a real install.
/// </summary>
internal static class SelfTest
{
    public static int Run(string folder, SingleInstance instance)
    {
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        var failed = 0;
        void Check(bool ok, string what)
        {
            if (!ok) failed++;
            report.AppendLine($"{(ok ? "ok  " : "FAIL")}  {what}");
        }
        void Note(string text) => report.AppendLine("      " + text);

        // It rewrites settings and history: only ever on a data folder made for the test. A test server alone is
        // not enough, because the data would then still be the player's own.
        if (!AppPaths.DataFolderOverridden)
        {
            File.WriteAllText(Path.Combine(folder, "selftest.txt"), "refused: the self-test only runs with a test data folder (TOKENIFY_DATA)");
            return 2;
        }

        try
        {
            using (var seed = new Store(AppPaths.Database))
            {
                var settings = seed.LoadSettings();
                settings.General.SetupDone = true;
                settings.General.Updates = "off";
                seed.SaveSettings(settings);
                seed.Set("history.cutoff", "0");   // the replays in the test folder count as new matches
            }

            MainWindow.Quiet = true;
            var before = ForegroundWatch.InFront();
            using var app = new TrackerApp(minimized: true, instance, justUpdated: false);
            var store = app.Tracker.Store;
            void Pump(int ms)
            {
                var until = Environment.TickCount64 + ms;
                while (Environment.TickCount64 < until)
                {
                    Application.DoEvents();
                    Thread.Sleep(15);
                }
            }

            // ---- wait for the replays in the test folder to be read
            var replays = Core.Tracker.ListReplays(app.Tracker.ResolveFolder()).Files.Count;
            var deadline = Environment.TickCount64 + 180_000;
            while (Environment.TickCount64 < deadline)
            {
                Pump(400);
                var files = store.Files();
                if (files.Count >= replays && files.All(f => f.State is "done" or "failed" or "unsupported" or "pre-season" or "skipped")) break;
            }
            Pump(600);
            var snap = app.Tracker.Snapshot();
            Check(store.Files().Count >= replays, $"every replay in the test folder was looked at ({store.Files().Count} of {replays})");
            Note($"total {snap.Stats.TotalChests} chests in {snap.Stats.Games} valid games, CPG {Fmt.Cpg(snap.Stats.Cpg)}, {snap.Stats.Last10.Label} {Fmt.Cpg(snap.Stats.Last10.Cpg)}, best {snap.Stats.Best}, to review {snap.NeedsReview}");

            // ---- the overlay window
            var overlay = app.Overlay;
            var window = overlay.Window;
            Check(window.Visible, "overlay is shown");
            var styles = window.Styles();
            Check(styles.Layered, "overlay window is layered (per-pixel transparency)");
            Check(styles.Topmost, "overlay window is always on top");
            Check(styles.NoActivate, "overlay window can never be activated (never takes the keyboard)");
            Check(styles.ToolWindow, "overlay window stays out of the taskbar and Alt+Tab");
            Check(styles.ClickThrough, "locked overlay is click-through");
            var after = ForegroundWatch.InFront();
            Check(!after.Own, "the tracker did not put itself in front");
            Check(after.Title == before.Title && after.Class == before.Class, "the window that was in front is still in front");

            var primary = Screen.PrimaryScreen!.Bounds;
            var panel = new Rectangle(window.PanelLocation, window.PanelSize);
            Check(primary.Contains(panel), $"overlay panel is fully on the main display ({panel})");
            Check(window.PanelSize.Width is > 120 and < 600, $"overlay has a sensible size at 100% ({window.PanelSize})");

            // unlock: takes mouse input, still never activates
            app.ToggleLock();
            Pump(300);
            styles = window.Styles();
            Check(!app.Tracker.Settings.Overlay.Locked, "unlock: setting saved");
            Check(!styles.ClickThrough && styles.NoActivate && styles.Topmost, "unlocked overlay takes the mouse but still cannot be activated");
            Check(!window.BeginDrag(new Point(0, 0), false) == false, "unlocked overlay accepts a drag");
            window.EndDrag();
            Pump(100);

            // drag it somewhere, and check it is remembered
            var start = window.PanelLocation;
            var cursor = new Point(start.X + 20, start.Y + 20);
            window.BeginDrag(cursor, onGrip: false);
            var target = new Point(primary.Left + primary.Width / 2 + 37, primary.Top + primary.Height / 3 + 21);
            window.ContinueDrag(new Point(cursor.X + target.X - start.X, cursor.Y + target.Y - start.Y));
            window.EndDrag();   // no pause in between: the real mouse must not be able to join in
            Pump(400);
            var dropped = window.PanelLocation;
            Check(Math.Abs(dropped.X - target.X) <= 16 && Math.Abs(dropped.Y - target.Y) <= 16, $"drag moves the overlay (to {dropped}, wanted {target})");
            var saved = app.Tracker.Settings.Overlay;
            Check(saved.Monitor == Screen.PrimaryScreen.DeviceName && saved.Places.ContainsKey(saved.Monitor ?? ""), "position is saved for its display");
            var resolved = Placement.Resolve(saved, Screen.AllScreens.Select(sc => new MonitorInfo(sc.DeviceName, sc.Bounds, sc.Primary)).ToList(), window.PanelSize);
            Check(Math.Abs(resolved.X - dropped.X) <= 2 && Math.Abs(resolved.Y - dropped.Y) <= 2, $"the saved position resolves back to where it was dropped ({resolved})");

            // resize with the corner grip
            var widthBefore = window.PanelSize.Width;
            window.BeginDrag(new Point(1000, 1000), onGrip: true);
            window.ContinueDrag(new Point(1000 + widthBefore / 2, 1000 + widthBefore / 2));
            window.EndDrag();
            Pump(400);
            var scale = app.Tracker.Settings.Overlay.Scale;
            Check(scale is > 110 and <= 200, $"dragging the corner grip enlarges the overlay (scale {scale}%)");
            Check(window.PanelSize.Width > widthBefore, $"and the window grew ({widthBefore} to {window.PanelSize.Width} px)");
            app.Tracker.UpdateSettings(s => s.Overlay.Scale = 10_000);
            Pump(300);
            Check(app.Tracker.Settings.Overlay.Scale == 200, "scale is limited to 200%");
            app.Tracker.UpdateSettings(s => s.Overlay.Scale = 100);
            Pump(300);

            // a saved position that is now off-screen is recovered
            app.Tracker.UpdateSettings(s => s.Overlay.Places[s.Overlay.Monitor ?? ""] = new OverlayPlace { X = 1.2, Y = 1.2 });
            Pump(400);
            panel = new Rectangle(window.PanelLocation, window.PanelSize);
            Check(window.LastPresentError == 0, "Windows accepted the overlay's picture and position");
            Check(primary.Contains(panel), $"a position off the screen is brought back into view ({panel})");
            Check(panel.Right >= primary.Right - 2 && panel.Bottom >= primary.Bottom - 2, "and sits in the corner nearest to where it was saved");
            app.Tracker.UpdateSettings(s => { s.Overlay.Monitor = @"\\.\DISPLAY-THAT-IS-GONE"; });
            Pump(400);
            panel = new Rectangle(window.PanelLocation, window.PanelSize);
            Check(Screen.AllScreens.Any(sc => sc.Bounds.Contains(panel)), "with its display unplugged the overlay moves to a connected one");
            Check(app.Tracker.Settings.Overlay.Monitor == @"\\.\DISPLAY-THAT-IS-GONE", "and still remembers its own display for when it returns");
            app.Tracker.UpdateSettings(s => { s.Overlay.Monitor = null; s.Overlay.Places.Clear(); });
            Pump(300);

            // hide, show, lock
            app.ToggleOverlay();
            Pump(300);
            Check(!window.Visible, "show/hide hides the overlay");
            app.ToggleOverlay();
            Pump(300);
            Check(window.Visible, "show/hide shows it again");
            app.ToggleLock();
            Pump(300);
            Check(window.Styles().ClickThrough && app.Tracker.Settings.Overlay.Locked, "locking makes it click-through again");
            app.Tracker.UpdateSettings(s => s.Overlay.Enabled = false);
            Pump(300);
            Check(!window.Visible, "turning the overlay off hides it");
            app.ToggleLock();
            Pump(300);
            Check(window.Visible && app.Tracker.Settings.Overlay.Enabled, "unlocking from the tray or shortcut always brings it back");
            app.ToggleLock();
            Pump(200);
            after = ForegroundWatch.InFront();
            Check(!after.Own && after.Title == before.Title, "after all of that the tracker still never took the keyboard");
            foreach (var which in new[] { "lock", "visible" })
            {
                if (app.ShortcutProblem(which)) Note($"shortcut '{which}' could not be registered here (another program has it): reported in Settings, not a failure");
                else Note($"shortcut '{which}' registered with Windows");
            }

            // ---- the OBS page: two plain files, written only when asked for
            app.Tracker.UpdateSettings(s => s.General.ObsPage = true);
            Pump(600);
            Check(File.Exists(AppPaths.ObsPage) && File.Exists(ObsPage.DataPath), "the OBS page and its data file are written when the option is on");
            var obsData = File.Exists(ObsPage.DataPath) ? File.ReadAllText(ObsPage.DataPath) : "";
            Check(obsData.Contains($"\"value\":\"{Fmt.Int(snap.Stats.TotalChests)}\""), "the OBS data holds the confirmed total");
            Directory.CreateDirectory(Path.Combine(folder, "obs"));
            foreach (var file in new[] { AppPaths.ObsPage, ObsPage.DataPath })
            {
                if (File.Exists(file)) File.Copy(file, Path.Combine(folder, "obs", Path.GetFileName(file)), true);
            }
            app.Tracker.UpdateSettings(s => s.General.ObsPage = false);
            Pump(200);

            // ---- every page, drawn to a file
            var main = new MainWindow(app, app.Tracker.Snapshot()) { StartPosition = FormStartPosition.Manual, Location = new Point(-12000, -12000) };
            main.Show();
            Pump(300);
            var review = store.AllMatchesLight().OrderByDescending(m => m.Ambiguous).ThenByDescending(m => m.Destroyed).FirstOrDefault();
            foreach (var key in new[] { "overview", "matches", "appearance", "layout", "position", "tracking", "shortcuts", "account", "advanced" })
            {
                if (key == "matches") main.ShowMatch(review?.Id);
                else main.Show(key);
                Pump(350);
                Save(main, Path.Combine(folder, $"window-{key}.png"));
                // ...and the rest of a page that is taller than the window, a screenful at a time
                var page = main.CurrentPage;
                for (var part = 2; part <= 6 && page.AutoScroll && page.VerticalScroll.Visible; part++)
                {
                    var scrolledFrom = page.AutoScrollPosition;
                    page.AutoScrollPosition = new Point(0, -scrolledFrom.Y + page.ClientSize.Height - 40);
                    if (page.AutoScrollPosition == scrolledFrom) break;
                    Pump(200);
                    Save(main, Path.Combine(folder, $"window-{key}-{part}.png"));
                }
            }
            Check(true, "every page of the main window was drawn");
            main.Dispose();

            if (review is not null)
            {
                using var dialog = new CorrectionDialog(review, true) { StartPosition = FormStartPosition.Manual, Location = new Point(-12000, -12000) };
                dialog.Show();
                Pump(250);
                Save(dialog, Path.Combine(folder, "dialog-correct.png"));
            }
            using (var calendarHost = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(-12000, -12000), ShowInTaskbar = false })
            {
                var calendar = new TkCalendar(DateTime.Today) { Location = Point.Empty };
                calendarHost.ClientSize = calendar.Size;
                calendarHost.Controls.Add(calendar);
                calendarHost.Show();
                Pump(200);
                Save(calendarHost, Path.Combine(folder, "calendar.png"));
            }

            app.Tracker.UpdateSettings(s => s.General.SetupDone = false);
            var wizard = new SetupWizard(app, app.Tracker.Snapshot()) { StartPosition = FormStartPosition.Manual, Location = new Point(-12000, -12000) };
            wizard.Show();
            for (var i = 0; i < wizard.StepCount; i++)
            {
                wizard.GoTo(i);
                Pump(350);
                Save(wizard, Path.Combine(folder, $"setup-{i + 1}.png"));
            }
            Check(true, "every setup step was drawn");
            wizard.Dispose();
            app.Tracker.UpdateSettings(s => s.General.SetupDone = true);
            Pump(200);
        }
        catch (Exception ex)
        {
            failed++;
            report.AppendLine("FAIL  self-test stopped: " + ex);
        }

        report.AppendLine();
        report.AppendLine(failed == 0 ? "ALL CHECKS PASSED" : $"{failed} CHECK(S) FAILED");
        File.WriteAllText(Path.Combine(folder, "selftest.txt"), report.ToString());
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Asks the window to draw itself into a bitmap. Nothing is read from the screen.</summary>
    private static void Save(Form form, string path)
    {
        using var whole = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
        form.DrawToBitmap(whole, new Rectangle(Point.Empty, form.Size));
        var client = form.RectangleToScreen(form.ClientRectangle);
        var crop = new Rectangle(client.Left - form.Left, client.Top - form.Top, client.Width, client.Height);
        crop.Intersect(new Rectangle(Point.Empty, whole.Size));
        using var shot = crop.Width > 10 && crop.Height > 10 ? whole.Clone(crop, PixelFormat.Format32bppArgb) : new Bitmap(whole);
        shot.Save(path, ImageFormat.Png);
    }
}
