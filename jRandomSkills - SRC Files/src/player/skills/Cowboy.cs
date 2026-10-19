using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using src.utils;
using static src.jRandomSkills;

namespace src.player.skills
{
    public class Cowboy : ISkill
    {
        private const Skills skillName = Skills.Cowboy;
        // In CS2 the R8 entity is named "weapon_deagle", so it can only be told apart by its item definition index
        private const ushort revolverDefIndex = 64;
        private static readonly List<CCSPlayerController> holderBuffer = [];
        private static int? originalMaxClip;
        private static int? originalReserveMax;
        private static readonly Dictionary<uint, (int Clip, int Reserve)> reloadStarts = [];

        public static void LoadSkill()
        {
            SkillUtils.RegisterSkill(skillName, SkillsInfo.GetValue<string>(skillName, "color"));
        }

        public static void NewRound()
        {
            RestoreRevolverData();
            reloadStarts.Clear();
        }

        public static void EnableSkill(CCSPlayerController player)
        {
            if (!Instance.IsPlayerValid(player)) return;

            ApplyRevolverData();

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) return;

            bool hasRevolver = false;
            foreach (var item in pawn.WeaponServices.MyWeapons)
            {
                if (item == null || !item.IsValid || item.Value == null || !item.Value.IsValid) continue;
                var weapon = item.Value;

                if (IsRevolver(weapon))
                {
                    hasRevolver = true;
                    continue;
                }

                if (IsPistol(weapon))
                    SkillUtils.SafeKillEntity<CBasePlayerWeapon>((uint)weapon.Index);
            }

            if (hasRevolver)
            {
                FillRevolver(player);
                return;
            }

            uint playerIndex = player.Key();
            Instance.AddTimer(.1f, () =>
            {
                var pl = PlayerManager.GetController(playerIndex);
                if (pl == null || !pl.IsValid || !pl.PawnIsAlive) return;
                pl.GiveNamedItem(CsItem.Revolver);
                Server.NextFrame(() =>
                {
                    var current = PlayerManager.GetController(playerIndex);
                    if (current != null && current.IsValid && current.PawnIsAlive)
                        FillRevolver(current);
                });
            }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        }

        public static void RoundEnd()
        {
            PlayerManager.FillSkillHolders(skillName, holderBuffer);
            foreach (var player in holderBuffer.ToArray())
                RemoveRevolver(player);
        }

        public static void DisableSkill(CCSPlayerController player)
        {
            RemoveRevolver(player);
            SkillUtils.ResetPrintHTML(player);

            PlayerManager.FillSkillHolders(skillName, holderBuffer);
            if (holderBuffer.All(holder => holder.Key() == player.Key()))
                RestoreRevolverData();
        }

        public static void OnTick()
        {
            PlayerManager.FillSkillHolders(skillName, holderBuffer);
            if (holderBuffer.Count == 0) return;

            int now = Server.TickCount;
            int fireDelayTicks = Math.Max((int)Math.Ceiling(SkillsInfo.GetValue<float>(skillName, "fireDelay") / Server.TickInterval), 1);
            bool hudFrame = SkillUtils.IsHudFrame();

            foreach (var player in holderBuffer)
            {
                if (!player.IsValid || !player.PawnIsAlive) continue;

                var pawn = player.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (activeWeapon == null || !activeWeapon.IsValid || !IsRevolver(activeWeapon))
                {
                    if (hudFrame) SkillUtils.ResetPrintHTML(player);
                    continue;
                }

                var weapon = activeWeapon.As<CCSWeaponBase>();
                UpdateReload(weapon);
                if (hudFrame) UpdateHUD(player, weapon);

                // Skip the hammer cocking delay before a primary shot
                if (weapon.PostponeFireReadyTicks > now)
                {
                    weapon.PostponeFireReadyTicks = now;
                    weapon.PostponeFireReadyFrac = 0;
                    Utilities.SetStateChanged(weapon, "CCSWeaponBase", "m_nPostponeFireReadyTicks");
                    Utilities.SetStateChanged(weapon, "CCSWeaponBase", "m_flPostponeFireReadyFrac");
                }

                // Shorten the time between shots
                if (weapon.NextPrimaryAttackTick > now + fireDelayTicks)
                {
                    weapon.NextPrimaryAttackTick = now + fireDelayTicks;
                    Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
                }
            }
        }

        // The game takes too little from the reserve when reloading past its own magazine size,
        // so the reserve is corrected by what the reload actually added to the magazine
        private static void UpdateReload(CCSWeaponBase weapon)
        {
            uint weaponIndex = weapon.Index;

            if (weapon.InReload)
            {
                reloadStarts.TryAdd(weaponIndex, (weapon.Clip1, weapon.ReserveAmmo[0]));
                return;
            }

            if (!reloadStarts.Remove(weaponIndex, out var start)) return;

            int reserve = Math.Max(start.Reserve - (weapon.Clip1 - start.Clip), 0);
            if (weapon.ReserveAmmo[0] == reserve) return;

            weapon.ReserveAmmo[0] = reserve;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
        }

