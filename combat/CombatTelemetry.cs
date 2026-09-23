using System;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordCombatBridge
{
    // Read-only observations from the same native APIs used by the player
    // controller. A capability describes an available input, never a hit promise.
    public static class CombatTelemetry
    {
        public static string WeaponJson(Agent agent)
        {
            if (agent == null || !agent.IsActive() || !agent.IsHuman)
                return UnknownWeapon("NO_ACTIVE_HUMAN");
            try
            {
                MissionWeapon weapon = agent.WieldedWeapon;
                MissionWeapon offhand = agent.WieldedOffhandWeapon;
                bool hasShield = !offhand.IsEmpty && offhand.IsShield() && offhand.HitPoints > 0;
                if (weapon.IsEmpty || weapon.CurrentUsageItem == null)
                    return "{\"known\":true,\"equipped\":false,\"is_melee\":false,\"can_swing\":false,\"can_thrust\":false,\"can_block\":" + Bool(hasShield) +
                        ",\"block_capability_known\":" + Bool(hasShield) + ",\"has_shield\":" + Bool(hasShield) + ",\"reach\":null}";

                WeaponComponentData usage = weapon.CurrentUsageItem;
                bool melee = usage.IsMeleeWeapon;
                string swing = "false", thrust = "false";
                if (melee)
                {
                    // Test the two inputs used by the local pilot in the actual
                    // stance/offhand context. Positive damage alone is not proof
                    // that the selected item usage defines that attack animation.
                    try
                    {
                        int leftUsage = offhand.IsEmpty || offhand.CurrentUsageItem == null ? -1 :
                            MBItem.GetItemUsageIndex(offhand.CurrentUsageItem.ItemUsage);
                        bool mounted = agent.MountAgent != null;
                        bool leftStance = agent.GetIsLeftStance();
                        bool lowLook = agent.IsLookDirectionLow;
                        swing = Bool(usage.SwingDamage > 0 && MBItem.GetItemUsageStrikeType(usage.ItemUsage,
                            (int)Agent.UsageDirection.AttackUp, mounted, leftUsage, leftStance, lowLook) == (int)StrikeType.Swing);
                        thrust = Bool(usage.ThrustDamage > 0 && MBItem.GetItemUsageStrikeType(usage.ItemUsage,
                            (int)Agent.UsageDirection.AttackDown, mounted, leftUsage, leftStance, lowLook) == (int)StrikeType.Thrust);
                    }
                    catch { swing = "null"; thrust = "null"; }
                }
                string reach = "null";
                try
                {
                    float length = usage.GetRealWeaponLength();
                    if (!float.IsNaN(length) && !float.IsInfinity(length) && length > 0 && length < 10)
                        reach = CombatProtocol.Number(length);
                }
                catch { }
                // There is no public native CanBlock accessor. Until weapon-only
                // defense is validated, advertise blocking only with a wielded
                // intact shield; false with block_capability_known=false is unknown.
                return "{\"known\":true,\"equipped\":true,\"slot\":" + (int)agent.GetPrimaryWieldedItemIndex() +
                    ",\"offhand_slot\":" + (int)agent.GetOffhandWieldedItemIndex() +
                    ",\"item_id\":" + CombatProtocol.Json(weapon.Item.StringId) +
                    ",\"usage\":" + CombatProtocol.Json(usage.ItemUsage) +
                    ",\"class\":" + CombatProtocol.Json(usage.WeaponClass.ToString()) +
                    ",\"is_melee\":" + Bool(melee) + ",\"can_swing\":" + swing + ",\"can_thrust\":" + thrust +
                    ",\"can_block\":" + Bool(hasShield) + ",\"block_capability_known\":" + Bool(hasShield) +
                    ",\"has_shield\":" + Bool(hasShield) + ",\"reach\":" + reach +
                    ",\"reach_kind\":\"native_weapon_length_metres\",\"ammo\":" + weapon.Ammo + "}";
            }
            catch { return UnknownWeapon("WEAPON_READ_FAILED"); }
        }

        public static string AttackJson(Agent agent)
        {
            if (agent == null || !agent.IsActive() || !agent.IsHuman)
                return "{\"known\":false,\"active\":false,\"block_direction\":null}";
            try
            {
                // Native CustomBattleAutoBlockModel and SandboxAutoBlockModel
                // inspect channel 1, not MovementFlags (which are input intent).
                Agent.ActionStage stage = agent.GetCurrentActionStage(1);
                Agent.ActionCodeType type = agent.GetCurrentActionType(1);
                Agent.UsageDirection direction = agent.GetCurrentActionDirection(1);
                bool active = (type == Agent.ActionCodeType.ReadyMelee &&
                    (stage == Agent.ActionStage.AttackReady || stage == Agent.ActionStage.AttackQuickReady)) ||
                    (type == Agent.ActionCodeType.ReleaseMelee && stage == Agent.ActionStage.AttackRelease);
                string typeName = type == Agent.ActionCodeType.ReadyMelee ? "ReadyMelee" :
                    type == Agent.ActionCodeType.ReleaseMelee ? "ReleaseMelee" : type.ToString();
                return "{\"known\":true,\"channel\":1,\"active\":" + Bool(active) +
                    ",\"type\":" + CombatProtocol.Json(typeName) +
                    ",\"stage\":" + CombatProtocol.Json(stage.ToString()) +
                    ",\"direction\":" + CombatProtocol.Json(DirectionName(direction)) +
                    ",\"block_direction\":" + CombatProtocol.Json(active ? BlockDirection(direction) : null) +
                    ",\"movement_flags\":" + ((uint)agent.MovementFlags) + "}";
            }
            catch { return "{\"known\":false,\"active\":false,\"block_direction\":null}"; }
        }

        // Matches MissionMainAgentController.ControlTick's native auto-block
        // mapping: opposing left/right are mirrored, up/down are unchanged.
        public static string BlockDirection(Agent.UsageDirection direction)
        {
            switch (direction)
            {
                case Agent.UsageDirection.AttackUp: return "up";
                case Agent.UsageDirection.AttackDown: return "down";
                case Agent.UsageDirection.AttackLeft: return "right";
                case Agent.UsageDirection.AttackRight: return "left";
                default: return null;
            }
        }

        private static string DirectionName(Agent.UsageDirection direction)
        {
            // Enum aliases such as AttackBegin can otherwise replace AttackUp.
            switch (direction)
            {
                case Agent.UsageDirection.AttackUp: return "AttackUp";
                case Agent.UsageDirection.AttackDown: return "AttackDown";
                case Agent.UsageDirection.AttackLeft: return "AttackLeft";
                case Agent.UsageDirection.AttackRight: return "AttackRight";
                case Agent.UsageDirection.None: return "None";
                default: return direction.ToString();
            }
        }

        private static string UnknownWeapon(string reason)
        {
            return "{\"known\":false,\"reason\":" + CombatProtocol.Json(reason) +
                ",\"is_melee\":false,\"can_swing\":null,\"can_thrust\":null,\"can_block\":false,\"block_capability_known\":false,\"reach\":null}";
        }

        private static string Bool(bool value) { return value ? "true" : "false"; }
    }
}
