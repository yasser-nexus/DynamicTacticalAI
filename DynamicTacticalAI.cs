using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

namespace DynamicTacticalAIOverhaul
{
    public sealed class DynamicTacticalAI : Script
    {
        private const string IniPath = "scripts/DynamicTacticalAI.ini";
        private const string LogPath = "scripts/DynamicTacticalAI.log";
        private readonly Dictionary<int, long> policeCooldown = new Dictionary<int, long>();
        private readonly Dictionary<int, long> civilianCooldown = new Dictionary<int, long>();
        private readonly List<Barrier> barriers = new List<Barrier>();
        private readonly Random random = new Random();
        private Config config;
        private bool enabled = true, reloadRequested, toggleRequested;
        private long nextPolice, nextCivilians, nextRoadblock, nextMaintenance, lastLog;
        private long shootingUntil, nextReport, reportDue;
        private int reporterHandle, trackedVehicleHandle;
        private Vector3 previousVehiclePosition;
        private long previousVehicleTime;
        private PendingBlock pending;

        private sealed class Barrier
        {
            public Vehicle Vehicle;
            public Prop Spike;
            public Vector3 Position;
            public long Created;
            public bool TiresHit;
        }
        private sealed class PendingBlock
        {
            public Vector3 Position;
            public float Heading;
            public int VehicleHash, SpikeHash;
            public long Deadline;
        }
        private sealed class Config
        {
            public bool Pursuit = true, Bystanders = true, Roadblocks = true, Reports = true;
            public Keys Toggle = Keys.F10, Reload = Keys.F11;
            public int PoliceInterval = 2500, CivilianInterval = 1800, RoadblockInterval = 60000;
            public int MaxRoadblocks = 2, LifetimeSeconds = 120;
            public float PoliceRadius = 120f, CivilianRadius = 60f, RoadblockDistance = 100f, SpeedThreshold = 25f;
        }

