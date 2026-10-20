using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using src.utils;
using static src.jRandomSkills;

namespace src.player.skills
{
    public class Remik : ISkill
    {
        private const Skills skillName = Skills.Remik;
        private static readonly List<CCSPlayerController> holderBuffer = [];
        private static readonly Dictionary<uint, int> nextGrenadeTicks = [];
        // Blocked weapon index -> its NextPrimaryAttackTick from before the block
        private static readonly Dictionary<uint, int> blockedWeapons = [];
        private static readonly HashSet<uint> weaponsToBlock = [];

        public static void LoadSkill()
        {
            SkillUtils.RegisterSkill(skillName, SkillsInfo.GetValue<string>(skillName, "color"));
        }

        public static void NewRound()
        {
            nextGrenadeTicks.Clear();
            UnblockAll();
        }

        public static void PlayerDisconnect(uint playerIndex)
        {
            nextGrenadeTicks.Remove(playerIndex);
        }

        public static void EnableSkill(CCSPlayerController player)
        {
            if (!Instance.IsPlayerValid(player)) return;
            GiveGrenades(player);
            nextGrenadeTicks[player.Key()] = Server.TickCount + GetGrenadeIntervalTicks();
        }

        // Called from the round reset loop, which is not guarded, so nothing here may throw
        public static void DisableSkill(CCSPlayerController player)
        {
            try
            {
                nextGrenadeTicks.Remove(player.Key());
                // Other holders are blocked again on the next tick
                UnblockAll();
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[jRandomSkills] Remik.DisableSkill failed: {ex.Message}");
            }
        }

        public static void OnTick()
        {
            PlayerManager.FillSkillHolders(skillName, holderBuffer);
            if (holderBuffer.Count == 0)
            {
                UnblockAll();
                return;
            }

            weaponsToBlock.Clear();
            int now = Server.TickCount;
            float minSpeed = SkillsInfo.GetValue<float>(skillName, "minSpeed");

            foreach (var player in holderBuffer)
            {
                if (!player.IsValid || !player.PawnIsAlive) continue;

                var pawn = player.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                uint playerKey = player.Key();
                if (!nextGrenadeTicks.TryGetValue(playerKey, out int nextGrenadeTick) || now >= nextGrenadeTick)
                {
                    GiveGrenades(player);
                    nextGrenadeTicks[playerKey] = now + GetGrenadeIntervalTicks();
                }

                var velocity = pawn.AbsVelocity;
                if (velocity == null) continue;
                if (velocity.X * velocity.X + velocity.Y * velocity.Y >= minSpeed * minSpeed) continue;

                var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (activeWeapon == null || !activeWeapon.IsValid || !IsGun(activeWeapon)) continue;

                weaponsToBlock.Add(activeWeapon.Index);
                BlockWeapon(activeWeapon);
            }

            // Running again, switched away or dropped
            foreach (var weaponIndex in blockedWeapons.Keys.ToArray())
                if (!weaponsToBlock.Contains(weaponIndex))
                    UnblockWeapon(weaponIndex);
        }

        // Blocking with a tick far in the future, instead of one tick ahead, lets the client know
        // the weapon cannot fire, so it does not predict a shot animation that the server then rejects
        private static void BlockWeapon(CBasePlayerWeapon weapon)
        {
            if (weapon.NextPrimaryAttackTick == int.MaxValue) return;

            // A reload finishing while blocked overwrites the tick, so only the first value is kept
            blockedWeapons.TryAdd(weapon.Index, weapon.NextPrimaryAttackTick);
            weapon.NextPrimaryAttackTick = int.MaxValue;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
        }

        private static void UnblockWeapon(uint weaponIndex)
        {
            if (!blockedWeapons.Remove(weaponIndex, out int originalTick)) return;

            var weapon = Utilities.GetEntityFromIndex<CBasePlayerWeapon>((int)weaponIndex);
            if (weapon == null || !weapon.IsValid || weapon.NextPrimaryAttackTick != int.MaxValue) return;

            weapon.NextPrimaryAttackTick = Math.Max(originalTick, Server.TickCount);
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
        }

        private static void UnblockAll()
        {
            foreach (var weaponIndex in blockedWeapons.Keys.ToArray())
                UnblockWeapon(weaponIndex);
        }

        private static bool IsGun(CBasePlayerWeapon weapon)
        {
            var vdata = weapon.As<CCSWeaponBase>().VData;
            if (vdata != null)
                return vdata.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE || vdata.GearSlot == gear_slot_t.GEAR_SLOT_PISTOL;
            return WeaponPool.IsRifle(weapon.DesignerName) || WeaponPool.IsPistol(weapon.DesignerName);
        }

        private static int GetGrenadeIntervalTicks()
        {
            return Math.Max((int)Math.Ceiling(SkillsInfo.GetValue<float>(skillName, "grenadeInterval") / Server.TickInterval), 1);
        }

        // Giving a grenade the player cannot carry makes the game drop it on the ground, so only types
        // the player is missing are given, and never past the server's total grenade limit
        private static void GiveGrenades(CCSPlayerController player)
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) return;
            var services = pawn.WeaponServices;

            var held = new HashSet<string>(StringComparer.Ordinal);
            int total = 0;
            foreach (var item in services.MyWeapons)
            {
                if (item == null || !item.IsValid || item.Value == null || !item.Value.IsValid) continue;
                var weapon = item.Value;
                if (!WeaponPool.IsGrenade(weapon.DesignerName)) continue;

                held.Add(weapon.DesignerName);
                int ammoType = weapon.GetVData<CBasePlayerWeaponVData>()?.PrimaryAmmoType ?? -1;
                total += ammoType >= 0 && ammoType < services.Ammo.Length ? Math.Max((int)services.Ammo[ammoType], 1) : 1;
            }

            int limit = SkillUtils.CvarValue("ammo_grenade_limit_total", 4);
            bool isCT = player.Team == CsTeam.CounterTerrorist;
            // Molotov and incendiary share one slot
            bool hasFireGrenade = held.Contains("weapon_molotov") || held.Contains("weapon_incgrenade");

            (CsItem Item, bool Held)[] grenades =
            [
                (CsItem.HEGrenade, held.Contains("weapon_hegrenade")),
                (CsItem.Flashbang, held.Contains("weapon_flashbang")),
                (CsItem.SmokeGrenade, held.Contains("weapon_smokegrenade")),
                (isCT ? CsItem.IncendiaryGrenade : CsItem.Molotov, hasFireGrenade),
            ];

            foreach (var (item, isHeld) in grenades)
            {
                if (isHeld || total >= limit) continue;
                player.GiveNamedItem(item);
                total++;
            }
        }

        public class SkillConfig(Skills skill = skillName, bool active = true, string color = "#7fb3d5", CsTeam onlyTeam = CsTeam.None, bool disableOnFreezeTime = false, bool needsTeammates = false, string requiredPermission = "", float? hudDuration = null, float? descriptionHudDuration = null, int maxPerServer = -1, Rarity rarity = Rarity.Common, float minSpeed = 50f, float grenadeInterval = 20f) : SkillsInfo.DefaultSkillInfo(skill, active, color, onlyTeam, disableOnFreezeTime, needsTeammates, requiredPermission, hudDuration, descriptionHudDuration, maxPerServer, rarity)
        {
            public float MinSpeed { get; set; } = minSpeed;
            public float GrenadeInterval { get; set; } = grenadeInterval;
        }
    }
}
