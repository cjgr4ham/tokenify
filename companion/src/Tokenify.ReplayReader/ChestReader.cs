// TOKENIFY Companion - chest counting from Fortnite replays.
// Parts of this file are adapted from rossalv/fortnite-chest-counter (MIT, src/ChestReplayReader/ChestReader.cs:
// hand-decoding of the few needed fields, chest identities, corrupt-block guard, decryption and decompression)
// and from LED-N/Fortnite-ChestCounter (MIT, src/ChestCounter/ReplayAnalyzer.cs: opening rules, see
// OpeningResolver). Licence texts are in companion/lib/.
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Unreal.Core;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Exceptions;
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

/// <summary>
/// Reads one Fortnite .replay file and finds the chests opened by the player who recorded it.
///
/// Attribution: every searchable container replicates <c>SearchingPawn</c> (the pawn that searched it) next to
/// <c>bAlreadySearched</c>. The game state replicates <c>RecorderPlayerState</c> and each player pawn replicates
/// its <c>PlayerState</c>, so we know exactly which openings were made by the recording player's pawn(s).
/// Only the handful of fields needed are decoded (minimal parse mode), which keeps a replay to about a second.
/// </summary>
public sealed class ChestReader : ReplayReader<Replay>
{
    private enum GroupKind { Ignore, GameState, PlayerState, Pawn, ReplayController, Container }

    private readonly ChestRegistry _registry;
    private readonly Dictionary<string, GroupKind> _kinds = new(StringComparer.Ordinal);
    private readonly NetBitReader _field = new();

    // ---- per replay (reset by Read) ----
    private OpeningResolver _resolver;
    private uint? _recorderPlayerState;
    private uint? _controllerPlayerState;
    private string? _sessionId;
    private readonly Dictionary<uint, uint> _pawnToPlayerState = new();
    private readonly Dictionary<uint, string> _playerAccountId = new();
    private readonly Dictionary<uint, string> _playerName = new();
    private readonly Dictionary<uint, int> _playerTeam = new();
    private readonly HashSet<uint> _playerStateChannels = new();
    private readonly Dictionary<uint, string> _dynamicChestIds = new();
    private readonly List<(string Class, double X, double Y, string Id)> _dynamicChests = new();
    private int _rejectedBlocks;
    private int _decodedContainerUpdates;

    public ChestReader(ChestRegistry? registry = null) : base(NullLogger.Instance, ParseMode.Minimal)
    {
        _registry = registry ?? ChestRegistry.Default;
        _resolver = new OpeningResolver(_registry);
    }

    private static float Now => CurrentTime;

    /// <summary>Scan one replay file. Never throws for bad or partial data; see <see cref="ScanResult.Status"/>.</summary>
    public ScanResult Read(string path)
    {
        var info = new FileInfo(path);
        var result = new ScanResult
        {
            FilePath = path,
            FileSize = info.Exists ? info.Length : 0,
            FileWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
            RegistryVersion = _registry.Version,
        };

        var probe = ReplayProbe.Probe(path);
        result.IsLive = probe.IsLive;
        result.FileVersion = (int)probe.FileVersion;
        result.LengthMs = probe.LengthMs;
        result.StartedUtc = probe.TimestampUtc;
        switch (probe.State)
        {
            case ProbeState.Recording:
                result.Status = ScanStatus.NotReady;
                result.Error = probe.Reason;
                return result;
            case ProbeState.Invalid:
                result.Status = ScanStatus.Corrupt;
                result.Error = probe.Reason;
                return result;
            case ProbeState.UnsupportedVersion:
                result.Status = ScanStatus.Unsupported;
                result.Error = probe.Reason;
                return result;
        }

        Reset();
        byte[] data;
        try
        {
            data = ReadShared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Status = ScanStatus.NotReady;
            result.Error = "could not open the file: " + ex.Message;
            return result;
        }

        return Read(data, result);
    }

