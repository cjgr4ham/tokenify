// TOKENIFY Chest Tracker - chest counting from Fortnite replays.
// Parts of this file are adapted from rossalv/fortnite-chest-counter (MIT, src/ChestReplayReader/ChestReader.cs:
// hand-decoding of the few needed fields, chest identities) and from LED-N/Fortnite-ChestCounter (MIT,
// src/ChestCounter/ReplayAnalyzer.cs: opening rules, see OpeningResolver). Licence texts are in companion/lib/.
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Exceptions;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace Tokenify.ReplayReader;

/// <summary>
/// Reads one Fortnite .replay file and finds the chests opened or destroyed by the player who recorded it.
///
/// Attribution: every searchable container replicates <c>SearchingPawn</c> (the pawn that searched or destroyed
/// it) next to <c>bAlreadySearched</c> / <c>bDestroyed</c>. The game state replicates <c>RecorderPlayerState</c>
/// and each player pawn replicates its <c>PlayerState</c>, so we know which interactions were made by the recording
/// player's pawn(s). Only the handful of fields needed are decoded (minimal parse mode), which keeps a replay to
/// about a second. What the camera was looking at plays no part: everything replicated to the recording client is
/// read, whether or not it was ever on screen.
///
/// Besides the chests' own records, three more things are collected as supporting evidence (see
/// <see cref="EvidenceAnalyzer"/>): damage cues that name a chest (who hit it, with what), what the recording
/// player had in hand, and chests that vanished without any record. None of them can add a chest on its own.
/// </summary>
public sealed class ChestReader : FortniteReplayBase
{
    private enum GroupKind { Ignore, GameState, PlayerState, Pawn, ReplayController, Container, Traced }

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
    private readonly Dictionary<uint, (float At, uint Actor)> _channelOpen = new();
    private readonly Dictionary<uint, (string Key, string Class, bool Stable)> _containerChannels = new();
    private readonly HashSet<uint> _tracedChannels = new();
    private readonly Dictionary<string, bool> _extraGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _channelClass = new();
    private readonly Dictionary<uint, float> _lastPositionAt = new();
    private readonly HashSet<uint> _actorChannels = new();
    private readonly Dictionary<string, bool> _capturedRpcs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NetFieldExportGroup?> _functionGroups = new(StringComparer.Ordinal);
    private readonly NetBitReader _rpc = new();
    private int _decodedContainerUpdates;
    private uint _currentChannel;

    // ---- supporting evidence (always collected; small) ----
    private const string DamageCueFunction = "NetMulticast_Athena_BatchedDamageCues";
    private const string AbilityCueFunction = "NetMulticast_InvokeGameplayCueExecuted_WithParams";
    private readonly Dictionary<uint, uint> _actorArchetype = new();
    private readonly List<(float Time, uint Pawn, uint Weapon)> _weaponChanges = new();
    private readonly List<(float Time, uint Pawn, uint Target, uint Weapon, float Magnitude, bool Fatal)> _hits = new();
    private readonly List<AbilityCue> _cues = new();
    private readonly List<ContainerRemoval> _removals = new();
    private readonly NetBitReader _cue = new();
    private NetFieldExportGroup? _damageCueGroup;
    private float _damageCueLookupAt = float.MinValue;
    private readonly Dictionary<uint, string?> _resolvedClass = new();
    private readonly Dictionary<uint, string?> _resolvedKey = new();
    private bool _namesResolved;

    /// <summary>What the last read collected, before classification (tests and diagnostics).</summary>
    public ReplayObservations? LastObservations { get; private set; }

    /// <summary>Extra recording for the audit viewer, diagnostics and research. Null = count only.</summary>
    public TraceOptions? Trace { get; init; }

    /// <summary>The trace of the last replay read (when <see cref="Trace"/> was set).</summary>
    public ReplayTrace? LastTrace { get; private set; }

