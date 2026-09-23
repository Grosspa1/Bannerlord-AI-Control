using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace BannerlordCombatBridge
{
    // Called only from the mission's main thread. No controller or owner is assigned here.
    public static class FormationOrders
    {
        public static string UnavailableReason(Mission mission)
        {
            Agent player;
            Team team;
            OrderController controller;
            return GetContext(mission, out player, out team, out controller);
        }

        public static string Execute(Mission mission, string argument)
        {
            int index;
            string order;
            float x;
            float y;
            Parse(argument, out index, out order, out x, out y);

            Agent player;
            Team team;
            OrderController controller;
            string unavailable = GetContext(mission, out player, out team, out controller);
            if (unavailable != null)
                throw new InvalidOperationException("FORMATION_CONTEXT: " + unavailable);

            Formation target = null;
            foreach (Formation formation in team.FormationsIncludingEmpty)
            {
                if (formation != null && formation.Index == index)
                {
                    target = formation;
                    break;
                }
            }
            if (!CanCommand(target, player, team, controller))
                throw new InvalidOperationException("FORMATION_NOT_COMMANDABLE: The selected formation is empty or is not under the player's command.");

            OrderType nativeOrder = NativeOrder(order);
            WorldPosition position = default(WorldPosition);
            if (order == "move")
            {
                Vec2 point = new Vec2(x, y);
                if (!mission.IsPositionInsideBoundaries(point) ||
                    !mission.IsPositionInsideHardBoundaries(point) ||
                    mission.IsPositionInsideAnyBlockerNavMeshFace2D(point))
                    throw new InvalidOperationException("INVALID_ORDER_POSITION: Position lies outside the battlefield or inside a navigation blocker.");

                // Restrict this adapter to field battles: the player's height is a safe
                // initial query height, but does not identify a siege floor or ship deck.
                position = new WorldPosition(mission.Scene, new Vec3(x, y, player.Position.z));
                if (!mission.IsOrderPositionAvailable(ref position, team))
                    throw new InvalidOperationException("INVALID_ORDER_POSITION: Position has no valid navmesh or is unavailable to this team.");
            }

            var previousSelection = new List<Formation>();
            foreach (Formation selected in controller.SelectedFormations)
                previousSelection.Add(selected);

            bool orderAttempted = false;
            Exception failure = null;
            bool selectionRestored = false;
            try
            {
                controller.ClearSelectedFormations();
                controller.SelectFormation(target);
                if (controller.SelectedFormations.Count != 1 ||
                    !object.ReferenceEquals(controller.SelectedFormations[0], target) ||
                    !CanCommand(target, player, team, controller))
                    throw new InvalidOperationException("FORMATION_SELECTION_CHANGED: Native selection did not retain exactly the requested formation.");

                // SetOrder preserves vanilla pre/post hooks, AI handover for already
                // player-owned formations, agent updates, gestures, and order events.
                orderAttempted = true;
                if (order == "move")
                    controller.SetOrderWithPosition(nativeOrder, position);
                else
                    controller.SetOrder(nativeOrder);

                if (ObservedOrder(target, order) != nativeOrder)
                    throw new InvalidOperationException("The native order was not observable immediately after dispatch.");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // Selection is UI state, so put back the still-selectable originals.
                // Never retain another formation solely because its index was reused.
                try
                {
                    controller.ClearSelectedFormations();
                    foreach (Formation selected in previousSelection)
                    {
                        if (selected != null && object.ReferenceEquals(selected.Team, team) &&
                            controller.IsFormationSelectable(selected))
                            controller.SelectFormation(selected);
                    }
                    selectionRestored = controller.SelectedFormations.Count == previousSelection.Count;
                    if (selectionRestored)
                    {
                        for (int i = 0; i < previousSelection.Count; i++)
                        {
                            if (!object.ReferenceEquals(controller.SelectedFormations[i], previousSelection[i]))
                                selectionRestored = false;
                        }
                    }
                }
                catch (Exception restoreError)
                {
                    if (failure == null)
                        failure = restoreError;
                }
            }

            if (failure != null)
            {
                if (orderAttempted)
                    throw new InvalidOperationException("FORMATION_RESULT_UNCERTAIN: A native order was attempted. Inspect current state before issuing another order. " + failure.Message, failure);
                throw failure;
            }

            return "{\"issued\":true,\"formation_index\":" + index.ToString(CultureInfo.InvariantCulture) +
                ",\"order\":" + Quote(order) + ",\"observed_order\":" + Quote(ObservedOrder(target, order).ToString()) +
                ",\"selection_restored\":" + (selectionRestored ? "true" : "false") + "}";
        }

        public static string Snapshot(Mission mission)
        {
            Agent player;
            Team team;
            OrderController controller;
            string unavailable = GetContext(mission, out player, out team, out controller);
            if (unavailable != null)
                return "{\"available\":false,\"reason\":" + Quote(unavailable) + ",\"formations\":[]}";

            var json = new StringBuilder("{\"available\":true,\"reason\":null,\"formations\":[");
            bool first = true;
            foreach (Formation formation in team.FormationsIncludingEmpty)
            {
                if (!CanCommand(formation, player, team, controller))
                    continue;
                if (!first) json.Append(',');
                first = false;
                Vec2 position = formation.CurrentPosition;
                json.Append("{\"index\":").Append(formation.Index.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"class\":").Append(Quote(formation.RepresentativeClass.ToString()));
                json.Append(",\"units\":").Append(formation.CountOfUnits.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"x\":").Append(Number(position.x));
                json.Append(",\"y\":").Append(Number(position.y));
                json.Append(",\"delegated_to_ai\":").Append(formation.IsAIControlled ? "true" : "false");
                json.Append(",\"movement\":").Append(Quote(OrderController.GetActiveMovementOrderOf(formation).ToString()));
                json.Append(",\"arrangement\":").Append(Quote(OrderController.GetActiveArrangementOrderOf(formation).ToString()));
                json.Append(",\"firing\":").Append(Quote(OrderController.GetActiveFiringOrderOf(formation).ToString()));
                json.Append('}');
            }
            return json.Append("]}").ToString();
        }

        private static string GetContext(Mission mission, out Agent player, out Team team, out OrderController controller)
        {
            player = null;
            team = null;
            controller = null;
            if (GameNetwork.IsMultiplayerOrReplay)
                return "Single-player live missions only.";
            if (mission == null || !object.ReferenceEquals(mission, Mission.Current) || mission.IsFinalized ||
                mission.MissionEnded || mission.IsMissionEnding || mission.MissionIsEnding ||
                mission.CurrentState != Mission.State.Continuing || !mission.IsLoadingFinished)
                return "No active loaded mission.";
            if (!mission.IsFieldBattle || mission.IsNavalBattle || mission.IsNavalRaidBattle ||
                mission.Mode != MissionMode.Battle || mission.CombatType != Mission.MissionCombatType.Combat ||
                !mission.IsDeploymentFinished)
                return "Formation orders currently require a deployed field battle.";
            MissionState state = MissionState.Current;
            if (state == null || state.CurrentMission != mission || state.Paused)
                return "The mission is paused or inactive.";
            Game game = Game.Current;
            if (game == null || game.GameStateManager == null ||
                game.GameStateManager.ActiveState != state || game.GameStateManager.ActiveStateDisabledByUser)
                return "The mission is not the active game state.";
            player = mission.MainAgent;
            team = mission.PlayerTeam;
            if (player == null || !player.IsActive() || !player.IsHuman || !player.IsPlayerControlled ||
                player.Health <= 0 || player.Mission != mission || team == null || !object.ReferenceEquals(player.Team, team))
                return "A living player-controlled commander on the player team is required.";
            if (player.IsUsingGameObject || player.MovementLockedState != AgentMovementLockedState.None)
                return "The player is using an object or movement is locked.";
            MissionMainAgentController playerController = mission.GetMissionBehavior<MissionMainAgentController>();
            if (playerController == null || playerController.MissionScreen == null)
                return "The native player controller is unavailable.";
            var screen = playerController.MissionScreen;
            if (!screen.IsMissionTickable || screen.IsFocusLost || screen.IsPhotoModeEnabled ||
                screen.IsCheatGhostMode || screen.IsConversationActive || screen.IsDeploymentActive ||
                screen.IsRadialMenuActive || screen.MouseVisible)
                return "Close menus and focus the active battle before issuing orders.";
            controller = team.PlayerOrderController;
            if (controller == null || !object.ReferenceEquals(controller.Owner, player) ||
                !object.ReferenceEquals(controller.Team, team))
                return "The native player order controller does not belong to the current commander.";
            return null;
        }

        private static bool CanCommand(Formation formation, Agent player, Team team, OrderController controller)
        {
            return formation != null && formation.CountOfUnits > 0 &&
                object.ReferenceEquals(formation.Team, team) &&
                object.ReferenceEquals(formation.PlayerOwner, player) &&
                controller.IsFormationSelectable(formation);
        }

        // Public to allow the argument contract to be tested without running the engine.
        public static void Parse(string argument, out int index, out string order, out float x, out float y)
        {
            index = -1;
            order = null;
            x = 0;
            y = 0;
            string[] parts = (argument ?? "").Split('|');
            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 0)
                throw new ArgumentException("INVALID_FORMATION_ORDER: Expected formationIndex|order[|x|y].");
            order = parts[1];
            if (order == "move")
            {
                if (parts.Length != 4 || !TryFinite(parts[2], out x) || !TryFinite(parts[3], out y))
                    throw new ArgumentException("INVALID_FORMATION_ORDER: Move requires two finite scene coordinates.");
            }
            else if (parts.Length != 2 ||
                (order != "charge" && order != "stop" && order != "hold_fire" &&
                 order != "fire_at_will" && order != "line" && order != "shield_wall"))
                throw new ArgumentException("INVALID_FORMATION_ORDER: Supported orders are move, charge, stop, hold_fire, fire_at_will, line, shield_wall.");
        }

        private static OrderType NativeOrder(string order)
        {
            switch (order)
            {
                case "move": return OrderType.Move;
                case "charge": return OrderType.Charge;
                case "stop": return OrderType.StandYourGround;
                case "hold_fire": return OrderType.HoldFire;
                case "fire_at_will": return OrderType.FireAtWill;
                case "line": return OrderType.ArrangementLine;
                case "shield_wall": return OrderType.ArrangementCloseOrder;
                default: throw new ArgumentException("INVALID_FORMATION_ORDER: Unknown order.");
            }
        }

        private static OrderType ObservedOrder(Formation formation, string order)
        {
            if (order == "hold_fire" || order == "fire_at_will")
                return OrderController.GetActiveFiringOrderOf(formation);
            if (order == "line" || order == "shield_wall")
                return OrderController.GetActiveArrangementOrderOf(formation);
            return OrderController.GetActiveMovementOrderOf(formation);
        }

        private static bool TryFinite(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string Number(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Quote(string value)
        {
            if (value == null) return "null";
            var result = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '\\' || c == '"') result.Append('\\').Append(c);
                else if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else result.Append(c);
            }
            return result.Append('"').ToString();
        }
    }
}