    /// <summary>Scan replay bytes already in memory (used by tests; <paramref name="result"/> carries file info).</summary>
    public ScanResult Read(byte[] data, ScanResult result)
    {
        Reset();
        result.RegistryVersion = _registry.Version;
        try
        {
            using var archive = new Unreal.Core.BinaryReader(data.AsMemory());
            ReadReplay(archive);
            result.Status = IsPartialReplay ? ScanStatus.Partial : ScanStatus.Complete;
            if (IsPartialReplay) result.Error = "the file ends in the middle of a data chunk";
        }
        catch (InvalidReplayException ex)
        {
            result.Status = ScanStatus.Corrupt;
            result.Error = ex.Message;
            TryCleanup();
        }
        catch (Exception ex)
        {
            // Keep everything read so far, but never treat it as a final count.
            result.Status = ScanStatus.Partial;
            result.Error = ex.GetType().Name + ": " + ex.Message;
            TryCleanup();
        }

        FillMetadata(result);
        Resolve(result);

        // A build newer than anything this parser knows that also failed to read is "unsupported", not "damaged":
        // it is kept and retried automatically after a parser update.
        if (result.Status is ScanStatus.Partial or ScanStatus.Corrupt && Replay?.Header is { } h
            && (h.EngineNetworkVersion > EngineNetworkVersionHistory.LATEST || h.NetworkVersion > NetworkVersionHistory.LATEST))
        {
            result.Status = ScanStatus.Unsupported;
        }
        else if (result.Status == ScanStatus.Complete && !result.RecorderFound)
        {
            result.Status = ScanStatus.NoRecorder;
            result.Error ??= _decodedContainerUpdates == 0 && _pawnToPlayerState.Count == 0
                ? "no player or chest data could be decoded (new replay layout?)"
                : "the recording player could not be identified";
        }

        return result;
    }

    private void TryCleanup()
    {
        try { Cleanup(); } catch { /* reader state is reset on the next read anyway */ }
    }

    private void Reset()
    {
        _resolver = new OpeningResolver(_registry);
        _recorderPlayerState = null;
        _controllerPlayerState = null;
        _sessionId = null;
        _pawnToPlayerState.Clear();
        _playerAccountId.Clear();
        _playerName.Clear();
        _playerTeam.Clear();
        _playerStateChannels.Clear();
        _dynamicChestIds.Clear();
        _dynamicChests.Clear();
        _rejectedBlocks = 0;
        _decodedContainerUpdates = 0;
        CurrentTime = 0;
    }

    /// <summary>Fortnite may keep the replay open while recording, so allow concurrent writers (read-only for us).</summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > 1L << 30) throw new IOException("file is larger than any real replay");
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    private void FillMetadata(ScanResult result)
    {
        var replay = Replay;
        if (replay is null) return;

        if (replay.Info is { } info)
        {
            result.LengthMs = info.LengthInMs;
            result.IsLive = info.IsLive;
            result.FileVersion = (int)info.FileVersion;
            if (info.Timestamp > new DateTime(2015, 1, 1))
            {
                // Written by the game as local time (matches the "UnsavedReplay-yyyy.MM.dd-HH.mm.ss" file name).
                result.StartedUtc = DateTime.SpecifyKind(info.Timestamp, DateTimeKind.Local).ToUniversalTime();
            }
        }

        if (replay.Header is { } header)
        {
            result.ReplayGuid = string.IsNullOrWhiteSpace(header.Guid) ? null : header.Guid.Trim();
            result.Branch = header.Branch;
            result.Changelist = header.Changelist;
            result.EngineNetworkVersion = (int)header.EngineNetworkVersion;
            result.NetworkVersion = (int)header.NetworkVersion;
        }

        result.MatchSessionId = CleanId(_sessionId);
        result.CorruptBlocksSkipped = _rejectedBlocks;
    }

