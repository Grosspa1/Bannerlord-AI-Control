using System;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace BannerlordCombatBridge
{
    // The lease and wall-clock watchdog live in the mission bridge. This adapter
    // owns only the ordinary player input surface for the lease's lifetime.
    public sealed class PlayerInput
    {
        private Mission _mission;
        private Agent _agent;
        private MissionMainAgentController _controller;
        private bool _ownsController;
        private Command _command;
        private bool _jumpPending;
        private Vec2 _lastMovement;
        private Agent.MovementControlFlag _lastFlags;
        private Agent.EventControlFlag _lastEvents;
        private bool _hasWritten;

        private sealed class Command
        {
            public float Forward, Strafe, Yaw, Pitch;
            public Agent.MovementControlFlag CombatFlags;
            public bool Jump;
        }

        public bool IsAcquired { get { return _ownsController; } }

        public static string ValidateTickOrder(Mission mission, MissionBehavior driver)
        {
            if (mission == null || driver == null)
                return "The combat input tick driver is unavailable.";
            MissionMainAgentController controller = mission.GetMissionBehavior<MissionMainAgentController>();
            int controllerIndex = mission.MissionBehaviors.IndexOf(controller);
            int driverIndex = mission.MissionBehaviors.IndexOf(driver);
            // Mission.OnPreTick iterates from the end of this list to the start.
            return controllerIndex >= 0 && driverIndex > controllerIndex ? null :
                "The combat input driver must tick before the native player controller.";
        }

        // Null means eligible. Keep this fail closed: a battle-looking scene by
        // itself is insufficient, and multiplayer/replay is never eligible.
        public static string UnavailableReason(Mission mission)
        {
            if (GameNetwork.IsMultiplayerOrReplay)
                return "Single-player battles only.";
            if (mission == null || mission != Mission.Current)
                return "No current mission.";
            if (mission.IsFinalized || mission.MissionEnded || mission.IsMissionEnding || mission.MissionIsEnding ||
                mission.CurrentState != Mission.State.Continuing || !mission.IsLoadingFinished)
                return "The mission is loading or ending.";
            if (mission.Mode != MissionMode.Battle || !mission.IsDeploymentFinished ||
                mission.CombatType != Mission.MissionCombatType.Combat ||
                !(mission.IsFieldBattle || mission.IsSiegeBattle || mission.IsSallyOutBattle))
                return "An active land battle is required.";
            MissionState state = MissionState.Current;
            if (state == null || state.CurrentMission != mission || state.Paused)
                return "The mission is paused or inactive.";
            Game game = Game.Current;
            if (game == null || game.GameStateManager == null ||
                game.GameStateManager.ActiveState != state ||
                game.GameStateManager.ActiveStateDisabledByUser)
                return "The mission is not the active game state.";
            Agent agent = mission.MainAgent;
            if (agent == null || !agent.IsActive() || !agent.IsHuman ||
                !agent.IsPlayerControlled || agent.Health <= 0 || agent.Mission != mission)
                return "An active human player agent is required.";
            if (agent.MountAgent != null)
                return "The first combat build supports the player on foot only.";
            if (agent.IsUsingGameObject || agent.IsLookDirectionLocked ||
                agent.MovementLockedState != AgentMovementLockedState.None)
                return "The player is using an object or movement is locked.";
            MissionMainAgentController controller = mission.GetMissionBehavior<MissionMainAgentController>();
            if (controller == null || controller.MissionScreen == null)
                return "The native player controller is unavailable.";
            var screen = controller.MissionScreen;
            if (!screen.IsMissionTickable || screen.IsFocusLost || screen.IsPhotoModeEnabled ||
                screen.IsCheatGhostMode || screen.IsConversationActive || screen.IsDeploymentActive ||
                screen.IsRadialMenuActive || screen.MouseVisible)
                return "Close menus and focus the active battle before taking control.";
            return null;
        }

        public string Acquire(Mission mission)
        {
            if (_ownsController)
                return "Player input is already acquired.";
            string reason = UnavailableReason(mission);
            if (reason != null)
                return reason;
            MissionMainAgentController controller = mission.GetMissionBehavior<MissionMainAgentController>();
            // A disabled controller may belong to the game or another mod.
            // Never claim it and later re-enable someone else's ownership.
            if (controller.IsDisabled)
                return "The native player controller is already disabled.";
            _mission = mission;
            _agent = mission.MainAgent;
            _controller = controller;
            _command = null;
            _jumpPending = false;
            _controller.IsDisabled = true;
            _ownsController = true;
            try { Neutralize(); }
            catch { Release(); throw; }
            return null;
        }

        public static string ValidateArguments(string args)
        {
            Command command;
            return Parse(args, out command);
        }

        public string Apply(Mission mission, string args)
        {
            Command next;
            string reason = Parse(args, out next);
            if (reason != null)
                return reason;
            reason = CheckOwnership(mission);
            if (reason != null)
            {
                Release();
                return reason;
            }
            _command = next;
            _jumpPending = next.Jump;
            return null;
        }

        public string Tick(Mission mission)
        {
            string reason = CheckOwnership(mission);
            if (reason != null)
            {
                Release();
                return reason;
            }
            Command command = _command;
            // MissionScreen.UpdateCamera restores this native Boolean every
            // frame. Assert it here, before the native controller's pre-tick.
            _controller.IsDisabled = true;
            if (command == null)
            {
                Neutralize();
                return null;
            }
            // Bannerlord's native controller writes MovementAxisX to x and
            // MovementAxisY to y. Positive x is right; positive y is forward.
            float length = (float)Math.Sqrt(command.Strafe * command.Strafe + command.Forward * command.Forward);
            float scale = length > 1f ? 1f / length : 1f;
            _lastMovement = new Vec2(command.Strafe * scale, command.Forward * scale);
            _lastFlags = command.CombatFlags;
            _lastEvents = _jumpPending ? Agent.EventControlFlag.Jump : Agent.EventControlFlag.None;
            _hasWritten = true;
            _agent.MovementInputVector = _lastMovement;
            _agent.MovementFlags = _lastFlags;
            float horizontal = (float)Math.Cos(command.Pitch);
            _agent.LookDirection = new Vec3(-(float)Math.Sin(command.Yaw) * horizontal,
                (float)Math.Cos(command.Yaw) * horizontal, (float)Math.Sin(command.Pitch));
            _agent.EventControlFlags = _lastEvents;
            _jumpPending = false;
            return null;
        }

        public void Release()
        {
            if (!_ownsController)
                return;
            try
            {
                try
                {
                    if (_hasWritten && _mission != null && !_mission.IsFinalized && _agent != null &&
                        _agent.Mission == _mission && _agent.IsActive())
                    {
                        // Do not erase values changed by another input producer.
                        Vec2 movement = _agent.MovementInputVector;
                        if (movement.x == _lastMovement.x && movement.y == _lastMovement.y)
                            _agent.MovementInputVector = Vec2.Zero;
                        if (_agent.MovementFlags == _lastFlags)
                            _agent.MovementFlags = Agent.MovementControlFlag.None;
                        if (_agent.EventControlFlags == _lastEvents)
                            _agent.EventControlFlags = Agent.EventControlFlag.None;
                    }
                }
                finally
                {
                    if (_controller != null && _controller.IsDisabled)
                        _controller.IsDisabled = false;
                }
            }
            finally
            {
                _ownsController = false;
                _hasWritten = false;
                _jumpPending = false;
                _command = null;
                _controller = null;
                _agent = null;
                _mission = null;
            }
        }

        private string CheckOwnership(Mission mission)
        {
            if (!_ownsController)
                return "Player input has not been acquired.";
            if (mission != _mission || mission == null || mission.MainAgent != _agent)
                return "The mission or player agent changed.";
            if (_controller == null || mission.GetMissionBehavior<MissionMainAgentController>() != _controller)
                return "The native player controller changed.";
            return UnavailableReason(mission);
        }

        private void Neutralize()
        {
            _lastMovement = Vec2.Zero;
            _lastFlags = Agent.MovementControlFlag.None;
            _lastEvents = Agent.EventControlFlag.None;
            _hasWritten = true;
            _agent.MovementInputVector = Vec2.Zero;
            _agent.MovementFlags = Agent.MovementControlFlag.None;
            _agent.EventControlFlags = Agent.EventControlFlag.None;
        }

        private static string Parse(string args, out Command command)
        {
            command = null;
            string[] fields = (args ?? "").Split('|');
            if (fields.Length != 7)
                return "Expected forward|strafe|yaw|pitch|attack|block|jump.";
            Command parsed = new Command();
            if (!Number(fields[0], -1f, 1f, out parsed.Forward) ||
                !Number(fields[1], -1f, 1f, out parsed.Strafe))
                return "Movement must be finite numbers between -1 and 1.";
            if (!Number(fields[2], -(float)Math.PI, (float)Math.PI, out parsed.Yaw) ||
                !Number(fields[3], -1.4f, 1.4f, out parsed.Pitch))
                return "Yaw must be between -pi and pi and pitch between -1.4 and 1.4 radians.";
            Agent.MovementControlFlag attack, block;
            if (!Direction(fields[4], false, out attack) || !Direction(fields[5], true, out block))
                return "Attack and block must be none, up, down, left, or right.";
            if (attack != Agent.MovementControlFlag.None && block != Agent.MovementControlFlag.None)
                return "Attack and block cannot be held together.";
            if (fields[6] != "0" && fields[6] != "1")
                return "Jump must be 0 or 1.";
            parsed.CombatFlags = attack | block;
            parsed.Jump = fields[6] == "1";
            command = parsed;
            return null;
        }

        private static bool Number(string text, float min, float max, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;
        }

        private static bool Direction(string text, bool defend, out Agent.MovementControlFlag flag)
        {
            flag = Agent.MovementControlFlag.None;
            switch (text)
            {
                case "none": return true;
                case "up": flag = defend ? Agent.MovementControlFlag.DefendUp : Agent.MovementControlFlag.AttackUp; return true;
                case "down": flag = defend ? Agent.MovementControlFlag.DefendDown : Agent.MovementControlFlag.AttackDown; return true;
                case "left": flag = defend ? Agent.MovementControlFlag.DefendLeft : Agent.MovementControlFlag.AttackLeft; return true;
                case "right": flag = defend ? Agent.MovementControlFlag.DefendRight : Agent.MovementControlFlag.AttackRight; return true;
                default: return false;
            }
        }
    }
}
