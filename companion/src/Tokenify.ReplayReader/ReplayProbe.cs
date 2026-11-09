using System.Text;

namespace Tokenify.ReplayReader;

public enum ProbeState
{
    /// <summary>Finished recording and decryptable: ready to read.</summary>
    Ready,
    /// <summary>Fortnite is still writing it (or stopped mid-match): no decryption key yet.</summary>
    Recording,
    /// <summary>Empty, truncated header, or not a Fortnite replay.</summary>
    Invalid,
    /// <summary>A replay file version newer than this parser knows.</summary>
    UnsupportedVersion,
}

public sealed record ProbeResult(
    ProbeState State,
    uint FileVersion,
    uint LengthMs,
    bool IsLive,
    bool IsEncrypted,
    bool KeyPresent,
    DateTime? TimestampUtc,
    string? Reason);

/// <summary>
/// Reads only the small file header (UE LocalFileNetworkReplayStreaming FLocalFileReplayInfo) to decide whether a
/// replay can be parsed yet. Fortnite marks a replay "live" and leaves its encryption key empty or zeroed until the
/// match ends, so nothing in a recording replay can be decrypted: parsing it early would only ever produce a wrong
/// zero, so we wait instead.
/// </summary>
public static class ReplayProbe
{
    public const uint FileMagic = 0x1CA2E27F;
    public const uint LatestKnownFileVersion = 7; // HISTORY_CUSTOM_VERSIONS

    public static ProbeResult Probe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(fs.Length, 16384)];
            fs.ReadExactly(buffer);
            return Probe(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProbeResult(ProbeState.Invalid, 0, 0, false, false, false, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static ProbeResult Probe(ReadOnlySpan<byte> header)
    {
        try
        {
            var r = new SpanReader(header);
            if (r.U32() != FileMagic)
            {
                return new ProbeResult(ProbeState.Invalid, 0, 0, false, false, false, null, "not a replay file");
            }

            var fileVersion = r.U32();
            if (fileVersion > LatestKnownFileVersion)
            {
                return new ProbeResult(ProbeState.UnsupportedVersion, fileVersion, 0, false, false, false, null, $"replay file version {fileVersion}");
            }

            if (fileVersion >= 7)
            {
                var count = r.I32();
                if (count < 0 || count > 256) return Invalid(fileVersion, "bad custom version count");
                r.Skip(count * 20);
            }

            var lengthMs = r.U32();
            r.U32(); // network version
            r.U32(); // changelist
            r.SkipFString();
            var isLive = r.U32() != 0;

            DateTime? timestamp = null;
            if (fileVersion >= 3)
            {
                var binary = r.I64();
                // Written by the game as local time (matches the "UnsavedReplay-yyyy.MM.dd-HH.mm.ss" file name).
                var local = DateTime.FromBinary(binary);
                if (local.Year > 2015 && local.Year < 2100)
                {
                    timestamp = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
                }
            }

            if (fileVersion >= 2)
            {
                r.U32(); // compressed
            }

            var encrypted = false;
            var keyPresent = true;
            if (fileVersion >= 6)
            {
                encrypted = r.U32() != 0;
                var keySize = r.I32();
                if (keySize < 0 || keySize > 1024) return Invalid(fileVersion, "bad key size");
                var key = r.Bytes(keySize);
                keyPresent = keySize > 0 && key.IndexOfAnyExcept((byte)0) >= 0;
            }

            var ready = !isLive && (!encrypted || keyPresent);
            return new ProbeResult(ready ? ProbeState.Ready : ProbeState.Recording, fileVersion, lengthMs, isLive, encrypted, keyPresent, timestamp,
                ready ? null : isLive ? "still recording" : "no decryption key");
        }
        catch (IndexOutOfRangeException)
        {
            return new ProbeResult(ProbeState.Invalid, 0, 0, false, false, false, null, "header is truncated");
        }
    }

    private static ProbeResult Invalid(uint version, string why) => new(ProbeState.Invalid, version, 0, false, false, false, null, why);

    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public uint U32() { var v = BitConverter.ToUInt32(Take(4)); return v; }
        public int I32() => BitConverter.ToInt32(Take(4));
        public long I64() => BitConverter.ToInt64(Take(8));
        public ReadOnlySpan<byte> Bytes(int n) => Take(n);
        public void Skip(int n) => Take(n);

        public void SkipFString()
        {
            var len = I32();
            if (len == 0) return;
            if (len < -4096 || len > 4096) throw new IndexOutOfRangeException();
            Take(len < 0 ? -len * 2 : len);
        }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _pos + n > _data.Length) throw new IndexOutOfRangeException();
            var s = _data.Slice(_pos, n);
            _pos += n;
            return s;
        }
    }

    /// <summary>Decodes an FString at the start of <paramref name="data"/> (used by tests).</summary>
    internal static string ReadFString(ReadOnlySpan<byte> data)
    {
        var len = BitConverter.ToInt32(data);
        return len < 0 ? Encoding.Unicode.GetString(data.Slice(4, -len * 2)).TrimEnd('\0') : Encoding.ASCII.GetString(data.Slice(4, len)).TrimEnd('\0');
    }
}