        private static void UpdateHUD(CCSPlayerController player, CCSWeaponBase weapon)
        {
            var playerInfo = PlayerManager.GetPlayer(player);
            if (playerInfo == null || weapon.VData == null || weapon.VData.MaxClip1 <= 0) return;

            int magazines = (int)Math.Ceiling(weapon.ReserveAmmo[0] / (double)weapon.VData.MaxClip1);
            playerInfo.PrintHTML = player.GetTranslation("cowboy_hud", $"<font color='#c88a3d'>{magazines}</font>");
        }

        // The game clamps the magazine and the reserve to the weapon data limits, which every R8 shares,
        // so they are raised while someone has this skill and restored afterwards
        private static void ApplyRevolverData()
        {
            var vdata = GetRevolverData();
            if (vdata == null) return;

            originalMaxClip ??= vdata.MaxClip1;
            originalReserveMax ??= vdata.PrimaryReserveAmmoMax;

            int clipSize = Math.Max(SkillsInfo.GetValue<int>(skillName, "clipSize"), originalMaxClip.Value);
            vdata.MaxClip1 = clipSize;
            vdata.PrimaryReserveAmmoMax = Math.Max(SkillsInfo.GetValue<int>(skillName, "reserveMagazines") * clipSize, originalReserveMax.Value);
        }

        private static void RestoreRevolverData()
        {
            if (originalMaxClip == null || originalReserveMax == null) return;

            var vdata = GetRevolverData();
            if (vdata != null)
            {
                vdata.MaxClip1 = originalMaxClip.Value;
                vdata.PrimaryReserveAmmoMax = originalReserveMax.Value;
            }

            originalMaxClip = null;
            originalReserveMax = null;
        }

        private static CCSWeaponBaseVData? GetRevolverData()
        {
            var vdata = VirtualFunctions.GetCSWeaponDataFromKeyFunc.Invoke(-1, revolverDefIndex.ToString());
            return vdata == null || vdata.Handle == IntPtr.Zero ? null : vdata;
        }

        private static void FillRevolver(CCSPlayerController player)
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) return;

            foreach (var item in pawn.WeaponServices.MyWeapons)
            {
                if (item == null || !item.IsValid || item.Value == null || !item.Value.IsValid || !IsRevolver(item.Value)) continue;
                var weapon = item.Value.As<CCSWeaponBase>();
                if (weapon.VData == null) continue;

                weapon.Clip1 = weapon.VData.MaxClip1;
                weapon.ReserveAmmo[0] = weapon.VData.PrimaryReserveAmmoMax;
                Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");
                Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
            }
        }

        private static void RemoveRevolver(CCSPlayerController player)
        {
            if (player == null || !player.IsValid) return;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) return;

            bool hasOtherPistol = false;
            foreach (var item in pawn.WeaponServices.MyWeapons)
            {
                if (item == null || !item.IsValid || item.Value == null || !item.Value.IsValid) continue;

                if (IsRevolver(item.Value))
                    SkillUtils.SafeKillEntity<CBasePlayerWeapon>(item.Value.Index);
                else if (IsPistol(item.Value))
                    hasOtherPistol = true;
            }

            if (hasOtherPistol || !player.PawnIsAlive) return;

            // Give back the default pistol that was taken away when the skill was enabled
            uint playerIndex = player.Key();
            Server.NextFrame(() =>
            {
                var pl = PlayerManager.GetController(playerIndex);
                if (pl == null || !pl.IsValid || !pl.PawnIsAlive) return;

                bool isCT = pl.Team == CsTeam.CounterTerrorist;
                string? defaultPistol = ConVar.Find(isCT ? "mp_ct_default_secondary" : "mp_t_default_secondary")?.StringValue;
                if (string.IsNullOrWhiteSpace(defaultPistol))
                    defaultPistol = isCT ? "weapon_hkp2000" : "weapon_glock";
                pl.GiveNamedItem(defaultPistol);
            });
        }

        private static bool IsRevolver(CBasePlayerWeapon weapon)
        {
            return weapon.AttributeManager.Item.ItemDefinitionIndex == revolverDefIndex;
        }

        private static bool IsPistol(CBasePlayerWeapon weapon)
        {
            var vdata = weapon.As<CCSWeaponBase>().VData;
            if (vdata != null)
                return vdata.GearSlot == gear_slot_t.GEAR_SLOT_PISTOL;
            return WeaponPool.IsPistol(weapon.DesignerName);
        }

        public class SkillConfig(Skills skill = skillName, bool active = true, string color = "#c88a3d", CsTeam onlyTeam = CsTeam.None, bool disableOnFreezeTime = false, bool needsTeammates = false, string requiredPermission = "", float? hudDuration = null, float? descriptionHudDuration = null, int maxPerServer = -1, Rarity rarity = Rarity.Common, float fireDelay = .35f, int clipSize = 18, int reserveMagazines = 6) : SkillsInfo.DefaultSkillInfo(skill, active, color, onlyTeam, disableOnFreezeTime, needsTeammates, requiredPermission, hudDuration, descriptionHudDuration, maxPerServer, rarity)
        {
            public float FireDelay { get; set; } = fireDelay;
            public int ClipSize { get; set; } = clipSize;
            public int ReserveMagazines { get; set; } = reserveMagazines;
        }
    }
}