    private static string? CleanId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim();
        return id.Length is >= 8 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;
    }

    private void Resolve(ScanResult result)
    {
        var recorder = _recorderPlayerState ?? _controllerPlayerState;
        var myPawns = new HashSet<uint>();
        if (recorder is uint ps)
        {
            foreach (var (pawn, owner) in _pawnToPlayerState)
            {
                if (owner == ps) myPawns.Add(pawn);
            }

            if (_playerAccountId.TryGetValue(ps, out var id) && id.Length >= 16 && id != "NULL") result.RecorderAccountId = id.ToLowerInvariant();
            if (_playerName.TryGetValue(ps, out var name)) result.RecorderName = name;
            if (_playerTeam.TryGetValue(ps, out var team)) result.RecorderTeam = team;
        }

        result.RecorderFound = myPawns.Count > 0;

        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var o in _resolver.Openings)
        {
            var cls = _registry.Classify(o.ClassPath);
            if (_registry.Counts(cls) && (!o.Stale || o.StableKey) && o.Opener is > 0)
            {
                result.ChestsOpenedByAnyone++;
            }
        }

        foreach (var o in _resolver.OpenedBy(myPawns))
        {
            var cls = _registry.Classify(o.ClassPath);
            var shortName = ChestRegistry.ShortClassName(o.ClassPath);
            switch (cls.Role)
            {
                case ContainerRole.KnownChest:
                    result.Chests.Add(new ChestRecord(o.Time, cls.Kind, shortName, o.Destroyed, o.Reopened, Unverified: false));
                    break;
                case ContainerRole.UnknownChest:
                    unknown.Add(shortName);
                    if (_registry.CountsUnknownChests)
                    {
                        result.Chests.Add(new ChestRecord(o.Time, cls.Kind, shortName, o.Destroyed, o.Reopened, Unverified: true));
                    }
                    break;
                case ContainerRole.OtherContainer:
                    result.OtherContainers[cls.Kind] = result.OtherContainers.GetValueOrDefault(cls.Kind) + 1;
                    break;
            }
        }

        result.UnknownChestClasses = unknown.ToList();
        result.Chests.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    // ------------------------------------------------------------------------------------------ decoding

    private GroupKind Classify(string path)
    {
        if (_kinds.TryGetValue(path, out var kind)) return kind;

        kind = GroupKind.Ignore;
        // Skip RPC parameter groups ("Class:Function") and ClassNetCache groups.
        if (!path.Contains(':') && !path.EndsWith("ClassNetCache", StringComparison.Ordinal))
        {
            var dot = path.LastIndexOf('.');
            var name = dot >= 0 ? path[(dot + 1)..] : path;

            if (_registry.Classify(path).Role != ContainerRole.None)
                kind = GroupKind.Container;
            else if (name.Contains("PlayerPawn", StringComparison.Ordinal))
                kind = GroupKind.Pawn;
            else if (name.Contains("ReplayPC", StringComparison.Ordinal))
                kind = GroupKind.ReplayController;
            else if (name.Contains("PlayerState", StringComparison.Ordinal) && !name.Contains("Component", StringComparison.Ordinal))
                kind = GroupKind.PlayerState;
            else if (name.Contains("GameState", StringComparison.Ordinal) && !name.Contains("Component", StringComparison.Ordinal))
                kind = GroupKind.GameState;
        }

        _kinds[path] = kind;
        return kind;
    }

    /// <summary>
    /// Map-placed chests have a stable actor name. Spawned chests (buried, relic, ...) don't, and the recording
    /// client gives them a new network id every time they come back into view, so those are identified by class
    /// and spawn position instead (horizontally only: buried chests rise out of the ground).
    /// </summary>
    private (string Id, bool Stable) ChestId(uint actor, FVector? location, string groupPath)
    {
        if (_netGuidCache.TryGetPathName(actor, out var name) && !string.IsNullOrEmpty(name))
        {
            return (name, true);
        }

        if (_dynamicChestIds.TryGetValue(actor, out var known))
        {
            return (known, !known.StartsWith('#'));
        }

        string id;
        if (location is null)
        {
            id = $"#{actor}";
        }
        else
        {
            var cls = ChestRegistry.ShortClassName(groupPath);
            var tolerance = _registry.SameSpawnToleranceCm;
            var match = _dynamicChests.FindIndex(c => c.Class == cls
                && Math.Abs(c.X - location.X) < tolerance
                && Math.Abs(c.Y - location.Y) < tolerance);
            if (match >= 0)
            {
                id = _dynamicChests[match].Id;
            }
            else
            {
                id = $"{cls}@{location.X:F0},{location.Y:F0}";
                _dynamicChests.Add((cls, location.X, location.Y, id));
            }
        }

        _dynamicChestIds[actor] = id;
        return (id, !id.StartsWith('#'));
    }

    /// <summary>
    /// Mirrors Unreal.Core's property reader (handle, bit count, payload) for the classes we care about, decoding
    /// only the fields we need and skipping the rest. See RepLayout.cpp ReceiveProperties_BackwardsCompatible.
    /// </summary>
