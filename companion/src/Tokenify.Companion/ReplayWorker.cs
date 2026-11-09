// Child-process reading is adapted from rossalv/fortnite-chest-counter (MIT, src/ChestCounter/ReplayWorker.cs);
// efficiency mode from LED-N/Fortnite-ChestCounter (MIT, src/ChestCounter/EcoQos.cs).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tokenify.ReplayReader;

namespace Tokenify.Companion;

/// <summary>
/// Reads one replay in a separate, short-lived, low-priority copy of this exe ("TOKENIFY-Companion.exe
/// --read-replay file"). A replay the parser can't handle can then be killed after a time limit instead of hanging
/// the app, and the parser's memory goes back to Windows as soon as each replay is done.
/// </summary>
internal static class ReplayWorker
{
    public const string Arg = "--read-replay";
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <summary>Child entry point: parse, write the result as JSON on stdout.</summary>
    public static int Run(string path)
    {
        try
        {
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
        var exe = Environment.ProcessPath;
        if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new ChestReader().Read(path); // running under "dotnet run": read in-process
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(Arg);
        psi.ArgumentList.Add(path);

        using var child = Process.Start(psi);
        if (child is null)
        {
            failure = "could not start the replay reader";
            return null;
        }

        try
        {
            try { child.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }

            using var output = new MemoryStream();
            var copyOut = child.StandardOutput.BaseStream.CopyToAsync(output);
            var readErr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(timeout))
            {
                try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                failure = $"took longer than {timeout.TotalSeconds:0} s to read";
                return null;
            }

            child.WaitForExit();
            copyOut.Wait(TimeSpan.FromSeconds(5));
            if (child.ExitCode != 0 || output.Length == 0)
            {
                var err = readErr.Wait(TimeSpan.FromSeconds(2)) ? readErr.Result : "";
                failure = $"replay reader failed ({child.ExitCode}): {(err.Length > 200 ? err[..200] : err)}";
                return null;
            }

            return JsonSerializer.Deserialize<ScanResult>(output.ToArray(), Json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            failure = "unreadable result from the replay reader: " + ex.Message;
            return null;
        }
    }
}

/// <summary>Windows 10 1709+/11 power throttling ("efficiency mode"): parsing runs on efficient cores at low clocks.</summary>
internal static class EcoQos
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