        public DynamicTacticalAI()
        {
            Interval = 0;
            config = LoadConfig();
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Aborted += OnAborted;
        }
        private static long Now { get { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; } }
        private static bool Exists(Entity entity) { return entity != null && entity.Exists(); }
        private static bool Valid(Ped ped) { return Exists(ped) && ped.IsAlive; }
        private static float Distance(Vector3 a, Vector3 b) { return (a - b).Length(); }
        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.KeyCode == config.Toggle) toggleRequested = true;
                if (e.KeyCode == config.Reload) reloadRequested = true;
            }
            catch (Exception ex) { Log(ex); }
        }
        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                long now = Now;
                if (reloadRequested) { reloadRequested = false; config = LoadConfig(); GTA.UI.Notification.Show("Dynamic Tactical AI: settings reloaded."); }
                if (toggleRequested) { toggleRequested = false; enabled = !enabled; if (!enabled) CleanupAll(); GTA.UI.Notification.Show("Dynamic Tactical AI: " + (enabled ? "enabled" : "disabled") + "."); }
                if (!enabled) return;
                if (Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE) || Function.Call<bool>(Hash.IS_CUTSCENE_ACTIVE))
                {
                    previousVehicleTime = 0;
                    return;
                }
                Ped player = Game.Player.Character;
                if (!Valid(player)) { CancelPending(); return; }
                ProcessReport(player, now);
                int wanted = Game.Player.WantedLevel;
                CheckSpikes(player, now);
                if (Function.Call<bool>(Hash.IS_PED_SHOOTING, player.Handle)) shootingUntil = now + 5000;
                if (now >= nextMaintenance)
                {
                    nextMaintenance = now + 1000;
                    Maintain(player, wanted, now);
                }
                if (config.Pursuit && wanted > 0 && now >= nextPolice)
                {
                    nextPolice = now + config.PoliceInterval;
                    UpdatePolice(player, wanted, now);
                }
                if (config.Bystanders && now >= nextCivilians)
                {
                    nextCivilians = now + config.CivilianInterval;
                    UpdateCivilians(player, now);
                }
                if (config.Pursuit && config.Roadblocks && wanted >= 4)
                {
                    if (pending != null) FinishPending(now);
                    else if (now >= nextRoadblock && barriers.Count < config.MaxRoadblocks)
                    {
                        nextRoadblock = now + config.RoadblockInterval;
                        BeginRoadblock(player, now);
                    }
                }
                else CancelPending();
            }
            catch (Exception ex) { Log(ex); }
        }
        private void UpdatePolice(Ped player, int wanted, long now)
        {
            Ped[] peds = World.GetNearbyPeds(player, config.PoliceRadius);
            if (peds == null) return;
            int processed = 0;
            foreach (Ped cop in peds)
            {
                if (processed >= 12) break;
                try
                {
                    if (!Valid(cop) || cop.Handle == player.Handle || !IsCop(cop) || IsMission(cop)) continue;
                    Vehicle vehicle = cop.CurrentVehicle;
                    if (!Exists(vehicle) || IsMission(vehicle) || Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, vehicle.Handle, -1, false) != cop.Handle) continue;
                    long last;
                    if (policeCooldown.TryGetValue(cop.Handle, out last) && now - last < 12000) continue;
                    policeCooldown[cop.Handle] = now; processed++;
                    Function.Call(Hash.SET_DRIVER_ABILITY, cop.Handle, 1.0f);
                    Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, cop.Handle, wanted >= 4 ? 0.9f : 0.55f);
                    // Alternate between direct pursuit and intercepts to avoid sending every car to one point.
                    if (wanted >= 2 && processed % 3 == 0 && player.IsInVehicle())
                    {
                        Vector3 forward = player.ForwardVector;
                        Vector3 side = new Vector3(-forward.Y, forward.X, 0f);
                        Vector3 target = player.Position + forward * 45f + side * (processed % 2 == 0 ? 20f : -20f);
                        target = World.GetNextPositionOnStreet(target);
                        if (Distance(target, player.Position) < 150f && target.Length() > 1f)
                            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, cop.Handle, vehicle.Handle, target.X, target.Y, target.Z, 32f, 786603, 10f);
                        else Function.Call(Hash.TASK_VEHICLE_CHASE, cop.Handle, player.Handle);
                    }
                    else Function.Call(Hash.TASK_VEHICLE_CHASE, cop.Handle, player.Handle);
                }
                catch (Exception ex) { Log(ex); }
            }
        }
        private void UpdateCivilians(Ped player, long now)
        {
            bool shooting = now < shootingUntil;
            Vehicle pv = player.CurrentVehicle;
            bool speeding = Exists(pv) && Function.Call<float>(Hash.GET_ENTITY_SPEED, pv.Handle) >= config.SpeedThreshold;
            if (!shooting && !speeding) return;
            Ped[] peds = World.GetNearbyPeds(player, config.CivilianRadius);
            if (peds == null) return;
            int processed = 0;
            foreach (Ped ped in peds)
            {
                if (processed >= 8) break;
                try
                {
                    if (!Valid(ped) || ped.Handle == player.Handle || IsCop(ped) || IsMission(ped) || ped.IsInVehicle() || !Function.Call<bool>(Hash.IS_PED_HUMAN, ped.Handle) || Function.Call<bool>(Hash.IS_PED_IN_GROUP, ped.Handle)) continue;
                    if (Function.Call<bool>(Hash.IS_PED_A_PLAYER, ped.Handle)) continue;
                    float distance = Distance(ped.Position, player.Position);
                    if (!shooting && distance > 25f) continue;
                    long last;
                    if (civilianCooldown.TryGetValue(ped.Handle, out last) && now - last < 30000) continue;
                    civilianCooldown[ped.Handle] = now; processed++;
                    if (shooting && config.Reports && reporterHandle == 0 && now >= nextReport && Game.Player.WantedLevel == 0 && distance < 45f && Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY, ped.Handle, player.Handle, 17) && random.Next(4) == 0)
                    {
                        Function.Call(Hash.TASK_USE_MOBILE_PHONE_TIMED, ped.Handle, 4000);
                        reporterHandle = ped.Handle;
                        reportDue = now + 4000;
                        nextReport = now + 60000;
                    }
                    else if (shooting && random.Next(2) == 0)
                        Function.Call(Hash.TASK_SEEK_COVER_FROM_PED, ped.Handle, player.Handle, 10000, false);
                    else Function.Call(Hash.TASK_SMART_FLEE_PED, ped.Handle, player.Handle, 100f, 12000, false, false);
                }
                catch (Exception ex) { Log(ex); }
            }
        }
        private void ProcessReport(Ped player, long now)
        {
            if (reporterHandle == 0) return;
            if (!config.Bystanders || !config.Reports) { reporterHandle = 0; return; }
            if (now < reportDue) return;
            int handle = reporterHandle;
            reporterHandle = 0;
            Ped witness = Entity.FromHandle(handle) as Ped;
            if (!Valid(witness) || witness.IsInVehicle() || IsMission(witness) || Distance(witness.Position, player.Position) > 100f || Game.Player.WantedLevel != 0) return;
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player.Handle, 1, false);
            Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player.Handle, false);
        }
        private static bool IsCop(Ped ped) { return Function.Call<int>(Hash.GET_PED_TYPE, ped.Handle) == 6; }
        private static bool IsMission(Entity entity) { return Function.Call<bool>(Hash.IS_ENTITY_A_MISSION_ENTITY, entity.Handle); }
        private void BeginRoadblock(Ped player, long now)
        {
            if (!player.IsInVehicle()) return;
            Vector3 desired = player.Position + player.ForwardVector * config.RoadblockDistance;
            var node = new OutputArgument();
            var nodeHeading = new OutputArgument();
            if (!Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING, desired.X, desired.Y, desired.Z, node, nodeHeading, 1, 3f, 0)) return;
            Vector3 position = node.GetResult<Vector3>();
            float heading = nodeHeading.GetResult<float>();
            if (position.Length() < 1f || Distance(position, player.Position) < 55f || Distance(position, player.Position) > config.RoadblockDistance + 100f) return;
            foreach (Barrier b in barriers) if (Distance(position, b.Position) < 60f) return;
            if (Function.Call<bool>(Hash.IS_POSITION_OCCUPIED, position.X, position.Y, position.Z, 8f, false, true, true, false, false, 0, false)) return;
            int vehicle = Game.GenerateHash("riot");
            int spike = Game.GenerateHash("p_ld_stinger_s");
            if (!Function.Call<bool>(Hash.IS_MODEL_IN_CDIMAGE, vehicle) || !Function.Call<bool>(Hash.IS_MODEL_A_VEHICLE, vehicle) || !Function.Call<bool>(Hash.IS_MODEL_IN_CDIMAGE, spike)) return;
            Function.Call(Hash.REQUEST_MODEL, vehicle);
            Function.Call(Hash.REQUEST_MODEL, spike);
            pending = new PendingBlock { Position = position, Heading = heading, VehicleHash = vehicle, SpikeHash = spike, Deadline = now + 8000 };
        }
        private void FinishPending(long now)
        {
            PendingBlock p = pending;
            if (now >= p.Deadline) { CancelPending(); return; }
            if (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, p.VehicleHash) || !Function.Call<bool>(Hash.HAS_MODEL_LOADED, p.SpikeHash)) return;
            Ped player = Game.Player.Character;
            if (!Valid(player) || Distance(player.Position, p.Position) < 45f || Distance(player.Position, p.Position) > 300f ||
                Function.Call<bool>(Hash.IS_POSITION_OCCUPIED, p.Position.X, p.Position.Y, p.Position.Z, 8f, false, true, true, false, false, 0, false))
            { CancelPending(); return; }
            Vehicle vehicle = null; Prop spike = null;
            try
            {
                int vh = Function.Call<int>(Hash.CREATE_VEHICLE, p.VehicleHash, p.Position.X, p.Position.Y, p.Position.Z, p.Heading + 90f, false, true, false);
                if (vh != 0) vehicle = Entity.FromHandle(vh) as Vehicle;
                if (!Exists(vehicle)) return;
                Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, vehicle.Handle, 5f);
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, vehicle.Handle, false, true, true);
                Vector3 forward = new Vector3(-(float)Math.Sin(p.Heading * Math.PI / 180), (float)Math.Cos(p.Heading * Math.PI / 180), 0f);
                Vector3 pos = p.Position - forward * 5f;
                int sh = Function.Call<int>(Hash.CREATE_OBJECT, p.SpikeHash, pos.X, pos.Y, pos.Z, false, true, false);
                if (sh != 0) spike = Entity.FromHandle(sh) as Prop;
                if (!Exists(spike)) return;
                Function.Call(Hash.SET_ENTITY_HEADING, spike.Handle, p.Heading + 90f);
                Function.Call(Hash.PLACE_OBJECT_ON_GROUND_PROPERLY, spike.Handle);
                Function.Call(Hash.FREEZE_ENTITY_POSITION, spike.Handle, true);
                barriers.Add(new Barrier { Vehicle = vehicle, Spike = spike, Position = spike.Position, Created = now });
                vehicle = null; spike = null;
            }
            finally
            {
                SafeDelete(spike); SafeDelete(vehicle); CancelPending();
            }
        }
        private void Maintain(Ped player, int wanted, long now)
        {
            for (int i = barriers.Count - 1; i >= 0; i--)
            {
                Barrier b = barriers[i];
                try
                {
                    if (wanted < 4 || !config.Pursuit || !config.Roadblocks || now - b.Created > config.LifetimeSeconds * 1000L || Distance(player.Position, b.Position) > 350f || !Exists(b.Spike))
                    { RemoveBarrier(i); continue; }
                }
                catch (Exception ex) { Log(ex); }
            }
            Prune(policeCooldown, now, 60000); Prune(civilianCooldown, now, 60000);
        }
        private void CheckSpikes(Ped player, long now)
        {
            Vehicle vehicle = player.CurrentVehicle;
            if (!Exists(vehicle) || barriers.Count == 0)
            {
                trackedVehicleHandle = 0; previousVehicleTime = 0; return;
            }
            Vector3 current = vehicle.Position;
            Vector3 previous = current;
            if (trackedVehicleHandle == vehicle.Handle && previousVehicleTime != 0 && now - previousVehicleTime <= 500 && Distance(previousVehiclePosition, current) < 35f)
                previous = previousVehiclePosition;
            trackedVehicleHandle = vehicle.Handle;
            previousVehiclePosition = current; previousVehicleTime = now;
            if (Function.Call<float>(Hash.GET_ENTITY_SPEED, vehicle.Handle) <= 3f || !Function.Call<bool>(Hash.GET_VEHICLE_TYRES_CAN_BURST, vehicle.Handle)) return;
            foreach (Barrier b in barriers)
            {
                if (b.TiresHit || !Exists(b.Spike)) continue;
                // Test the path between frames in XY, not just the current point, to reduce tunneling at speed.
                float dx = current.X - previous.X, dy = current.Y - previous.Y;
                float lengthSquared = dx * dx + dy * dy;
                float t = lengthSquared < 0.0001f ? 0f : ((b.Position.X - previous.X) * dx + (b.Position.Y - previous.Y) * dy) / lengthSquared;
                t = Math.Max(0f, Math.Min(1f, t));
                float x = previous.X + t * dx - b.Position.X, y = previous.Y + t * dy - b.Position.Y;
                float z = previous.Z + t * (current.Z - previous.Z);
                if (x * x + y * y > 2.75f * 2.75f || Math.Abs(z - b.Position.Z) > 2f) continue;
                Function.Call(Hash.SET_VEHICLE_TYRE_BURST, vehicle.Handle, 0, true, 1000f);
                Function.Call(Hash.SET_VEHICLE_TYRE_BURST, vehicle.Handle, 1, true, 1000f);
                b.TiresHit = true;
            }
        }
        private static void Prune(Dictionary<int, long> map, long now, long age)
        {
            var remove = new List<int>();
            foreach (var entry in map) if (now - entry.Value > age) remove.Add(entry.Key);
            foreach (int key in remove) map.Remove(key);
        }
        private void CancelPending()
        {
            if (pending == null) return;
            try { Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, pending.VehicleHash); Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, pending.SpikeHash); }
            catch (Exception ex) { Log(ex); }
            pending = null;
        }
        private void RemoveBarrier(int i)
        {
            Barrier b = barriers[i]; barriers.RemoveAt(i);
            SafeDelete(b.Spike);
            try
            {
                if (Exists(b.Vehicle) && Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, b.Vehicle.Handle, -1, false) && Function.Call<int>(Hash.GET_VEHICLE_NUMBER_OF_PASSENGERS, b.Vehicle.Handle, false, true) == 0) b.Vehicle.Delete();
                else if (Exists(b.Vehicle)) b.Vehicle.MarkAsNoLongerNeeded();
            }
            catch (Exception ex) { Log(ex); }
        }
        private void SafeDelete(Entity entity) { try { if (Exists(entity)) entity.Delete(); } catch (Exception ex) { Log(ex); } }
        private void CleanupAll() { reporterHandle = 0; trackedVehicleHandle = 0; previousVehicleTime = 0; CancelPending(); for (int i = barriers.Count - 1; i >= 0; i--) RemoveBarrier(i); policeCooldown.Clear(); civilianCooldown.Clear(); }
        private void OnAborted(object sender, EventArgs e) { try { CleanupAll(); } catch (Exception ex) { Log(ex); } }
        private void Log(Exception ex)
        {
            try { long now = Now; if (now - lastLog < 5000) return; lastLog = now; Directory.CreateDirectory("scripts"); File.AppendAllText(LogPath, DateTime.Now.ToString("O") + " " + ex + Environment.NewLine); } catch { }
        }
        private Config LoadConfig()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(IniPath)) return c;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string section = "";
                foreach (string raw in File.ReadAllLines(IniPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                    int split = line.IndexOf('='); if (split <= 0) continue;
                    values[section + "." + line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                }
                c.Pursuit = Bool(values, "Features.Pursuit", c.Pursuit); c.Bystanders = Bool(values, "Features.Bystanders", c.Bystanders);
                c.Roadblocks = Bool(values, "Features.Roadblocks", c.Roadblocks); c.Reports = Bool(values, "Features.CivilianReports", c.Reports);
                c.Toggle = Key(values, "Keys.Toggle", c.Toggle); c.Reload = Key(values, "Keys.Reload", c.Reload);
                c.PoliceInterval = Int(values, "Tuning.PoliceIntervalMs", c.PoliceInterval, 1000, 30000);
                c.CivilianInterval = Int(values, "Tuning.CivilianIntervalMs", c.CivilianInterval, 1000, 30000);
                c.RoadblockInterval = Int(values, "Tuning.RoadblockIntervalMs", c.RoadblockInterval, 10000, 600000);
                c.MaxRoadblocks = Int(values, "Tuning.MaxRoadblocks", c.MaxRoadblocks, 1, 5);
                c.LifetimeSeconds = Int(values, "Tuning.RoadblockLifetimeSeconds", c.LifetimeSeconds, 20, 600);
                c.PoliceRadius = Float(values, "Tuning.PoliceRadius", c.PoliceRadius, 25, 250);
                c.CivilianRadius = Float(values, "Tuning.CivilianRadius", c.CivilianRadius, 10, 120);
                c.RoadblockDistance = Float(values, "Tuning.RoadblockDistance", c.RoadblockDistance, 65, 200);
                c.SpeedThreshold = Float(values, "Tuning.SpeedThresholdMps", c.SpeedThreshold, 5, 80);
            }
            catch (Exception ex) { Log(ex); }
            return c;
        }
        private static bool Bool(Dictionary<string, string> d, string k, bool fallback) { string s; bool v; return d.TryGetValue(k, out s) && bool.TryParse(s, out v) ? v : fallback; }
        private static int Int(Dictionary<string, string> d, string k, int fallback, int min, int max) { string s; int v; return d.TryGetValue(k, out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? Math.Max(min, Math.Min(max, v)) : fallback; }
        private static float Float(Dictionary<string, string> d, string k, float fallback, float min, float max) { string s; float v; return d.TryGetValue(k, out s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !float.IsNaN(v) && !float.IsInfinity(v) ? Math.Max(min, Math.Min(max, v)) : fallback; }
        private static Keys Key(Dictionary<string, string> d, string k, Keys fallback) { string s; Keys v; return d.TryGetValue(k, out s) && Enum.TryParse(s, true, out v) && Enum.IsDefined(typeof(Keys), v) ? v : fallback; }
    }
}