#pragma warning disable CS8765
    public override bool ReceiveProperties(FBitArchive archive, NetFieldExportGroup group, uint channelIndex,
        out INetFieldExportGroup? exportGroup, bool enablePropertyChecksum = true, bool netDeltaUpdate = false)
#pragma warning restore CS8765
    {
        var kind = netDeltaUpdate ? GroupKind.Ignore : Classify(group.PathName);
        if (kind == GroupKind.Ignore)
        {
            return base.ReceiveProperties(archive, group, channelIndex, out exportGroup, enablePropertyChecksum, netDeltaUpdate);
        }

        exportGroup = null;
        var channel = Channels[channelIndex];
        if (channel is null) return false;

        if (enablePropertyChecksum) archive.ReadBit();

        uint? searchingPawn = null, playerState = null, recorderPlayerState = null, animCount = null;
        bool? searched = null, destroyed = null;
        string? sessionId = null, netId = null;
        int? team = null;

        while (true)
        {
            var handle = archive.ReadIntPacked();
            if (handle == 0) break;

            handle--;
            if (!group.IsValidIndex(handle)) return false;

            var numBits = (int)archive.ReadIntPacked();
            if (numBits == 0) continue;
            if (archive.IsError || numBits > archive.GetBitsLeft()) return false;

            var start = archive.GetBitsLeft();
            switch (kind, group.NetFieldExports[handle]?.Name)
            {
                case (GroupKind.Container, "SearchingPawn"):
                    searchingPawn = archive.ReadIntPacked();
                    break;
                case (GroupKind.Container, "bAlreadySearched"):
                    searched = archive.ReadBit();
                    break;
                case (GroupKind.Container, "bDestroyed"):
                    destroyed = archive.ReadBit();
                    break;
                case (GroupKind.Container, "SearchAnimationCount") when numBits <= 32:
                    animCount = (uint)archive.ReadBitsToInt(numBits);
                    break;
                case (GroupKind.Pawn, "PlayerState"):
                case (GroupKind.ReplayController, "PlayerState"):
                    playerState = archive.ReadIntPacked();
                    break;
                case (GroupKind.GameState, "RecorderPlayerState"):
                    recorderPlayerState = archive.ReadIntPacked();
                    break;
                case (GroupKind.GameState, "GameSessionId"):
                    sessionId = ReadString(archive, numBits);
                    break;
                case (GroupKind.PlayerState, "UniqueId"):
                case (GroupKind.PlayerState, "UniqueID"):
                    netId = ReadNetId(archive, numBits);
                    break;
                case (GroupKind.PlayerState, "TeamIndex") when numBits <= 32:
                    team = archive.ReadBitsToInt(numBits);
                    break;
            }

            var consumed = start - archive.GetBitsLeft();
            if (consumed > numBits || archive.IsError) return false;
            if (consumed < numBits) archive.SkipBits(numBits - consumed);
        }

        switch (kind)
        {
            case GroupKind.Container when channel.ActorId is uint actor
                                          && (searchingPawn != null || searched != null || destroyed != null || animCount != null):
                var (id, stable) = ChestId(actor, channel.Actor?.Location, group.PathName);
                _decodedContainerUpdates++;
                _resolver.Observe(new ContainerUpdate(Now, channelIndex, id, stable, group.PathName, searchingPawn, searched, destroyed, animCount));
                break;
            case GroupKind.Pawn when playerState is > 0 && channel.ActorId is uint pawn:
                _pawnToPlayerState[pawn] = playerState.Value;
                break;
            case GroupKind.ReplayController when playerState is > 0:
                _controllerPlayerState ??= playerState;
                break;
            case GroupKind.GameState:
                if (recorderPlayerState is > 0) _recorderPlayerState ??= recorderPlayerState;
                if (!string.IsNullOrWhiteSpace(sessionId)) _sessionId ??= sessionId;
                break;
            case GroupKind.PlayerState when channel.ActorId is uint stateActor:
                _playerStateChannels.Add(channelIndex);
                if (!string.IsNullOrEmpty(netId)) _playerAccountId[stateActor] = netId;
                if (team is not null) _playerTeam[stateActor] = team.Value;
                break;
        }

        return true;
    }

    private string? ReadString(FBitArchive archive, int numBits)
    {
        _field.FillBitsFrom(archive, numBits);
        _field.EngineNetworkVersion = archive.EngineNetworkVersion;
        try
        {
            var s = _field.ReadFString();
            return _field.IsError ? null : s;
        }
        catch
        {
            return null;
        }
    }

    private string? ReadNetId(FBitArchive archive, int numBits)
    {
        _field.FillBitsFrom(archive, numBits);
        _field.EngineNetworkVersion = archive.EngineNetworkVersion;
        try
        {
            var s = _field.SerializePropertyNetId();
            return _field.IsError ? null : s;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnExternalDataRead(uint channelIndex, IExternalData? externalData)
    {
        // Player names arrive as external data on the player state's channel (obfuscated with a fixed shift).
        if (externalData is not ExternalData { Archive: { } archive } || !_playerStateChannels.Contains(channelIndex)
            || Channels[channelIndex]?.ActorId is not uint stateActor)
        {
            return;
        }

        try
        {
            archive.ReadByte(); // handle
            archive.ReadByte(); // unknown
            var isPlayer = archive.ReadBoolean();
            var encoded = archive.ReadFString();
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 64) return;
            var decoded = encoded;
            if (isPlayer)
            {
                var chars = new char[encoded.Length];
                for (var i = 0; i < encoded.Length; i++)
                {
                    var shift = (encoded.Length % 4 * 3 % 8 + 1 + i) * 3 % 8;
                    chars[i] = (char)(encoded[i] + shift);
                }
                decoded = new string(chars);
            }
            _playerName[stateActor] = decoded;
        }
        catch
        {
            // a name is a nice-to-have; never let it affect counting
        }
    }

    protected override void OnChannelOpened(uint channelIndex, NetworkGUID? actor)
    {
        base.OnChannelOpened(channelIndex, actor);
        _resolver.ChannelOpened(channelIndex, Now);
    }

    protected override void OnChannelClosed(uint channelIndex, NetworkGUID? actor)
    {
        base.OnChannelClosed(channelIndex, actor);
        _resolver.ChannelClosed(channelIndex);
        _playerStateChannels.Remove(channelIndex);
    }

    // ------------------------------------------------------------------- robustness against bad data

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
            _rejectedBlocks++;
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

    // ------------------------------------------------------------------------- callbacks we don't use

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
    }

    protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update)
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
