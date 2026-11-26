using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Oxide.Plugins
{
    [Info("Admin Live Map", "Guerisse", "1.1.0")]
    [Description("Feeds the Admin Live Map web page over RCON: players, trails, deaths, bases and the map image")]
    public class AdminLiveMap : RustPlugin
    {
        // The web page (LiveMap.html) connects to the server's WebRCON and calls these console commands:
        //   livemap.players            online players (small, asked every 2 seconds)
        //   livemap.sleepers           offline sleepers (asked only when they changed)
        //   livemap.deaths <afterId>   deaths newer than the given id
        //   livemap.trails [steamId]   where players walked recently (all players thinned out, or one in full)
        //   livemap.bases              tool cupboards and the size of their buildings
        //   livemap.mapdata            the map image (rendered once per map, then cached)
        // For people typing in the server console:
        //   livemap.map [rerender]     shows whether the map image is ready, or draws it again
        //   livemap.benchmark [online] [sleepers] [bases]   load test with made-up players and bases
        // They only work from RCON and the server console. Calls from a player's in-game F1 console are
        // ignored, because the answers reveal every player's position and base.
        //
        // Built for big servers: the frequent answer only holds online players, everything else is sent
        // when it changed, and building sizes are measured a few at a time in the background.

        private const float GridCell = 1024f / 7f;        // Rust grid squares are about 146.3 m
        private const float PlayersCacheSeconds = 1f;      // several open pages share one answer
        private const float TrailsCacheSeconds = 5f;
        private const float SleepersMinRebuildSeconds = 10f;
        private const float BasesCacheSeconds = 30f;
        private const float BasesCacheWhileScanningSeconds = 5f;
        private const float BaseScanTickSeconds = 0.2f;
        private const float BaseScanMinCycleSeconds = 60f; // measure all buildings at most once a minute
        private const float BaseScanIdleStopSeconds = 300f; // stop measuring when no page asked for 5 minutes
        private const float RerenderCooldownSeconds = 300f; // rendering pauses the server for a few seconds
        private const string DeathsFile = "AdminLiveMap/deaths";
        private const string MapFile = "AdminLiveMap/map";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private Configuration _config;
        private readonly long _epoch = Now(); // trail times are stored as seconds since the plugin loaded
        private readonly Dictionary<ulong, List<TrailPoint>> _trails = new Dictionary<ulong, List<TrailPoint>>();
        private readonly List<ulong> _emptyTrails = new List<ulong>();
        private List<DeathRecord> _deaths = new List<DeathRecord>();
        private int _nextDeathId = 1;

        private readonly CachedReply _playersReply = new CachedReply();
        private readonly CachedReply _trailsReply = new CachedReply();
        private readonly CachedReply _sleepersReply = new CachedReply();
        private readonly CachedReply _basesReply = new CachedReply();
        private int _sleepersReplyVersion = -1;
        private string _mapReply;
        private float _lastRender = float.MinValue;

        // Sleepers rarely change, so the page only asks for them when this number changes.
        private int _sleepersVersion;
        private int _sleepersCount = -1;
        private ulong _sleepersHash;

        // Reused between answers to avoid creating garbage every second
        private readonly StringBuilder _sb = new StringBuilder(64 * 1024);
        private readonly List<PlayerSnap> _snaps = new List<PlayerSnap>();
        private readonly Dictionary<ulong, ulong> _teamLeaders = new Dictionary<ulong, ulong>();
        private readonly HashSet<ulong> _seen = new HashSet<ulong>();

        // Base scanner: building outlines, measured a few buildings at a time
        private readonly Dictionary<uint, BuildingInfo> _buildings = new Dictionary<uint, BuildingInfo>();
        private readonly Queue<uint> _scanQueue = new Queue<uint>();
        private readonly HashSet<uint> _scanIds = new HashSet<uint>();
        private readonly List<uint> _scanGone = new List<uint>();
        private Timer _scanTimer;
        private bool _scanRunning, _scanDoneOnce;
        private float _scanCycleStart = float.MinValue, _lastBasesRequest, _lastCycleSeconds = -1f;
        private double _scanMsTotal, _scanMsMax;
        private int _scanTicks;

        private float _sampleMs;

        private class CachedReply
        {
            public string Json;
            public float Time = float.MinValue;

            public bool Fresh(float maxAge) => Json != null && UnityEngine.Time.realtimeSinceStartup - Time < maxAge;

            public string Set(string json)
            {
                Json = json;
                Time = UnityEngine.Time.realtimeSinceStartup;
                return json;
            }
        }

        private struct TrailPoint
        {
            public int T;      // seconds since _epoch
            public float X, Z;
        }

        private struct PlayerSnap
        {
            public ulong Id, Team;
            public string Name, Mounted;
            public float X, Z, Heading, Health;
            public int Flags; // 1 = connected but not woken up yet, 2 = dead
        }

        private struct BaseSnap
        {
            public ulong TcId, Owner, Team;
            public float X, Z, MinX, MaxX, MinZ, MaxZ;
            public int Blocks; // -1 = not measured yet
            public bool Extra; // a second cupboard in the same building
            public HashSet<ulong> Auth;
        }

        private class BuildingInfo
        {
            public float MinX, MaxX, MinZ, MaxZ;
            public int Blocks;
        }

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Map image scale (0.5 = one pixel per 2 metres)")]
            public float MapScale = 0.5f;

            [JsonProperty("Map image ocean border (pixels)")]
            public int OceanMargin = 250;

            [JsonProperty("Trail point interval (seconds)")]
            public float TrailInterval = 5f;

            [JsonProperty("Trail minimum movement between points (metres)")]
            public float TrailMinMove = 3f;

            [JsonProperty("Keep trails for (minutes)")]
            public int TrailMinutes = 60;

            [JsonProperty("Max trail points sent at once (older points are thinned out above this)")]
            public int MaxTrailPoints = 20000;

            [JsonProperty("Keep deaths for (minutes)")]
            public int DeathMinutes = 120;

            [JsonProperty("Max deaths kept")]
            public int MaxDeaths = 500;

            [JsonProperty("Send sleepers to the page")]
            public bool SendSleepers = true;

            [JsonProperty("Send bases to the page")]
            public bool SendBases = true;

            [JsonProperty("Base scan time per step (milliseconds, 5 steps a second)")]
            public float ScanBudgetMs = 2f;

            public void Clamp()
            {
                MapScale = Mathf.Clamp(MapScale, 0.1f, 2f);
                OceanMargin = Mathf.Clamp(OceanMargin, 0, 1000);
                TrailInterval = Mathf.Clamp(TrailInterval, 1f, 60f);
                TrailMinMove = Mathf.Clamp(TrailMinMove, 0f, 100f);
                TrailMinutes = Mathf.Clamp(TrailMinutes, 0, 24 * 60);
                MaxTrailPoints = Mathf.Clamp(MaxTrailPoints, 1000, 500000);
                DeathMinutes = Mathf.Clamp(DeathMinutes, 0, 7 * 24 * 60);
                MaxDeaths = Mathf.Clamp(MaxDeaths, 0, 10000);
                ScanBudgetMs = Mathf.Clamp(ScanBudgetMs, 0.5f, 20f);
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new JsonException("empty");
                _config.Clamp();
                SaveConfig();
            }
            catch (Exception e)
            {
                PrintError($"The config file has an error ({e.Message}). Using default settings; fix or delete the file and reload.");
                _config = new Configuration();
            }
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Hooks

        private void OnServerInitialized()
        {
            try { _deaths = Interface.Oxide.DataFileSystem.ReadObject<List<DeathRecord>>(DeathsFile) ?? new List<DeathRecord>(); }
            catch { _deaths = new List<DeathRecord>(); }
            foreach (var d in _deaths) if (d.id >= _nextDeathId) _nextDeathId = d.id + 1;
            PruneDeaths();

            timer.Every(_config.TrailInterval, SampleTrails);
            SampleTrails();
        }

        private void OnServerSave() => SaveDeaths();

        private void Unload() => SaveDeaths();

        #endregion

        #region Helpers

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private static string ToJson(object o) => JsonConvert.SerializeObject(o, Formatting.None);

        // RCON and the server console have no connection; players' F1 console calls are ignored without a reply.
        private static bool FromConsoleOrRcon(ConsoleSystem.Arg arg) => arg.Connection == null;

        private string NameOf(ulong id)
        {
            var p = covalence.Players.FindPlayerById(id.ToString());
            return p != null && !string.IsNullOrEmpty(p.Name) ? p.Name : id.ToString();
        }

        private static ulong TeamOf(BasePlayer p, Dictionary<ulong, ulong> leaders)
        {
            ulong teamId = p.currentTeam;
            if (teamId == 0) return 0;
            if (leaders.ContainsKey(teamId)) return teamId;
            var team = RelationshipManager.ServerInstance != null ? RelationshipManager.ServerInstance.FindTeam(teamId) : null;
            if (team == null) return 0;
            leaders[teamId] = team.teamLeader;
            return teamId;
        }

        // Small JSON writer: much faster than building dictionaries for thousands of players.
        private static void Str(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x2028 || c == 0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // Steam IDs are too big for JavaScript numbers, so they are sent as text.
        private static void Id(StringBuilder sb, ulong id)
        {
            if (id == 0) { sb.Append("null"); return; }
            sb.Append('"').Append(id.ToString(Inv)).Append('"');
        }

        private static void Int(StringBuilder sb, long v) => sb.Append(v.ToString(Inv));

        private static void Int(StringBuilder sb, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
            sb.Append(((long)Math.Round(v)).ToString(Inv));
        }

        private static void Num(StringBuilder sb, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
            sb.Append(Math.Round(v, 1).ToString("0.#", Inv));
        }

        private static void Teams(StringBuilder sb, Dictionary<ulong, ulong> leaders)
        {
            sb.Append("\"teams\":{");
            bool first = true;
            foreach (var kv in leaders)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key.ToString(Inv)).Append("\":");
                Id(sb, kv.Value);
            }
            sb.Append('}');
        }

        #endregion

        #region Players and sleepers

        [ConsoleCommand("livemap.players")]
        private void CmdPlayers(ConsoleSystem.Arg arg)
        {
            if (!FromConsoleOrRcon(arg)) return;
            arg.ReplyWith(_playersReply.Fresh(PlayersCacheSeconds) ? _playersReply.Json : _playersReply.Set(BuildPlayersReply()));
        }

        private string BuildPlayersReply()
        {
            CollectOnline(_snaps, _teamLeaders, _seen);
            UpdateSleepersVersion();
            int sleeperCount = _config.SendSleepers ? Math.Max(0, CountSleepers()) : 0;
            return WritePlayers(_snaps, _teamLeaders, _sleepersVersion, sleeperCount, _nextDeathId - 1);
        }

        private void CollectOnline(List<PlayerSnap> into, Dictionary<ulong, ulong> leaders, HashSet<ulong> seen)
        {
            into.Clear();
            leaders.Clear();
            seen.Clear();
            foreach (var p in BasePlayer.activePlayerList)
            {
                // A player who just connected and hasn't woken up yet is also in the sleepers list: report them here.
                if (p == null || p.IsNpc || !p.userID.IsSteamId() || !seen.Add(p.userID)) continue;
                var pos = p.transform.position;

                string mounted = null;
                var mount = p.GetMounted();
                if (mount != null)
                {
                    var vehicle = p.GetMountedVehicle();
                    mounted = vehicle != null ? vehicle.ShortPrefabName : mount.ShortPrefabName;
                }

                bool dead = p.IsDead();
                into.Add(new PlayerSnap
                {
                    Id = p.userID,
                    Name = p.displayName,
                    X = pos.x,
                    Z = pos.z,
                    Heading = dead ? -1f : p.eyes.rotation.eulerAngles.y,
                    Health = p.health,
                    Flags = (p.IsSleeping() ? 1 : 0) | (dead ? 2 : 0),
                    Team = TeamOf(p, leaders),
                    Mounted = mounted
                });
            }
        }

        private int CountSleepers()
        {
            // Sleepers that are also online are reported as online players instead.
            int n = 0;
            foreach (var p in BasePlayer.sleepingPlayerList)
                if (p != null && !p.IsNpc && p.userID.IsSteamId() && !_seen.Contains(p.userID)) n++;
            return n;
        }

        private void UpdateSleepersVersion()
        {
            // Cheap fingerprint of who is asleep: changes when someone falls asleep, wakes up or dies.
            int count = 0;
            ulong hash = 0;
            foreach (var p in BasePlayer.sleepingPlayerList)
            {
                if (p == null) continue;
                count++;
                hash ^= p.userID * 0x9E3779B97F4A7C15UL;
            }
            if (count != _sleepersCount || hash != _sleepersHash)
            {
                _sleepersCount = count;
                _sleepersHash = hash;
                _sleepersVersion++;
            }
        }

        private string WritePlayers(List<PlayerSnap> online, Dictionary<ulong, ulong> leaders, int sleepersVersion, int sleeperCount, int lastDeath)
        {
            float hour = TOD_Sky.Instance != null ? TOD_Sky.Instance.Cycle.Hour : -1f;
            var sb = _sb.Clear();
            sb.Append("{\"type\":\"players\",\"version\":");
            Str(sb, Version.ToString());
            sb.Append(",\"now\":"); Int(sb, Now());
            sb.Append(",\"size\":"); Int(sb, (long)World.Size);
            sb.Append(",\"seed\":"); Int(sb, (long)World.Seed);
            sb.Append(",\"hour\":"); Num(sb, hour);
            sb.Append(",\"sleepersVersion\":"); Int(sb, sleepersVersion);
            sb.Append(",\"sleeperCount\":"); Int(sb, sleeperCount);
            sb.Append(",\"lastDeath\":"); Int(sb, lastDeath);
            sb.Append(',');
            Teams(sb, leaders);
            // Each player: [steamId, name, x, z, heading (-1 = none), health, flags, teamId, vehicle]
            sb.Append(",\"online\":[");
            for (int i = 0; i < online.Count; i++)
            {
                var s = online[i];
                if (i > 0) sb.Append(',');
                sb.Append('['); Id(sb, s.Id);
                sb.Append(','); Str(sb, s.Name);
                sb.Append(','); Num(sb, s.X);
                sb.Append(','); Num(sb, s.Z);
                sb.Append(','); Int(sb, s.Heading);
                sb.Append(','); Int(sb, s.Health);
                sb.Append(','); Int(sb, s.Flags);
                sb.Append(','); Id(sb, s.Team);
                sb.Append(','); Str(sb, s.Mounted);
                sb.Append(']');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        [ConsoleCommand("livemap.sleepers")]
        private void CmdSleepers(ConsoleSystem.Arg arg)
        {
            if (!FromConsoleOrRcon(arg)) return;
            if (!_config.SendSleepers)
            {
                arg.ReplyWith("{\"type\":\"sleepers\",\"disabled\":true,\"version\":0,\"teams\":{},\"sleepers\":[]}");
                return;
            }

            UpdateSleepersVersion();
            bool current = _sleepersReply.Json != null && _sleepersReplyVersion == _sleepersVersion;
            if (!current && !_sleepersReply.Fresh(SleepersMinRebuildSeconds))
            {
                var online = new HashSet<ulong>();
                foreach (var p in BasePlayer.activePlayerList) if (p != null) online.Add(p.userID);
                var leaders = new Dictionary<ulong, ulong>();
                var list = new List<PlayerSnap>(BasePlayer.sleepingPlayerList.Count);
                CollectSleepers(list, leaders, online);
                _sleepersReplyVersion = _sleepersVersion;
                _sleepersReply.Set(WriteSleepers(list, leaders, _sleepersVersion));
            }
            arg.ReplyWith(_sleepersReply.Json);
        }

        private static void CollectSleepers(List<PlayerSnap> into, Dictionary<ulong, ulong> leaders, HashSet<ulong> online)
        {
            foreach (var p in BasePlayer.sleepingPlayerList)
            {
                if (p == null || p.IsNpc || !p.userID.IsSteamId() || online.Contains(p.userID)) continue;
                var pos = p.transform.position;
                into.Add(new PlayerSnap
                {
                    Id = p.userID,
                    Name = p.displayName,
                    X = pos.x,
                    Z = pos.z,
                    Health = p.health,
                    Flags = p.IsDead() ? 2 : 0,
                    Team = TeamOf(p, leaders)
                });
            }
        }

        private string WriteSleepers(List<PlayerSnap> sleepers, Dictionary<ulong, ulong> leaders, int version)
        {
            var sb = _sb.Clear();
            sb.Append("{\"type\":\"sleepers\",\"version\":"); Int(sb, version);
            sb.Append(',');
            Teams(sb, leaders);
            // Each sleeper: [steamId, name, x, z, health, flags, teamId]
            sb.Append(",\"sleepers\":[");
            for (int i = 0; i < sleepers.Count; i++)
            {
                var s = sleepers[i];
                if (i > 0) sb.Append(',');
                sb.Append('['); Id(sb, s.Id);
                sb.Append(','); Str(sb, s.Name);
                sb.Append(','); Int(sb, s.X);
                sb.Append(','); Int(sb, s.Z);
                sb.Append(','); Int(sb, s.Health);
                sb.Append(','); Int(sb, s.Flags);
                sb.Append(','); Id(sb, s.Team);
                sb.Append(']');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        #endregion

        #region Trails

        private void SampleTrails()
        {
            var sw = Stopwatch.StartNew();
            int now = (int)(Now() - _epoch);
            int cutoff = now - _config.TrailMinutes * 60;
            float minMoveSq = _config.TrailMinMove * _config.TrailMinMove;

            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || p.IsNpc || p.IsDead() || !p.userID.IsSteamId()) continue;
                var pos = p.transform.position;
                List<TrailPoint> trail;
                if (!_trails.TryGetValue(p.userID, out trail)) _trails[p.userID] = trail = new List<TrailPoint>();
                if (trail.Count > 0)
                {
                    var last = trail[trail.Count - 1];
                    float dx = pos.x - last.X, dz = pos.z - last.Z;
                    if (dx * dx + dz * dz < minMoveSq) continue;
                }
                trail.Add(new TrailPoint { T = now, X = pos.x, Z = pos.z });
            }

            _emptyTrails.Clear();
            foreach (var kv in _trails)
            {
                var trail = kv.Value;
                int old = 0;
                while (old < trail.Count && trail[old].T < cutoff) old++;
                if (old > 0) trail.RemoveRange(0, old);
                if (trail.Count == 0) _emptyTrails.Add(kv.Key);
            }
            foreach (var id in _emptyTrails) _trails.Remove(id);
            _sampleMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        [ConsoleCommand("livemap.trails")]
        private void CmdTrails(ConsoleSystem.Arg arg)
        {
            if (!FromConsoleOrRcon(arg)) return;

            ulong one;
            if (ulong.TryParse(arg.GetString(0), out one))
            {
                // One player's full trail, for when the page selects them
                List<TrailPoint> trail;
                _trails.TryGetValue(one, out trail);
                var sb = _sb.Clear();
                sb.Append("{\"type\":\"trail\",\"id\":"); Id(sb, one);
                sb.Append(",\"trail\":");
                WriteTrail(sb, trail, 1);
                sb.Append('}');
                arg.ReplyWith(sb.ToString());
                return;
            }

            if (!arg.IsRcon)
            {
                // Typed in the server console: a readable summary instead of a long list of points
                int points = 0;
                foreach (var trail in _trails.Values) points += trail.Count;
                arg.ReplyWith($"Trails: {_trails.Count} player(s), {points} point(s) from the last {_config.TrailMinutes} minutes.");
                return;
            }

            if (_trailsReply.Fresh(TrailsCacheSeconds)) { arg.ReplyWith(_trailsReply.Json); return; }
            arg.ReplyWith(_trailsReply.Set(WriteTrails(_trails, _config.MaxTrailPoints)));
        }

        private string WriteTrails(Dictionary<ulong, List<TrailPoint>> trails, int maxPoints)
        {
            // Above the limit, keep every 2nd, 3rd... point (and always the newest) so the answer stays small.
            int total = 0;
            foreach (var t in trails.Values) total += t.Count;
            int step = Math.Max(1, (total + maxPoints - 1) / maxPoints);

            var sb = _sb.Clear();
            sb.Append("{\"type\":\"trails\",\"now\":"); Int(sb, Now());
            sb.Append(",\"minutes\":"); Int(sb, _config.TrailMinutes);
            sb.Append(",\"step\":"); Int(sb, step);
            // Each trail: [time, x, z, time, x, z, ...]
            sb.Append(",\"trails\":{");
            bool first = true;
            foreach (var kv in trails)
            {
                if (kv.Value.Count == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key.ToString(Inv)).Append("\":");
                WriteTrail(sb, kv.Value, step);
            }
            sb.Append("}}");
            return sb.ToString();
        }

        private void WriteTrail(StringBuilder sb, List<TrailPoint> trail, int step)
        {
            sb.Append('[');
            if (trail != null && trail.Count > 0)
            {
                int last = trail.Count - 1;
                bool first = true;
                for (int i = 0; i <= last; i = i == last ? last + 1 : Math.Min(i + step, last))
                {
                    var p = trail[i];
                    if (!first) sb.Append(',');
                    first = false;
                    Int(sb, _epoch + p.T);
                    sb.Append(','); Int(sb, p.X);
                    sb.Append(','); Int(sb, p.Z);
                }
            }
            sb.Append(']');
        }

        #endregion

        #region Deaths

        private class DeathRecord
        {
            public int id;
            public long time;       // unix seconds
            public string victim;
            public string victimId;
            public string killer;   // player name, short prefab name, or damage type
            public string killerId; // set only when killed by a real player
            public string weapon;
            public int distance;
            public float x, z;
            public string grid;
        }

        private void SaveDeaths()
        {
            PruneDeaths();
            Interface.Oxide.DataFileSystem.WriteObject(DeathsFile, _deaths);
        }

        private void PruneDeaths()
        {
            long cutoff = Now() - _config.DeathMinutes * 60L;
            int old = 0;
            while (old < _deaths.Count && (_deaths[old].time < cutoff || _deaths.Count - old > _config.MaxDeaths)) old++;
            if (old > 0) _deaths.RemoveRange(0, old);
        }

        [ConsoleCommand("livemap.deaths")]
        private void CmdDeaths(ConsoleSystem.Arg arg)
        {
            if (!FromConsoleOrRcon(arg)) return;
            int after = arg.GetInt(0, 0);
            PruneDeaths();
            var list = new List<DeathRecord>();
            foreach (var d in _deaths) if (d.id > after) list.Add(d);
            arg.ReplyWith(ToJson(new Dictionary<string, object>
            {
                ["type"] = "deaths",
                ["now"] = Now(),
                ["lastDeath"] = _nextDeathId - 1,
                ["keepMinutes"] = _config.DeathMinutes,
                ["deaths"] = list
            }));
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null || player.IsNpc || !player.userID.IsSteamId()) return;
            try
            {
                var pos = player.transform.position;
                var rec = new DeathRecord
                {
                    id = _nextDeathId++,
                    time = Now(),
                    victim = player.displayName,
                    victimId = player.UserIDString,
                    x = (float)Math.Round(pos.x, 1),
                    z = (float)Math.Round(pos.z, 1),
                    grid = MapHelper.PositionToString(pos)
                };

                BaseEntity initiator = info?.Initiator;
                var killerPlayer = info?.InitiatorPlayer;
                if (killerPlayer != null && killerPlayer == player)
                {
                    rec.killer = "themselves";
                }
                else if (killerPlayer != null && !killerPlayer.IsNpc && killerPlayer.userID.IsSteamId())
                {
                    rec.killer = killerPlayer.displayName;
                    rec.killerId = killerPlayer.UserIDString;
                }
                else if (initiator != null)
                {
                    rec.killer = Pretty(initiator.ShortPrefabName);
                }
                else
                {
                    rec.killer = player.lastDamage.ToString().ToLowerInvariant();
                }

                if (info != null)
                {
                    string weapon = null;
                    var item = info.Weapon != null ? info.Weapon.GetItem() : null;
                    if (item != null) weapon = item.info.displayName.english;
                    else if (info.WeaponPrefab != null) weapon = Pretty(info.WeaponPrefab.ShortPrefabName);
                    rec.weapon = weapon;

                    float dist = info.ProjectileDistance;
                    if (dist <= 0f && initiator != null && initiator != player)
                        dist = Vector3.Distance(initiator.transform.position, pos);
                    rec.distance = Mathf.RoundToInt(dist);
                }

                _deaths.Add(rec);
                PruneDeaths();
                _playersReply.Json = null; // the next update tells the page there is a new death
            }
            catch (Exception e)
            {
                PrintWarning("Could not record a death: " + e.Message);
            }
        }

        private static string Pretty(string shortPrefab)
        {
            if (string.IsNullOrEmpty(shortPrefab)) return "unknown";
            string s = shortPrefab.Replace(".entity", "").Replace(".deployed", "").Replace("_deployed", "");
            return s.Replace('_', ' ').Replace('.', ' ').Trim();
        }

        #endregion

        #region Bases

        [ConsoleCommand("livemap.bases")]
        private void CmdBases(ConsoleSystem.Arg arg)
        {
            if (!FromConsoleOrRcon(arg)) return;
            if (!_config.SendBases)
            {
                arg.ReplyWith("{\"type\":\"bases\",\"disabled\":true,\"complete\":true,\"bases\":[],\"names\":{}}");
                return;
            }

            _lastBasesRequest = Time.realtimeSinceStartup;
            if (_scanTimer == null || _scanTimer.Destroyed)
            {
                _scanTimer = timer.Every(BaseScanTickSeconds, ScanTick);
                if (!_scanRunning) StartScanCycle();
            }

            float maxAge = _scanDoneOnce ? BasesCacheSeconds : BasesCacheWhileScanningSeconds;
            if (_basesReply.Fresh(maxAge)) { arg.ReplyWith(_basesReply.Json); return; }

            var list = new List<BaseSnap>();
            int total, measured;
            CollectBases(list, out total, out measured);
            arg.ReplyWith(_basesReply.Set(WriteBases(list, _scanDoneOnce, measured, total, NameOf)));
        }

        // Building sizes are measured a few buildings at a time (a couple of milliseconds, 5 times a second),
        // so even a server with a million building blocks never pauses. The tool cupboards themselves are read
        // from the game's own building list, so there is no need to search the world or listen to spawn hooks.
        private void StartScanCycle()
        {
            _scanQueue.Clear();
            _scanIds.Clear();
            if (BuildingManager.server != null)
            {
                foreach (var b in BuildingManager.server.buildingDictionary.Values)
                {
                    if (b == null || b.buildingPrivileges == null || b.buildingPrivileges.Count == 0) continue;
                    if (_scanIds.Add(b.ID)) _scanQueue.Enqueue(b.ID);
                }
            }

            _scanGone.Clear();
            foreach (var id in _buildings.Keys) if (!_scanIds.Contains(id)) _scanGone.Add(id);
            foreach (var id in _scanGone) _buildings.Remove(id);

            _scanRunning = true;
            _scanCycleStart = Time.realtimeSinceStartup;
        }

        private void ScanTick()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastBasesRequest > BaseScanIdleStopSeconds)
            {
                // Nobody is watching bases: stop until the page asks again.
                _scanTimer?.Destroy();
                _scanTimer = null;
                return;
            }

            if (_scanQueue.Count == 0)
            {
                if (_scanRunning)
                {
                    _scanRunning = false;
                    _scanDoneOnce = true;
                    _lastCycleSeconds = now - _scanCycleStart;
                    _basesReply.Json = null; // send the finished outlines on the next request
                }
                if (now - _scanCycleStart >= BaseScanMinCycleSeconds) StartScanCycle();
                return;
            }

            if (BuildingManager.server == null) return;
            var sw = Stopwatch.StartNew();
            while (_scanQueue.Count > 0 && sw.Elapsed.TotalMilliseconds < _config.ScanBudgetMs)
            {
                uint id = _scanQueue.Dequeue();
                var building = BuildingManager.server.GetBuilding(id);
                if (building == null || building.buildingPrivileges == null || building.buildingPrivileges.Count == 0)
                {
                    _buildings.Remove(id);
                    continue;
                }
                _buildings[id] = Measure(building);
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            _scanMsTotal += ms;
            if (ms > _scanMsMax) _scanMsMax = ms;
            _scanTicks++;
        }

        private static BuildingInfo Measure(BuildingManager.Building building)
        {
            var info = new BuildingInfo { MinX = float.MaxValue, MaxX = float.MinValue, MinZ = float.MaxValue, MaxZ = float.MinValue };
            foreach (var b in building.buildingBlocks)
            {
                if (b == null) continue;
                var bp = b.transform.position;
                if (bp.x < info.MinX) info.MinX = bp.x;
                if (bp.x > info.MaxX) info.MaxX = bp.x;
                if (bp.z < info.MinZ) info.MinZ = bp.z;
                if (bp.z > info.MaxZ) info.MaxZ = bp.z;
                info.Blocks++;
            }
            return info;
        }

        private void CollectBases(List<BaseSnap> into, out int total, out int measured)
        {
            total = measured = 0;
            if (BuildingManager.server == null) return;
            var leaders = new Dictionary<ulong, ulong>();
            foreach (var building in BuildingManager.server.buildingDictionary.Values)
            {
                if (building == null || building.buildingPrivileges == null || building.buildingPrivileges.Count == 0) continue;
                total++;
                BuildingInfo info;
                if (_buildings.TryGetValue(building.ID, out info)) measured++;

                bool extra = false;
                foreach (var tc in building.buildingPrivileges)
                {
                    if (tc == null || tc.IsDestroyed) continue;
                    var pos = tc.transform.position;
                    var snap = new BaseSnap
                    {
                        TcId = tc.net != null ? tc.net.ID.Value : 0,
                        X = pos.x,
                        Z = pos.z,
                        Owner = tc.OwnerID,
                        Extra = extra,
                        Auth = tc.authorizedPlayers,
                        Blocks = -1,
                        MinX = pos.x, MaxX = pos.x, MinZ = pos.z, MaxZ = pos.z
                    };
                    if (info != null && info.Blocks > 0)
                    {
                        snap.Blocks = info.Blocks;
                        snap.MinX = Math.Min(info.MinX, pos.x);
                        snap.MaxX = Math.Max(info.MaxX, pos.x);
                        snap.MinZ = Math.Min(info.MinZ, pos.z);
                        snap.MaxZ = Math.Max(info.MaxZ, pos.z);
                    }
                    else if (info != null) snap.Blocks = 0;

                    if (tc.OwnerID != 0 && RelationshipManager.ServerInstance != null)
                    {
                        var t = RelationshipManager.ServerInstance.FindPlayersTeam(tc.OwnerID);
                        if (t != null) snap.Team = t.teamID;
                    }
                    into.Add(snap);
                    extra = true;
                }
            }
        }

        private string WriteBases(List<BaseSnap> bases, bool complete, int measured, int total, Func<ulong, string> nameOf)
        {
            var names = new Dictionary<ulong, string>();
            var sb = _sb.Clear();
            sb.Append("{\"type\":\"bases\",\"complete\":").Append(complete ? "true" : "false");
            sb.Append(",\"measured\":"); Int(sb, measured);
            sb.Append(",\"total\":"); Int(sb, total);
            // Each base: [cupboardId, x, z, ownerId, teamId, blocks (-1 = not measured yet), extraCupboard (0/1),
            //             minX, maxX, minZ, maxZ, [authorised steamIds]]   (outline padded by 3 m)
            sb.Append(",\"bases\":[");
            for (int i = 0; i < bases.Count; i++)
            {
                var b = bases[i];
                if (i > 0) sb.Append(',');
                sb.Append('['); sb.Append('"').Append(b.TcId.ToString(Inv)).Append('"');
                sb.Append(','); Int(sb, b.X);
                sb.Append(','); Int(sb, b.Z);
                sb.Append(','); Id(sb, b.Owner);
                sb.Append(','); Id(sb, b.Team);
                sb.Append(','); Int(sb, b.Blocks);
                sb.Append(',').Append(b.Extra ? '1' : '0');
                sb.Append(','); Int(sb, b.MinX - 3f);
                sb.Append(','); Int(sb, b.MaxX + 3f);
                sb.Append(','); Int(sb, b.MinZ - 3f);
                sb.Append(','); Int(sb, b.MaxZ + 3f);
                sb.Append(",[");
                bool first = true;
                if (b.Auth != null)
                {
                    foreach (var id in b.Auth)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Id(sb, id);
                        if (!names.ContainsKey(id)) names[id] = nameOf(id);
                    }
                }
                sb.Append("]]");
                if (b.Owner != 0 && !names.ContainsKey(b.Owner)) names[b.Owner] = nameOf(b.Owner);
            }
            sb.Append("],\"names\":{");
            bool firstName = true;
            foreach (var kv in names)
            {
                if (!firstName) sb.Append(',');
                firstName = false;
                sb.Append('"').Append(kv.Key.ToString(Inv)).Append("\":");
                Str(sb, kv.Value);
            }
            sb.Append("}}");
            return sb.ToString();
        }

        #endregion

        #region Load test

        [ConsoleCommand("livemap.benchmark")]
        private void CmdBenchmark(ConsoleSystem.Arg arg)
        {
            if (!FromConsoleOrRcon(arg)) return;
            int online = Mathf.Clamp(arg.GetInt(0, 600), 0, 5000);
            int sleepers = Mathf.Clamp(arg.GetInt(1, 10000), 0, 100000);
            int bases = Mathf.Clamp(arg.GetInt(2, 10000), 0, 100000);

            var rnd = new System.Random(12345);
            float half = World.Size / 2f;
            Func<float> pos = () => (float)(rnd.NextDouble() * 2 - 1) * half;
            ulong baseId = 76561198000000000UL;

            // Made-up players, teams of up to 4
            var leaders = new Dictionary<ulong, ulong>();
            var onlineSnaps = new List<PlayerSnap>(online);
            for (int i = 0; i < online; i++)
            {
                ulong team = rnd.Next(3) == 0 ? 0 : 1000UL + (ulong)(i / 4);
                if (team != 0 && !leaders.ContainsKey(team)) leaders[team] = baseId + (ulong)i;
                onlineSnaps.Add(new PlayerSnap
                {
                    Id = baseId + (ulong)i, Name = "Test player \"" + i + "\"", X = pos(), Z = pos(),
                    Heading = rnd.Next(360), Health = rnd.Next(1, 100), Team = team,
                    Mounted = rnd.Next(10) == 0 ? "minicopter.entity" : null
                });
            }
            var sleeperSnaps = new List<PlayerSnap>(sleepers);
            for (int i = 0; i < sleepers; i++)
                sleeperSnaps.Add(new PlayerSnap { Id = baseId + 100000UL + (ulong)i, Name = "Sleeper " + i, X = pos(), Z = pos(), Health = 100 });

            // Everyone online walked non-stop for the whole trail time
            int perTrail = Math.Max(1, (int)(_config.TrailMinutes * 60 / _config.TrailInterval));
            var trails = new Dictionary<ulong, List<TrailPoint>>(online);
            for (int i = 0; i < online; i++)
            {
                var t = new List<TrailPoint>(perTrail);
                float x = pos(), z = pos();
                for (int k = 0; k < perTrail; k++)
                {
                    x += (float)(rnd.NextDouble() * 40 - 20);
                    z += (float)(rnd.NextDouble() * 40 - 20);
                    t.Add(new TrailPoint { T = k * (int)_config.TrailInterval, X = x, Z = z });
                }
                trails[baseId + (ulong)i] = t;
            }

            var baseSnaps = new List<BaseSnap>(bases);
            for (int i = 0; i < bases; i++)
            {
                float x = pos(), z = pos();
                var auth = new HashSet<ulong> { baseId + (ulong)rnd.Next(Math.Max(1, online + sleepers)), baseId + (ulong)rnd.Next(Math.Max(1, online + sleepers)) };
                baseSnaps.Add(new BaseSnap
                {
                    TcId = 900000UL + (ulong)i, X = x, Z = z, Owner = baseId + (ulong)i, Team = i % 3 == 0 ? 1000UL + (ulong)i : 0,
                    Blocks = rnd.Next(10, 400), MinX = x - 15, MaxX = x + 15, MinZ = z - 15, MaxZ = z + 15, Auth = auth
                });
            }
            Func<ulong, string> fakeName = id => "Player " + (id - baseId);

            int trailPoints = online * perTrail;
            var report = new StringBuilder();
            report.AppendLine($"Admin Live Map load test with made-up data: {online} online, {sleepers} sleepers, {bases} bases, " +
                              $"{trailPoints} trail points ({online} players walking for {_config.TrailMinutes} minutes).");
            report.AppendLine("Time = server time to build one answer. Answers are shared by all open pages for the time shown.");
            Bench(report, "Online players (asked every 2 s, shared for 1 s)", () => WritePlayers(onlineSnaps, leaders, 1, sleepers, 0));
            Bench(report, "Sleepers (asked only when someone falls asleep or wakes up, at most every 60 s)", () => WriteSleepers(sleeperSnaps, leaders, 1));
            Bench(report, $"All trails (once when the page connects, thinned to {_config.MaxTrailPoints} points)", () => WriteTrails(trails, _config.MaxTrailPoints));
            Bench(report, "One player's full trail (when you click a player)", () =>
            {
                var sb = _sb.Clear();
                WriteTrail(sb, online > 0 ? trails[baseId] : null, 1);
                return sb.ToString();
            });
            Bench(report, "Bases (asked every 60 s, shared for 30 s)", () => WriteBases(baseSnaps, true, bases, bases, fakeName));
            report.AppendLine($"Trail memory for this many points: about {trailPoints * 12 / 1024 / 1024f:0.0} MB.");

            // The real server, right now
            report.AppendLine();
            var swReal = Stopwatch.StartNew();
            string players = BuildPlayersReply();
            double playersMs = swReal.Elapsed.TotalMilliseconds;
            int realPoints = 0;
            foreach (var t in _trails.Values) realPoints += t.Count;
            report.AppendLine($"This server now: {_snaps.Count} online, {Math.Max(0, CountSleepers())} sleepers, {realPoints} trail points. " +
                              $"Online players answer: {playersMs:0.00} ms, {players.Length / 1024f:0.0} KB. Trail sampling: {_sampleMs:0.00} ms every {_config.TrailInterval:0.#} s.");
            if (_scanTicks > 0)
                report.AppendLine($"Base measuring: {_buildings.Count} buildings with a cupboard, " +
                                  (_lastCycleSeconds >= 0 ? $"a full pass takes {_lastCycleSeconds:0.0} s, " : "first pass still running, ") +
                                  $"{_scanMsTotal / _scanTicks:0.00} ms per step on average (max {_scanMsMax:0.00} ms, limit {_config.ScanBudgetMs:0.#} ms, 5 steps a second).");
            else
                report.AppendLine("Base measuring has not run yet. It starts when the page asks for bases.");

            arg.ReplyWith(report.ToString().TrimEnd());
        }

        private static void Bench(StringBuilder report, string label, Func<string> build)
        {
            string json = build(); // warm up
            const int runs = 3;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < runs; i++) json = build();
            double ms = sw.Elapsed.TotalMilliseconds / runs;
            report.AppendLine($"- {label}: {ms:0.00} ms, {json.Length / 1024f:0} KB");
        }

        #endregion

        #region Map image

        private class MapCache
        {
            public string Key;   // map size, seed and image settings
            public string Image; // data URL
        }

        // For people: short text answers only, never the image data (about 1 MB of text).
        [ConsoleCommand("livemap.map")]
        private void CmdMap(ConsoleSystem.Arg arg)
        {
            if (!FromConsoleOrRcon(arg)) return;

            if (arg.GetString(0) != "rerender")
            {
                arg.ReplyWith(_mapReply != null
                    ? "The map image is ready. Type \"livemap.map rerender\" to draw it again."
                    : "The map image is not loaded yet. The Live Map page loads it when it connects. Type \"livemap.map rerender\" to draw it now.");
                return;
            }

            float wait = _lastRender + RerenderCooldownSeconds - Time.realtimeSinceStartup;
            if (wait > 0f)
            {
                arg.ReplyWith($"The map was drawn recently. You can draw it again in {Mathf.CeilToInt(wait)} seconds.");
                return;
            }

            _mapReply = BuildMapReply(true);
            arg.ReplyWith(_mapReply.StartsWith("{\"type\":\"error\"")
                ? "The server could not draw the map image."
                : "Done. Reload the Live Map page to see the new map image.");
        }

        // For the web page: the map image and monument names as JSON.
        [ConsoleCommand("livemap.mapdata")]
        private void CmdMapData(ConsoleSystem.Arg arg)
        {
            if (!FromConsoleOrRcon(arg)) return;
            if (_mapReply == null) _mapReply = BuildMapReply(false);
            arg.ReplyWith(_mapReply);
        }

        private string BuildMapReply(bool forceRender)
        {
            string key = $"{(int)World.Size}_{World.Seed}_{_config.MapScale}_{_config.OceanMargin}";

            MapCache cache = null;
            if (!forceRender)
            {
                try { cache = Interface.Oxide.DataFileSystem.ReadObject<MapCache>(MapFile); }
                catch { cache = null; }
            }

            if (cache == null || cache.Key != key || string.IsNullOrEmpty(cache.Image))
            {
                Puts("Rendering the map image. This happens once per map; the server may pause for a few seconds.");
                int width, height;
                Color background;
                byte[] bytes = MapImageRenderer.Render(out width, out height, out background, _config.MapScale, true, false, _config.OceanMargin);
                _lastRender = Time.realtimeSinceStartup;
                if (bytes == null) return "{\"type\":\"error\",\"message\":\"The server could not render the map image.\"}";

                bool isPng = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50;
                cache = new MapCache
                {
                    Key = key,
                    Image = (isPng ? "data:image/png;base64," : "data:image/jpeg;base64,") + Convert.ToBase64String(bytes)
                };
                Interface.Oxide.DataFileSystem.WriteObject(MapFile, cache); // replaces the image of any older map
                Puts($"Map image ready: {width}x{height} pixels, {bytes.Length / 1024} KB");
            }

            var monuments = new List<object>();
            if (TerrainMeta.Path != null && TerrainMeta.Path.Monuments != null)
            {
                foreach (var m in TerrainMeta.Path.Monuments)
                {
                    if (m == null || !m.shouldDisplayOnMap || m.displayPhrase == null) continue;
                    string label = m.displayPhrase.english;
                    if (string.IsNullOrEmpty(label)) continue;
                    var pos = m.transform.position;
                    monuments.Add(new Dictionary<string, object>
                    {
                        ["name"] = label.Trim(),
                        ["x"] = Math.Round(pos.x),
                        ["z"] = Math.Round(pos.z)
                    });
                }
            }

            return ToJson(new Dictionary<string, object>
            {
                ["type"] = "map",
                ["size"] = (int)World.Size,
                ["seed"] = (int)World.Seed,
                ["margin"] = _config.OceanMargin,
                ["cell"] = GridCell,
                ["monuments"] = monuments,
                ["image"] = cache.Image
            });
        }

        #endregion
    }
}
