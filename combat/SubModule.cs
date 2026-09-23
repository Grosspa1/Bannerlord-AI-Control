using System;
using System.Globalization;
using System.IO;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace BannerlordCombatBridge
{
    public sealed class SubModule : MBSubModuleBase
    {
        internal static readonly string Session = Guid.NewGuid().ToString("N");
        internal static CombatMissionBehavior Active;
        private long _nextIdle;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Directory.CreateDirectory(CombatFiles.Root);
            CombatFiles.Log("Loaded session=" + Session);
            WriteIdle();
        }

        public override void OnBeforeMissionBehaviorInitialize(Mission mission)
        {
            // The later OnMissionBehaviorInitialize hook runs after behaviors'
            // initialization and would skip registering our application watchdog.
            base.OnBeforeMissionBehaviorInitialize(mission);
            if (!GameNetwork.IsMultiplayerOrReplay)
                mission.AddMissionBehavior(new CombatMissionBehavior());
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            // Mission ticks may stop behind menus. Wall-clock expiry still releases input.
            if (Active != null) Active.Watchdog();
            else if (CombatProtocol.NowMs() >= _nextIdle) WriteIdle();
        }

        protected override void OnSubModuleUnloaded()
        {
            if (Active != null) Active.Shutdown("module_unloaded");
            base.OnSubModuleUnloaded();
        }

        private void WriteIdle()
        {
            _nextIdle = CombatProtocol.NowMs() + 500;
            CombatFiles.Write("state.json", "{\"schema\":\"bannerlord.combat.v1\",\"session\":" +
                CombatProtocol.Json(Session) + ",\"updated_utc_ms\":" + CombatProtocol.NowMs() +
                ",\"active\":false,\"enabled\":false,\"reason\":\"NO_MISSION\"}");
        }
    }

    internal static class CombatFiles
    {
        internal const string Root = @"C:\Users\Public\BannerlordCombatBridge";
        internal static string ReadCommand()
        {
            try
            {
                using (FileStream stream = new FileStream(Path.Combine(Root, "command.json"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > 4096) return "COMMAND_TOO_LARGE";
                    using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true)))
                        return reader.ReadToEnd();
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (DecoderFallbackException) { return "INVALID_UTF8"; }
        }

        internal static void Write(string name, string json)
        {
            string path = Path.Combine(Root, name);
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (IOException) { /* Drop this sample; never sleep on the game thread. */ }
            catch (UnauthorizedAccessException) { }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        internal static void Log(string message)
        {
            try { File.AppendAllText(Path.Combine(Root, "combat.log"), DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine); }
            catch { }
        }
    }

    public sealed class CombatMissionBehavior : MissionLogic
    {
        private readonly PlayerInput _input = new PlayerInput();
        private string _token = Guid.NewGuid().ToString("N");
        private string _lastRaw;
        private string _reason = "DISABLED";
        private long _lastSequence;
        private long _leaseUntil;
        private long _inputLeaseUntil;
        private long _nextRead;
        private long _nextState;
        private bool _enabled;
        private bool _emergencyStop;
        private bool _closed;
        private bool _f10Down;
        private bool _f9Down;

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            if (SubModule.Active != null) SubModule.Active.Shutdown("mission_replaced");
            SubModule.Active = this;
            CombatFiles.Log("Mission attached token=" + _token);
        }

        public override void OnPreMissionTick(float dt)
        {
            if (_closed) return;
            try
            {
                Watchdog();
                long now = CombatProtocol.NowMs();
                if (now >= _nextRead)
                {
                    _nextRead = now + 20;
                    ProcessCommand(now);
                }
                if (_input.IsAcquired)
                {
                    string reason = TickOrderReason() ?? _input.Tick(Mission);
                    if (reason != null) Disable(reason);
                }
                if (now >= _nextState) Publish(now);
            }
            catch (Exception ex)
            {
                Disable("CONTROL_ERROR");
                CombatFiles.Log(ex.ToString());
            }
        }

        public void Watchdog()
        {
            if (_closed) return;
            try
            {
                bool f10 = Input.IsKeyDownImmediate(InputKey.F10);
                bool f9 = Input.IsKeyDownImmediate(InputKey.F9);
                if (f10 && !_f10Down)
                {
                    _emergencyStop = true;
                    Disable("EMERGENCY_STOP_F10", true);
                    InformationManager.DisplayMessage(new InformationMessage("Combat bridge stopped. F9 clears the stop latch."));
                }
                if (_emergencyStop && f9 && !_f9Down && !f10)
                {
                    _emergencyStop = false;
                    _reason = "DISABLED";
                    InformationManager.DisplayMessage(new InformationMessage("Combat bridge ready. A new command must enable control."));
                }
                _f10Down = f10;
                _f9Down = f9;
                // Orders and enable packets may keep the command connection alive,
                // but must never extend an older attack/movement input.
                if (_input.IsAcquired && CombatProtocol.NowMs() >= _inputLeaseUntil)
                {
                    _input.Release();
                    _inputLeaseUntil = 0;
                }
                if (_enabled)
                {
                    string reason = FormationOrders.UnavailableReason(Mission);
                    if (Mission != TaleWorlds.MountAndBlade.Mission.Current) reason = "MISSION_CHANGED";
                    if (_input.IsAcquired) reason = reason ?? PlayerInput.UnavailableReason(Mission);
                    if (reason != null) Disable(reason);
                    else if (CombatProtocol.NowMs() >= _leaseUntil) Disable("COMMAND_LEASE_EXPIRED");
                }
                if (CombatProtocol.NowMs() >= _nextState) Publish(CombatProtocol.NowMs());
            }
            catch (Exception ex)
            {
                Disable("WATCHDOG_ERROR");
                CombatFiles.Log(ex.ToString());
            }
        }

        private void ProcessCommand(long now)
        {
            string raw = CombatFiles.ReadCommand();
            if (raw == null || raw == _lastRaw) return;
            _lastRaw = raw;
            CombatCommand command = null;
            try
            {
                command = CombatProtocol.Parse(raw);
                CombatProtocol.Validate(command, SubModule.Session, _token, _lastSequence, now);
                // Consume before any native call: ambiguous failures cannot repeat an action.
                _lastSequence = command.seq;
                if ((command.kind == "status" || command.kind == "enable" || command.kind == "release") && command.args.Length != 0)
                    throw new InvalidOperationException("UNEXPECTED_ARGUMENTS");
                string result = "accepted";
                if (command.kind == "release") Disable("RELEASED");
                else if (command.kind == "status") result = "state_requested";
                else
                {
                    if (_emergencyStop) throw new InvalidOperationException("EMERGENCY_STOP_F10");
                    string unavailable = FormationOrders.UnavailableReason(Mission);
                    if (unavailable != null) throw new InvalidOperationException(unavailable);
                    if (command.kind == "enable") { _enabled = true; _reason = "ENABLED"; }
                    else
                    {
                        if (!_enabled) throw new InvalidOperationException("CONTROL_NOT_ENABLED");
                        if (command.kind == "input")
                        {
                            string failure = TickOrderReason();
                            if (failure == null && !_input.IsAcquired) failure = _input.Acquire(Mission);
                            if (failure == null) failure = _input.Apply(Mission, command.args);
                            if (failure != null) throw new InvalidOperationException(failure);
                            _inputLeaseUntil = command.sent_utc_ms + command.ttl_ms;
                            result = "input_accepted_not_hit_confirmation";
                        }
                        else if (command.kind == "order") result = FormationOrders.Execute(Mission, command.args);
                    }
                    _leaseUntil = command.sent_utc_ms + command.ttl_ms;
                }
                Reply(command, true, result);
            }
            catch (Exception ex)
            {
                // Invalid/stale commands cannot leave a preceding movement or attack held.
                if (_enabled || _input.IsAcquired) Disable("COMMAND_REJECTED");
                Reply(command, false, ex.Message);
            }
            Publish(now);
        }

        private void Reply(CombatCommand command, bool ok, string message)
        {
            CombatFiles.Write("response.json", "{\"session\":" + CombatProtocol.Json(SubModule.Session) +
                ",\"mission\":" + CombatProtocol.Json(command == null ? _token : command.mission) +
                ",\"seq\":" + (command == null ? 0 : command.seq) + ",\"ok\":" + (ok ? "true" : "false") +
                ",\"message\":" + CombatProtocol.Json(message) + ",\"updated_utc_ms\":" + CombatProtocol.NowMs() + "}");
        }

        private string TickOrderReason()
        {
            // Pre-ticks run in reverse list order. Refuse control if a mod has
            // inserted/replaced the native controller after us.
            return PlayerInput.ValidateTickOrder(Mission, this);
        }

        private void Disable(string reason, bool invalidate = false)
        {
            bool held = _enabled || _input.IsAcquired;
            _enabled = false;
            try { _input.Release(); }
            catch (Exception ex) { CombatFiles.Log("Release error: " + ex); }
            _leaseUntil = 0;
            _inputLeaseUntil = 0;
            _reason = reason;
            if (held || invalidate)
            {
                _token = Guid.NewGuid().ToString("N");
                _lastSequence = 0;
                CombatFiles.Log("Control released: " + reason);
            }
        }

        private void Publish(long now)
        {
            _nextState = now + 100;
            string unavailable = FormationOrders.UnavailableReason(Mission);
            StringBuilder json = new StringBuilder("{\"schema\":\"bannerlord.combat.v1\",\"session\":");
            json.Append(CombatProtocol.Json(SubModule.Session)).Append(",\"mission\":").Append(CombatProtocol.Json(_token))
                .Append(",\"updated_utc_ms\":").Append(now).Append(",\"active\":").Append(_closed ? "false" : "true")
                .Append(",\"eligible\":").Append(unavailable == null ? "true" : "false")
                .Append(",\"reason\":").Append(CombatProtocol.Json(unavailable ?? _reason))
                .Append(",\"enabled\":").Append(_enabled ? "true" : "false")
                .Append(",\"emergency_stop\":").Append(_emergencyStop ? "true" : "false")
                .Append(",\"input_owned\":").Append(_input.IsAcquired ? "true" : "false")
                .Append(",\"lease_remaining_ms\":").Append(Math.Max(0, _leaseUntil - now))
                .Append(",\"input_lease_remaining_ms\":").Append(Math.Max(0, _inputLeaseUntil - now))
                .Append(",\"mode\":").Append(CombatProtocol.Json(Mission.Mode.ToString()))
                .Append(",\"scene\":").Append(CombatProtocol.Json(Mission.SceneName))
                .Append(",\"main_agent\":").Append(AgentJson(Mission.MainAgent))
                .Append(",\"formations\":").Append(FormationOrders.Snapshot(Mission))
                .Append(",\"nearby_enemies\":[");
            int written = 0;
            Agent main = Mission.MainAgent;
            if (main != null && main.Team != null)
            {
                // Bounded output in large battles. This is proximity, not line-of-sight.
                foreach (Agent agent in Mission.Agents)
                {
                    if (!agent.IsActive() || !agent.IsHuman || agent.Team == null || !main.Team.IsEnemyOf(agent.Team)) continue;
                    if ((agent.Position - main.Position).LengthSquared > 10000f) continue;
                    if (written++ != 0) json.Append(',');
                    json.Append(AgentJson(agent));
                    if (written == 32) break;
                }
            }
            json.Append("],\"enemy_sample_limit\":32}");
            CombatFiles.Write("state.json", json.ToString());
        }

        private static string AgentJson(Agent agent)
        {
            if (agent == null) return "null";
            Vec3 p = agent.Position;
            Vec3 look = agent.LookDirection;
            return "{\"index\":" + agent.Index + ",\"name\":" + CombatProtocol.Json(agent.Name) +
                ",\"health\":" + CombatProtocol.Number(agent.Health) + ",\"active\":" + (agent.IsActive() ? "true" : "false") +
                ",\"mounted\":" + (agent.MountAgent != null ? "true" : "false") +
                ",\"position\":[" + CombatProtocol.Number(p.x) + "," + CombatProtocol.Number(p.y) + "," + CombatProtocol.Number(p.z) +
                "],\"look\":[" + CombatProtocol.Number(look.x) + "," + CombatProtocol.Number(look.y) + "," + CombatProtocol.Number(look.z) + "]}";
        }

        public void Shutdown(string reason)
        {
            if (_closed) return;
            Disable(reason);
            _closed = true;
            if (SubModule.Active == this) SubModule.Active = null;
        }
        protected override void OnEndMission() { Shutdown("mission_ended"); base.OnEndMission(); }
        public override void OnRemoveBehavior() { Shutdown("behavior_removed"); base.OnRemoveBehavior(); }
        public override void OnMissionStateDeactivated() { Disable("mission_deactivated", true); base.OnMissionStateDeactivated(); }
    }
}
