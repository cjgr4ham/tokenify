// Shared plumbing for reading Fortnite replays with Unreal.Core: decryption, Oodle decompression, skipping
// events, and a guard against corrupt export blocks. Adapted from rossalv/fortnite-chest-counter (MIT).
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Unreal.Core;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace Tokenify.ReplayReader;

/// <summary>
/// The replay's spectator controller sends an extra NetPlayerIndex byte when its channel opens;
/// Unreal.Core needs this marker to skip it.
/// </summary>
[PlayerController("BP_ReplayPC_Athena_C")]
internal sealed class ReplayPlayerControllerMarker
{
}

/// <summary>Base for TOKENIFY's replay readers (the chest counter and the forensic probe).</summary>
public abstract class FortniteReplayBase : ReplayReader<Replay>
{
    protected FortniteReplayBase() : base(NullLogger.Instance, ParseMode.Minimal)
    {
    }

    /// <summary>Replay time (seconds) of the frame being read.</summary>
    protected static float Now => CurrentTime;

    /// <summary>Research only: the same clock, readable from outside while a read is running.</summary>
    public static float Clock => CurrentTime;

    /// <summary>Research only: where the replay library's own warnings go (normally nowhere).</summary>
    public void UseLogger(Microsoft.Extensions.Logging.ILogger logger) => _logger = logger;

    /// <summary>Corrupt export blocks skipped without allocating (diagnostics).</summary>
    public int RejectedCorruptBlocks { get; protected set; }

    /// <summary>Fortnite may keep the replay open while recording, so allow concurrent writers (read-only for us).</summary>
    public static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > 1L << 30) throw new IOException("file is larger than any real replay");
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    protected void TryCleanup()
    {
        try { Cleanup(); } catch { /* reader state is reset on the next read anyway */ }
    }

    /// <summary>Real Fortnite classes have at most a few thousand replicated fields.</summary>
    private const uint MaxExportsPerGroup = 1 << 16;

    /// <summary>
    /// A few replays contain a corrupt export block whose string length reads as ~1 billion characters.
    /// Unreal.Core would allocate a buffer that size before the read fails. Walk the block first, checking every
    /// length against the data actually left; if one is impossible, drop the packet without the giant allocation.
    /// </summary>
    public override void ReceiveNetFieldExportsCompat(FBitArchive bitArchive)
    {
        bitArchive.Mark();
        var impossible = HasImpossibleLength(bitArchive);
        bitArchive.Pop();

        if (impossible)
        {
            RejectedCorruptBlocks++;
            throw new InvalidDataException("Corrupt net field export block (impossible length).");
        }

        base.ReceiveNetFieldExportsCompat(bitArchive);
    }

    private static bool HasImpossibleLength(FBitArchive a)
    {
        var version = a.EngineNetworkVersion;
        if (a.GetBitsLeft() < 32) return false;
        var count = a.ReadUInt32();

        for (var i = 0; i < count; i++)
        {
            if (a.GetBitsLeft() < 48) return false;
            a.ReadIntPacked();
            if (a.ReadBit())
            {
                if (SkipFString(a) is not { } ok) return false;
                if (!ok) return true;
                if (a.GetBitsLeft() < 32) return false;
                if (a.ReadUInt32() > MaxExportsPerGroup) return true;
            }

            if (a.GetBitsLeft() < 1) return false;
            if (!a.ReadBit()) continue;
            if (a.GetBitsLeft() < 72) return false;
            a.ReadIntPacked();
            a.ReadUInt32();

            if (version < EngineNetworkVersionHistory.HISTORY_NETEXPORT_SERIALIZE_FIX)
            {
                var strings = version < EngineNetworkVersionHistory.HISTORY_NETEXPORT_SERIALIZATION ? 2 : 1;
                for (var s = 0; s < strings; s++)
                {
                    if (SkipFString(a) is not { } ok) return false;
                    if (!ok) return true;
                }
            }
            else
            {
                if (a.GetBitsLeft() < 1) return false;
                if (a.ReadBit())
                {
                    if (a.GetBitsLeft() < 40) return false;
                    if (version < EngineNetworkVersionHistory.HISTORY_CHANNEL_NAMES) a.ReadUInt32(); else a.ReadIntPacked();
                }
                else
                {
                    if (SkipFString(a) is not { } ok) return false;
                    if (!ok) return true;
                    if (a.GetBitsLeft() < 32) return false;
                    a.ReadInt32();
                }
            }
        }

        return false;
    }

    /// <summary>true = skipped a valid string, false = impossible length, null = ran out of data.</summary>
    private static bool? SkipFString(FBitArchive a)
    {
        if (a.GetBitsLeft() < 32) return null;
        long length = a.ReadInt32();
        var bits = (length < 0 ? -2 * length : length) * 8;
        if (bits > a.GetBitsLeft()) return false;
        a.SkipBits((int)bits);
        return true;
    }

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
    }

    protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update)
    {
    }

    protected override void OnExternalDataRead(uint channelIndex, IExternalData? externalData)
    {
    }

    /// <summary>Events (kill feed, match stats, ...) aren't needed; skip them without decrypting.</summary>
    public override void ReadEvent(FArchive archive)
    {
        archive.ReadFString(); // id
        archive.ReadFString(); // group
        archive.ReadFString(); // metadata
        archive.ReadUInt32();  // start time
        archive.ReadUInt32();  // end time
        archive.SkipBytes(archive.ReadInt32());
    }

    protected override FArchive DecryptBuffer(FArchive archive, int size)
    {
        if (!Replay.Info.IsEncrypted)
        {
            return new Unreal.Core.BinaryReader(archive.ReadMemory(size))
            {
                EngineNetworkVersion = Replay.Header.EngineNetworkVersion,
                NetworkVersion = Replay.Header.NetworkVersion,
                ReplayHeaderFlags = Replay.Header.Flags,
                ReplayVersion = Replay.Info.FileVersion,
            };
        }

        using var aes = Aes.Create();
        aes.Key = Replay.Info.EncryptionKey.ToArray();
        var decrypted = aes.DecryptEcb(archive.ReadMemory(size).Span, PaddingMode.PKCS7);

        return new Unreal.Core.BinaryReader(decrypted.AsMemory())
        {
            EngineNetworkVersion = archive.EngineNetworkVersion,
            NetworkVersion = archive.NetworkVersion,
            ReplayHeaderFlags = archive.ReplayHeaderFlags,
            ReplayVersion = archive.ReplayVersion,
        };
    }

    protected override FArchive Decompress(FArchive archive)
    {
        if (!Replay.Info.IsCompressed) return archive;

        var decompressedSize = archive.ReadInt32();
        var compressedSize = archive.ReadInt32();
        if (decompressedSize < 0 || compressedSize < 0 || decompressedSize > 256 << 20) throw new InvalidDataException("impossible chunk size");
        var compressed = archive.ReadBytes(compressedSize);
        var output = Unreal.Encryption.Oodle.DecompressReplayData(compressed, decompressedSize);

        return new Unreal.Core.BinaryReader(output)
        {
            EngineNetworkVersion = archive.EngineNetworkVersion,
            NetworkVersion = archive.NetworkVersion,
            ReplayHeaderFlags = archive.ReplayHeaderFlags,
            ReplayVersion = archive.ReplayVersion,
        };
    }
}