    public ChestReader(ChestRegistry? registry = null)
    {
        _registry = registry ?? ChestRegistry.Default;
        _resolver = new OpeningResolver(_registry);
    }

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
        var readFailed = false;
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
            readFailed = true;
        }
        catch (Exception ex)
        {
            // Keep everything read so far, but never treat it as a final count.
            result.Status = ScanStatus.Partial;
            result.Error = ex.GetType().Name + ": " + ex.Message;
            readFailed = true;
        }

        // Names are looked up while the reader's caches still hold them; only then is a failed read cleaned up.
        FillMetadata(result);
        Resolve(result);
        if (result.Status is ScanStatus.Corrupt or ScanStatus.Partial) TryCleanup();

        // A build newer than anything this parser knows that also failed to read is "unsupported", not "damaged":
        // it is kept and retried automatically after a parser update. A file that simply ends early was read
        // correctly as far as it goes: that is a partial replay whatever its version.
        if (readFailed && result.Status is ScanStatus.Partial or ScanStatus.Corrupt && Replay?.Header is { } h
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
        _channelOpen.Clear();
        _containerChannels.Clear();
        _tracedChannels.Clear();
        _channelClass.Clear();
        _lastPositionAt.Clear();
        _actorChannels.Clear();
        _functionGroups.Clear();
        _actorArchetype.Clear();
        _weaponChanges.Clear();
        _hits.Clear();
        _cues.Clear();
        _removals.Clear();
        _damageCueGroup = null;
        _damageCueLookupAt = float.MinValue;
        _resolvedClass.Clear();
        _resolvedKey.Clear();
        _namesResolved = false;
        LastObservations = null;
        RejectedCorruptBlocks = 0;
        _decodedContainerUpdates = 0;
        CurrentTime = 0;
        LastTrace = Trace is null ? null : new ReplayTrace();
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
        result.CorruptBlocksSkipped = RejectedCorruptBlocks;
    }

    private static string? CleanId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim();
        return id.Length is >= 8 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;
    }

    /// <summary>Pawns of the recording player, of their teammates, and everyone's team (for labelling evidence).</summary>
    public (HashSet<uint> Mine, HashSet<uint> Mates) Pawns()
    {
        var mine = new HashSet<uint>();
        var mates = new HashSet<uint>();
        var recorder = _recorderPlayerState ?? _controllerPlayerState;
        if (recorder is not uint ps) return (mine, mates);
        var myTeam = _playerTeam.TryGetValue(ps, out var t) ? t : (int?)null;
        foreach (var (pawn, owner) in _pawnToPlayerState)
        {
            if (owner == ps) mine.Add(pawn);
            else if (myTeam is int team && _playerTeam.TryGetValue(owner, out var ot) && ot == team) mates.Add(pawn);
        }
        return (mine, mates);
    }

    private void Resolve(ScanResult result)
    {
        var recorder = _recorderPlayerState ?? _controllerPlayerState;
        var (myPawns, _) = Pawns();
        if (recorder is uint ps)
        {
            if (_playerAccountId.TryGetValue(ps, out var id) && id.Length >= 16 && id != "NULL") result.RecorderAccountId = id.ToLowerInvariant();
            if (_playerName.TryGetValue(ps, out var name)) result.RecorderName = name;
            if (_playerTeam.TryGetValue(ps, out var team)) result.RecorderTeam = team;
        }

        result.RecorderFound = myPawns.Count > 0;

        ResolveNames();
        var observations = new ReplayObservations
        {
            Openings = _resolver.Openings,
            Removals = _removals.ToList(),
            Hits = _hits.Select(h => (h, Key: KeyOf(h.Target))).Where(x => x.Key is not null)
                .Select(x => new ContainerHit(x.h.Time, x.h.Pawn, x.Key!, ClassOfActor(x.h.Weapon), x.h.Magnitude, x.h.Fatal)).ToList(),
            Weapons = _weaponChanges.Where(w => myPawns.Contains(w.Pawn)).Select(w => new WeaponChange(w.Time, w.Pawn, ClassOfActor(w.Weapon))).ToList(),
            Cues = _cues.Where(c => myPawns.Contains(c.Pawn)).ToList(),
            MyPawns = myPawns,
        };
        LastObservations = observations;

        var report = EvidenceAnalyzer.Analyze(observations, _registry);
        result.Events = report.Events;
        result.Unobservable = report.Unobservable;
        result.ChestsOpenedByAnyone = report.OpenedByAnyone;
        result.OtherContainers = report.OtherContainers;
        result.UnknownChestClasses = report.UnknownChestClasses.ToList();
        result.Chests = report.Events.Where(e => e.Counted)
            .Select(e => new ChestRecord(e.Time, e.Kind, e.ClassName, e.Action == ChestAction.Destroyed, e.Reopened, e.Unverified)).ToList();
    }

    /// <summary>
    /// The library empties its name tables as soon as a replay has been read, so the few names the evidence needs
    /// (weapons held, chests hit) are looked up just before that happens.
    /// </summary>
    protected override void Cleanup()
    {
        ResolveNames();
        base.Cleanup();
    }

    private void ResolveNames()
    {
        if (_namesResolved) return;
        _namesResolved = true;
        foreach (var w in _weaponChanges) _resolvedClass.TryAdd(w.Weapon, LookupClass(w.Weapon));
        foreach (var h in _hits)
        {
            _resolvedClass.TryAdd(h.Weapon, LookupClass(h.Weapon));
            _resolvedKey.TryAdd(h.Target, LookupKey(h.Target));
        }
    }

    private string? KeyOf(uint actor) => _resolvedKey.GetValueOrDefault(actor);

    private string? ClassOfActor(uint actor) => _resolvedClass.GetValueOrDefault(actor);

    /// <summary>The chest identity a network id refers to (map-placed: its path; spawned: class and position).</summary>
    private string? LookupKey(uint actor)
    {
        if (actor == 0) return null;
        if (_netGuidCache.TryGetPathName(actor, out var path) && !string.IsNullOrEmpty(path)) return path;
        return _dynamicChestIds.TryGetValue(actor, out var id) ? id : null;
    }

    /// <summary>Short class name of a spawned actor (a weapon, usually), or null.</summary>
    private string? LookupClass(uint actor)
    {
        if (actor == 0) return null;
        if (_actorArchetype.TryGetValue(actor, out var archetype) && _netGuidCache.TryGetPathName(archetype, out var path) && !string.IsNullOrEmpty(path))
        {
            return ChestRegistry.ShortClassName(path);
        }
        return _netGuidCache.TryGetPathName(actor, out var own) && !string.IsNullOrEmpty(own) ? ChestRegistry.ShortClassName(own) : null;
    }

    private bool IsContainerActor(uint actor)
    {
        if (actor == 0) return false;
        if (_dynamicChestIds.ContainsKey(actor)) return true;
        if (_netGuidCache.TryGetPathName(actor, out var path) && !string.IsNullOrEmpty(path)) return _registry.Classify(path).Role != ContainerRole.None;
        return _actorArchetype.TryGetValue(actor, out var archetype) && _netGuidCache.TryGetPathName(archetype, out var cls)
            && !string.IsNullOrEmpty(cls) && _registry.Classify(cls).Role != ContainerRole.None;
    }

    /// <summary>
    /// A damage cue is a remote call on the attacking character: what was hit, how hard, with which weapon. Its
    /// parameters are sent like properties (handle, bit count, payload). Only cues that name a container are kept.
    /// </summary>
    private void ReadDamageCue(FBitArchive archive, int bits)
    {
        if (_damageCueGroup is null)
        {
            if (Now - _damageCueLookupAt < 1f) return;
            _damageCueLookupAt = Now;
            var suffix = ":" + DamageCueFunction;
            _damageCueGroup = _netGuidCache.NetFieldExportGroupMap.FirstOrDefault(kv => kv.Key.EndsWith(suffix, StringComparison.Ordinal)).Value;
            if (_damageCueGroup is null) return;
        }
        if (Channels[_currentChannel]?.ActorId is not uint pawn) return;

        var group = _damageCueGroup;
        _cue.FillBitsFrom(archive, bits);
        _cue.EngineNetworkVersion = archive.EngineNetworkVersion;
        uint hit = 0, other = 0, weapon = 0;
        float magnitude = 0;
        var fatal = false;
        try
        {
            _cue.ReadBit(); // property checksum flag
            for (var guard = 0; guard < 64 && !_cue.AtEnd() && !_cue.IsError; guard++)
            {
                var handle = _cue.ReadIntPacked();
                if (handle == 0) break;
                handle--;
                if (!group.IsValidIndex(handle)) return;
                var numBits = (int)_cue.ReadIntPacked();
                if (numBits == 0) continue;
                if (_cue.IsError || numBits > _cue.GetBitsLeft()) return;
                if (numBits > 64)
                {
                    _cue.SkipBits(numBits);
                    continue;
                }
                var raw = _cue.ReadBitsToLong(numBits);
                switch (group.NetFieldExports[handle]?.Name)
                {
                    case "HitActor": hit = PackedInt(raw, numBits) ?? 0; break;
                    case "NonPlayerHitActor": other = PackedInt(raw, numBits) ?? 0; break;
                    case "SourceWeapon": weapon = PackedInt(raw, numBits) ?? 0; break;
                    case "Magnitude" when numBits == 32: magnitude = BitConverter.Int32BitsToSingle((int)raw); break;
                    case "bIsFatal":
                    case "NonPlayerbIsFatal":
                        fatal |= (raw & 1) != 0;
                        break;
                }
            }
        }
        catch
        {
            return; // supporting evidence only: never let it affect the count
        }

        if (IsContainerActor(hit)) _hits.Add((Now, pawn, hit, weapon, magnitude, fatal));
        if (other != hit && IsContainerActor(other)) _hits.Add((Now, pawn, other, weapon, magnitude, fatal));
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
            else if (Trace?.ExtraGroups?.IsMatch(path) == true)
                kind = GroupKind.Traced;
        }

        _kinds[path] = kind;
        return kind;
    }

    private bool IsExtra(string path)
    {
        if (Trace?.ExtraGroups is not { } extra) return false;
        if (!_extraGroups.TryGetValue(path, out var yes)) _extraGroups[path] = yes = extra.IsMatch(path);
        return yes;
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

    private static uint? PackedInt(ulong raw, int bits)
    {
        if (bits is < 8 or > 40 || bits % 8 != 0) return null;
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = (uint)(raw & 0xFF);
            raw >>= 8;
            value |= (b >> 1) << shift;
            if ((b & 1) == 0) return value;
        }
        return null;
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
        if (Trace?.Census == true && !netDeltaUpdate)
        {
            LastTrace!.Census[group.PathName] = LastTrace.Census.GetValueOrDefault(group.PathName) + 1;
        }

        if (kind == GroupKind.Ignore)
        {
            return base.ReceiveProperties(archive, group, channelIndex, out exportGroup, enablePropertyChecksum, netDeltaUpdate);
        }

        exportGroup = null;
        var channel = Channels[channelIndex];
        if (channel is null) return false;

        if (enablePropertyChecksum) archive.ReadBit();

        var byActor = !netDeltaUpdate && _actorChannels.Contains(channelIndex);
        if (byActor && kind == GroupKind.Ignore) kind = GroupKind.Traced;
        var extra = kind == GroupKind.Traced || byActor || (Trace is not null && !netDeltaUpdate && IsExtra(group.PathName));
        var tracing = Trace is not null && (extra || (kind == GroupKind.Container && Trace.Containers));
        List<TraceField>? traced = tracing ? new List<TraceField>() : null;
        var keepAll = kind == GroupKind.Container || byActor || Trace?.FieldFilter is null;

        uint? searchingPawn = null, playerState = null, recorderPlayerState = null, animCount = null, heldWeapon = null;
        bool? searched = null, destroyed = null, instant = null;
        var damageContext = false;
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

            var name = group.NetFieldExports[handle]?.Name;
            var start = archive.GetBitsLeft();
            switch (kind, name)
            {
                case (GroupKind.GameState, "GameSessionId"):
                    sessionId = ReadString(archive, numBits);
                    break;
                case (GroupKind.PlayerState, "UniqueId"):
                case (GroupKind.PlayerState, "UniqueID"):
                    netId = ReadNetId(archive, numBits);
                    break;
                case (GroupKind.Pawn, "ReplicatedMovement") when Trace?.Movement == true && channel.ActorId is uint mover:
                    TracePosition(archive, numBits, mover);
                    break;
                default:
                    var keep = traced is not null && (keepAll || (name is not null && Trace!.FieldFilter!.IsMatch(name)));
                    if (numBits > 64)
                    {
                        if (kind == GroupKind.Container && name == "EffectContext") damageContext = true;
                        if (keep && numBits <= 16384)
                        {
                            var bytes = archive.ReadBits(numBits).ToArray();
                            traced!.Add(new TraceField(name ?? "?" + handle, numBits, 0, bytes, name == "EffectContext" ? DescribeEffectContext(bytes, numBits) : null));
                        }
                        break;
                    }
                    var raw = archive.ReadBitsToLong(numBits);
                    if (keep) traced!.Add(new TraceField(name ?? "?" + handle, numBits, raw));
                    switch (kind, name)
                    {
                        case (GroupKind.Container, "SearchingPawn"):
                            searchingPawn = PackedInt(raw, numBits);
                            break;
                        case (GroupKind.Container, "bAlreadySearched"):
                            searched = (raw & 1) != 0;
                            break;
                        case (GroupKind.Container, "bDestroyed"):
                            destroyed = (raw & 1) != 0;
                            break;
                        case (GroupKind.Container, "bInstantDeath"):
                            instant = (raw & 1) != 0;
                            break;
                        case (GroupKind.Container, "EffectContext"):
                            damageContext = true;
                            break;
                        case (GroupKind.Pawn, "CurrentWeapon"):
                            heldWeapon = PackedInt(raw, numBits);
                            break;
                        case (GroupKind.Container, "SearchAnimationCount") when numBits <= 32:
                            animCount = (uint)raw;
                            break;
                        case (GroupKind.Pawn, "PlayerState"):
                        case (GroupKind.ReplayController, "PlayerState"):
                            playerState = PackedInt(raw, numBits);
                            break;
                        case (GroupKind.GameState, "RecorderPlayerState"):
                            recorderPlayerState = PackedInt(raw, numBits);
                            break;
                        case (GroupKind.PlayerState, "TeamIndex") when numBits <= 32:
                            team = (int)raw;
                            break;
                    }
                    break;
            }

            var consumed = start - archive.GetBitsLeft();
            if (consumed > numBits || archive.IsError) return false;
            if (consumed < numBits)
            {
                archive.SkipBits(numBits - consumed);
            }
        }

        switch (kind)
        {
            case GroupKind.Container when channel.ActorId is uint actor:
                if (!_containerChannels.TryGetValue(channelIndex, out var cc))
                {
                    var (id0, stable0) = ChestId(actor, channel.Actor?.Location, group.PathName);
                    cc = (id0, group.PathName, stable0);
                    _containerChannels[channelIndex] = cc;
                    TraceOpen(channelIndex, actor, group.PathName, id0, channel.Actor?.Location);
                }
                if (searchingPawn != null || searched != null || destroyed != null || animCount != null)
                {
                    _decodedContainerUpdates++;
                    _resolver.Observe(new ContainerUpdate(Now, channelIndex, cc.Key, cc.Stable, group.PathName, searchingPawn, searched, destroyed, animCount, instant, damageContext));
                }
                break;
            case GroupKind.Traced when channel.ActorId is uint tactor:
                if (_tracedChannels.Add(channelIndex)) TraceOpen(channelIndex, tactor, group.PathName, null, channel.Actor?.Location);
                break;
            case GroupKind.Pawn when channel.ActorId is uint pawn:
                if (playerState is > 0) _pawnToPlayerState[pawn] = playerState.Value;
                if (heldWeapon is uint held) _weaponChanges.Add((Now, pawn, held));
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

        if (traced is { Count: > 0 } && channel.ActorId is uint a)
        {
            LastTrace!.Updates.Add(new TraceUpdate(Now, channelIndex, a, group.PathName, traced.ToArray()));
            if (!LastTrace.GroupFields.ContainsKey(group.PathName))
            {
                LastTrace.GroupFields[group.PathName] = group.NetFieldExports.Where(e => e is not null).Select(e => e!.Name).ToArray();
            }
            NotePaths(traced);
        }

        return true;
    }

    /// <summary>
    /// Research: the start of a gameplay effect context names who caused the damage and with what. Layout (engine
    /// GameplayEffectTypes.cpp): 1 bit "valid", 7 flag bits, then one object reference per set flag in the order
    /// instigator, effect causer, ability, source object. The rest (hit result...) is not decoded.
    /// </summary>
    private string? DescribeEffectContext(byte[] data, int bits)
    {
        try
        {
            var r = new NetBitReader(data, bits);
            if (!r.ReadBit()) return "empty";
            var flags = r.ReadBitsToInt(7);
            string[] names = { "instigator", "causer", "ability", "source" };
            var parts = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                if ((flags & (1 << i)) == 0) continue;
                var guid = r.ReadIntPacked();
                if (r.IsError) return null;
                var label = _netGuidCache.TryGetPathName(guid, out var p) && !string.IsNullOrEmpty(p) ? p
                    : LastTrace is not null && LastTrace.Paths.TryGetValue(guid, out var a) ? a : "";
                parts.Add($"{names[i]}=#{guid}{(label.Length > 0 ? ":" + ChestRegistry.ShortClassName(label) : "")}");
            }
            return $"flags=0x{flags:X2} " + string.Join(" ", parts);
        }
        catch
        {
            return null;
        }
    }

    private void TracePosition(FBitArchive archive, int numBits, uint actor)
    {
        if (LastTrace is null || (_lastPositionAt.TryGetValue(actor, out var last) && Now - last < 1f)) return;
        _field.FillBitsFrom(archive, numBits);
        _field.EngineNetworkVersion = archive.EngineNetworkVersion;
        try
        {
            var m = _field.SerializeRepMovement();
            if (_field.IsError || m.Location is not { } l) return;
            _lastPositionAt[actor] = Now;
            LastTrace.Positions.Add(new TracePosition(Now, actor, l.X, l.Y, l.Z));
        }
        catch
        {
            // research only
        }
    }

    private void NotePaths(List<TraceField> fields)
    {
        foreach (var f in fields)
        {
            if (f.AsNetGuid() is uint g && g > 0 && !LastTrace!.Paths.ContainsKey(g) && _netGuidCache.TryGetPathName(g, out var p)) LastTrace.Paths[g] = p;
        }
    }

    /// <summary>
    /// Research: decode the parameters of one remote call. They are sent like properties (handle, bit count,
    /// payload) against the export group named "Class:Function".
    /// </summary>
    private void CaptureRpc(FBitArchive archive, int bits, string function)
    {
        if (!_functionGroups.TryGetValue(function, out var group))
        {
            var suffix = ":" + function;
            group = _netGuidCache.NetFieldExportGroupMap.FirstOrDefault(kv => kv.Key.EndsWith(suffix, StringComparison.Ordinal)).Value;
            if (group is not null) _functionGroups[function] = group; // not cached while unknown: it may be exported later
        }
        if (group is null || Channels[_currentChannel]?.ActorId is not uint actor) return;

        _rpc.FillBitsFrom(archive, bits);
        _rpc.EngineNetworkVersion = archive.EngineNetworkVersion;
        var fields = new List<TraceField>();
        try
        {
            _rpc.ReadBit(); // property checksum flag
            while (!_rpc.AtEnd() && !_rpc.IsError && fields.Count < 256)
            {
                var handle = _rpc.ReadIntPacked();
                if (handle == 0) break;
                handle--;
                if (!group.IsValidIndex(handle)) break;
                var numBits = (int)_rpc.ReadIntPacked();
                if (numBits == 0) continue;
                if (_rpc.IsError || numBits > _rpc.GetBitsLeft()) break;
                var name = group.NetFieldExports[handle]?.Name ?? "?" + handle;
                if (numBits <= 64) fields.Add(new TraceField(name, numBits, _rpc.ReadBitsToLong(numBits)));
                else fields.Add(new TraceField(name, numBits, 0, _rpc.ReadBits(numBits).ToArray()));
            }
        }
        catch
        {
            // research only
        }

        if (fields.Count == 0) return;
        NotePaths(fields);
        LastTrace!.Updates.Add(new TraceUpdate(Now, _currentChannel, actor, group.PathName, fields.ToArray()));
    }

    private void TraceOpen(uint channelIndex, uint actor, string cls, string? key, FVector? loc)
    {
        if (LastTrace is null) return;
        _tracedChannels.Add(channelIndex);
        var at = _channelOpen.TryGetValue(channelIndex, out var o) ? o.At : Now;
        LastTrace.Channels.Add(new TraceChannelEvent(at, channelIndex, actor, "open", null, cls, key, loc?.X, loc?.Y, loc?.Z));
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
        _channelOpen[channelIndex] = (Now, actor?.Value ?? 0);
        _containerChannels.Remove(channelIndex);
        _tracedChannels.Remove(channelIndex);
        _channelClass.Remove(channelIndex);
        _actorChannels.Remove(channelIndex);
        _resolver.ChannelOpened(channelIndex, Now);
        if (actor?.Value is uint spawned && spawned != 0 && Channels[channelIndex]?.ArchetypeId is uint spawnedFrom && spawnedFrom != 0)
        {
            _actorArchetype[spawned] = spawnedFrom;
        }

        if (LastTrace is not null && actor?.Value is uint id && id != 0)
        {
            var channel = Channels[channelIndex];
            // Map-placed actors are known by their own path; spawned ones by the class they were spawned from.
            if (_netGuidCache.TryGetPathName(id, out var own) && !string.IsNullOrEmpty(own))
            {
                _channelClass[channelIndex] = own;
                if (Trace!.Actors?.IsMatch(own) == true)
                {
                    _actorChannels.Add(channelIndex);
                    _tracedChannels.Add(channelIndex);
                    LastTrace.Paths[id] = own;
                    LastTrace.Channels.Add(new TraceChannelEvent(Now, channelIndex, id, "open", null, own, own, null, null, null));
                }
            }
            else if (channel?.ArchetypeId is uint arch && _netGuidCache.TryGetPathName(arch, out var archetype) && !string.IsNullOrEmpty(archetype))
            {
                _channelClass[channelIndex] = archetype;
                LastTrace.Paths[id] = archetype;
                if (Trace!.Census) LastTrace.SpawnCensus[archetype] = LastTrace.SpawnCensus.GetValueOrDefault(archetype) + 1;
                if (Trace.Spawns?.IsMatch(archetype) == true && channel?.Actor?.Location is { } at)
                {
                    LastTrace.Spawns.Add(new TraceSpawn(Now, channelIndex, id, archetype, at.X, at.Y, at.Z));
                }
            }
        }
    }

    /// <summary>
    /// The library clears the channel before reporting a close, so the close (and why: destroyed, out of range,
    /// dormant...) is captured here, after the bunch's last property updates have been read.
    /// </summary>
    public override bool ReceivedSequencedBunch(DataBunch bunch)
    {
        var closing = bunch.bClose;
        var reason = bunch.CloseReason;
        var index = bunch.ChIndex;
        var actor = Channels[index]?.ActorId ?? 0;
        var result = base.ReceivedSequencedBunch(bunch);
        if (closing)
        {
            if (_containerChannels.Remove(index, out var cc))
            {
                var touched = _resolver.ChannelClosed(index);
                // A map-placed chest that was seen closed and then stops existing, with no opened or destroyed
                // state in between. (Spawned chests do this whenever they go out of range: not recorded.)
                if (!touched && reason == ChannelCloseReason.Destroyed && actor != 0 && _netGuidCache.TryGetPathName(actor, out var placed) && !string.IsNullOrEmpty(placed))
                {
                    _removals.Add(new ContainerRemoval(Now, cc.Key, cc.Class, SeenIntact: true));
                }
                LastTrace?.Channels.Add(new TraceChannelEvent(Now, index, actor, "close", reason.ToString(), cc.Class, cc.Key, null, null, null));
            }
            else if (reason == ChannelCloseReason.Destroyed && _channelOpen.TryGetValue(index, out var bare) && bare.Actor != 0
                && _netGuidCache.TryGetPathName(bare.Actor, out var gone) && !string.IsNullOrEmpty(gone) && _registry.Classify(gone).Role != ContainerRole.None)
            {
                // The replay only says this map-placed container no longer exists: no state was ever sent for it.
                _removals.Add(new ContainerRemoval(Now, gone, gone, SeenIntact: false));
            }
            else if (_tracedChannels.Contains(index))
            {
                var closedActor = actor != 0 ? actor : _channelOpen.TryGetValue(index, out var co) ? co.Actor : 0;
                LastTrace?.Channels.Add(new TraceChannelEvent(Now, index, closedActor, "close", reason.ToString(), _channelClass.GetValueOrDefault(index), null, null, null, null));
            }
            else if (LastTrace is not null && Trace!.Containers && _channelClass.TryGetValue(index, out var bareClass)
                && _registry.Classify(bareClass).Role != ContainerRole.None && _channelOpen.TryGetValue(index, out var opened))
            {
                // A container whose channel opened and closed without any state: on its own this says nothing
                // about who (if anyone) opened it. Recorded so that such cases can be measured.
                LastTrace.Channels.Add(new TraceChannelEvent(opened.At, index, opened.Actor, "open", null, bareClass, bareClass, null, null, null, Bare: true));
                LastTrace.Channels.Add(new TraceChannelEvent(Now, index, opened.Actor, "close", reason.ToString(), bareClass, bareClass, null, null, null, Bare: true));
            }
            _channelClass.Remove(index);
            _tracedChannels.Remove(index);
            _playerStateChannels.Remove(index);
            _channelOpen.Remove(index);
        }
        return result;
    }

    public override bool ReceivedReplicatorBunch(DataBunch bunch, FBitArchive archive, uint? repObject, bool bHasRepLayout)
    {
        _currentChannel = bunch.ChIndex;
        return base.ReceivedReplicatorBunch(bunch, archive, repObject, bHasRepLayout);
    }

    /// <summary>Research: which RPCs / custom fields fire on traced channels (names and sizes only).</summary>
    public override bool ReadFieldHeaderAndPayload(FBitArchive archive, NetFieldExportGroup group, out NetFieldExport? outField, out uint? payload)
    {
        var ok = base.ReadFieldHeaderAndPayload(archive, group, out outField, out payload);
        if (ok && outField is not null && payload is uint size && size > 0)
        {
            if (outField.Name == DamageCueFunction)
            {
                archive.Mark();
                try { ReadDamageCue(archive, (int)size); }
                finally { archive.Pop(); }
            }
            else if (outField.Name == AbilityCueFunction && Channels[_currentChannel]?.ActorId is uint cuePawn
                && group.PathName.Contains("PlayerPawn", StringComparison.Ordinal))
            {
                _cues.Add(new AbilityCue(Now, cuePawn));
            }
        }
        if (ok && outField is not null && Trace?.RpcCensus == true)
        {
            var key = group.PathName + "." + outField.Name;
            LastTrace!.RpcCensus[key] = LastTrace.RpcCensus.GetValueOrDefault(key) + 1;
        }
        if (ok && outField is not null && payload is uint bits && bits > 0 && Trace?.RpcCapture is { } capture && LastTrace is not null)
        {
            var key = group.PathName + "." + outField.Name;
            if (!_capturedRpcs.TryGetValue(key, out var wanted)) _capturedRpcs[key] = wanted = capture.IsMatch(key);
            if (wanted)
            {
                archive.Mark();
                try { CaptureRpc(archive, (int)bits, outField.Name); }
                finally { archive.Pop(); }
            }
        }
        if (ok && LastTrace is not null && outField is not null && _tracedChannels.Contains(_currentChannel))
        {
            LastTrace.Rpcs.Add(new TraceRpc(Now, _currentChannel, group.PathName, outField.Name, (int)(payload ?? 0)));
        }
        return ok;
    }

    protected override void OnNetGuidExported(uint netGuid, string pathName)
    {
        if (Trace?.Keywords?.IsMatch(pathName) == true) LastTrace!.KeywordPaths.Add(pathName);
    }
}
