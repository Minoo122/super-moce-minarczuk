using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using src.utils;
using System.Drawing;
using static src.jRandomSkills;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace src.player.skills
{
    public class Portal : ISkill
    {
        private const Skills skillName = Skills.Portal;
        private const string teleportSound = "Weapon_Taser.ChargeReady";
        private const int ringSegments = 12;

        private static readonly Color[] portalColors = [Color.FromArgb(255, 255, 140, 0), Color.FromArgb(255, 0, 150, 255)];
        private static readonly string[] portalNames = ["#FF8C00", "#0096FF"];

        private static readonly Dictionary<uint, PortalPair> portals = [];
        // Pawns standing in a portal they arrived through; they are not sent back until they step out
        private static readonly HashSet<(uint Owner, uint Pawn)> insidePortal = [];
        private static readonly List<(uint Owner, uint Pawn)> leftPortal = [];

        public static void LoadSkill()
        {
            SkillUtils.RegisterSkill(skillName, SkillsInfo.GetValue<string>(skillName, "color"));
        }

        public static void NewRound()
        {
            try
            {
                foreach (var pair in portals.Values)
                    DestroyPair(pair);
                portals.Clear();
                insidePortal.Clear();
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[jRandomSkills] Portal.NewRound failed: {ex.Message}");
            }
        }

        public static void EnableSkill(CCSPlayerController player)
        {
            if (player == null || !player.IsValid) return;
            portals[player.Key()] = new PortalPair();
        }

        // Called from the round reset loop, which is not guarded, so nothing here may throw
        public static void DisableSkill(CCSPlayerController player)
        {
            try
            {
                RemoveOwner(player.Key());
                SkillUtils.ResetPrintHTML(player);
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[jRandomSkills] Portal.DisableSkill failed: {ex.Message}");
            }
        }

        public static void PlayerDisconnect(uint playerIndex)
        {
            RemoveOwner(playerIndex);
        }

        private static void RemoveOwner(uint ownerKey)
        {
            if (portals.Remove(ownerKey, out var pair))
                DestroyPair(pair);
            insidePortal.RemoveWhere(key => key.Owner == ownerKey);
        }

        private static void DestroyPair(PortalPair pair)
        {
            foreach (var portal in pair.Portals)
                if (portal != null)
                    foreach (var beamIndex in portal.Beams)
                        EntityManager.DestroyBeam(beamIndex);
        }

        public static void UseSkill(CCSPlayerController player)
        {
            if (!Instance.IsPlayerValid(player)) return;
            if (!portals.TryGetValue(player.Key(), out var pair)) return;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) return;

            var playerEvent = PlayerManager.GetPlayerFromEvent(player) ?? player;
            if (GetCooldown(pair) > 0) return;

            if (!((PlayerFlags)pawn.Flags).HasFlag(PlayerFlags.FL_ONGROUND))
            {
                playerEvent.PrintToChat($" {ChatColors.Red}" + playerEvent.GetTranslation("portal_ground_info"));
                return;
            }

            Vector origin = new(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z);
            int slot = pair.NextSlot;

            var other = pair.Portals[1 - slot];
            float minDistance = SkillsInfo.GetValue<float>(skillName, "triggerRadius") * 3;
            if (other != null && SkillUtils.GetDistance(origin, other.Origin) < minDistance)
            {
                playerEvent.PrintToChat($" {ChatColors.Red}" + playerEvent.GetTranslation("portal_too_close_info"));
                return;
            }

            if (EntityManager.OverBudget()) return;

            var old = pair.Portals[slot];
            if (old != null)
                foreach (var beamIndex in old.Beams)
                    EntityManager.DestroyBeam(beamIndex);

            pair.Portals[slot] = CreatePortal(player.Key(), origin, pawn.EyeAngles.Y, portalColors[slot]);
            pair.NextSlot = 1 - slot;
            pair.LastPlaced = DateTime.Now;
            // Whoever stands in the moved portal would otherwise be teleported the moment it appears
            insidePortal.RemoveWhere(key => key.Owner == player.Key());
            MarkPawnsInside(player.Key(), pair);

            playerEvent.PrintToChat($" {ChatColors.Green}" + playerEvent.GetTranslation("portal_placed_info", $"{(slot == 0 ? ChatColors.Orange : ChatColors.Blue)}{(slot == 0 ? "A" : "B")}{ChatColors.Green}"));
        }

        private static PortalInfo CreatePortal(uint ownerKey, Vector origin, float yaw, Color color)
        {
            var portal = new PortalInfo { Origin = origin };

            float halfWidth = SkillsInfo.GetValue<float>(skillName, "portalWidth") / 2;
            float halfHeight = SkillsInfo.GetValue<float>(skillName, "portalHeight") / 2;
            float beamWidth = SkillsInfo.GetValue<float>(skillName, "beamWidth");
            Vector right = SkillUtils.GetForwardVector(new QAngle(0, yaw - 90, 0));

            Vector PointOn(float angle) => new(
                origin.X + right.X * MathF.Cos(angle) * halfWidth,
                origin.Y + right.Y * MathF.Cos(angle) * halfWidth,
                origin.Z + halfHeight + MathF.Sin(angle) * halfHeight);

            float step = MathF.Tau / ringSegments;
            Vector previous = PointOn(0);
            for (int i = 1; i <= ringSegments; i++)
            {
                Vector next = PointOn(step * i);
                var beam = EntityManager.CreateTrackedBeam(ownerKey, previous, next, color);
                previous = next;
                if (beam == null || !beam.IsValid) continue;

                beam.Width = beamWidth;
                beam.EndWidth = beamWidth;
                SkillUtils.NetworkChanged(beam, "CBeam", "m_fWidth");
                SkillUtils.NetworkChanged(beam, "CBeam", "m_fEndWidth");
                portal.Beams.Add(beam.Index);
            }

            return portal;
        }

        public static void OnTick()
        {
            if (portals.Count == 0) return;

            if (SkillUtils.IsHudFrame())
                foreach (var (ownerKey, pair) in portals)
                {
                    var owner = PlayerManager.GetController(ownerKey);
                    if (owner != null && owner.IsValid)
                        UpdateHUD(owner, pair);
                }

            if (Server.TickCount % 2 != 0) return;

            foreach (var (ownerKey, pair) in portals)
            {
                if (pair.Portals[0] == null || pair.Portals[1] == null) continue;

                var owner = PlayerManager.GetController(ownerKey);
                if (owner == null || !owner.IsValid) continue;

                foreach (var player in PlayerManager.GetTickPlayers())
                {
                    if (!CanUsePortal(owner, player)) continue;

                    var pawn = player.PlayerPawn.Value;
                    if (pawn == null || !pawn.IsValid || pawn.Health <= 0 || pawn.AbsOrigin == null) continue;

                    int portalIndex = GetPortalAt(pair, pawn.AbsOrigin);
                    var key = (ownerKey, pawn.Index);
                    if (portalIndex < 0)
                    {
                        insidePortal.Remove(key);
                        continue;
                    }
                    if (!insidePortal.Add(key)) continue;

                    var destination = pair.Portals[1 - portalIndex]!.Origin;
                    pawn.Teleport(new Vector(destination.X, destination.Y, destination.Z + 2));
                    var playerEvent = PlayerManager.GetPlayerFromEvent(player) ?? player;
                    SkillUtils.EmitSoundToPlayer(playerEvent, teleportSound, SkillsInfo.GetValue<float>(skillName, "soundVolume"));
                }
            }

            // Pawns that died or vanished would otherwise keep their entry forever
            leftPortal.Clear();
            foreach (var key in insidePortal)
            {
                var pawn = Utilities.GetEntityFromIndex<CCSPlayerPawn>((int)key.Pawn);
                if (pawn == null || !pawn.IsValid || pawn.Health <= 0)
                    leftPortal.Add(key);
            }
            foreach (var key in leftPortal)
                insidePortal.Remove(key);
        }

        private static bool CanUsePortal(CCSPlayerController owner, CCSPlayerController player)
        {
            if (player == null || !player.IsValid || !player.PawnIsAlive) return false;
            if (player.Key() == owner.Key()) return true;
            return player.Team == owner.Team
                ? SkillsInfo.GetValue<bool>(skillName, "teammatesCanUse")
                : SkillsInfo.GetValue<bool>(skillName, "enemiesCanUse");
        }

        private static int GetPortalAt(PortalPair pair, Vector position)
        {
            float radius = SkillsInfo.GetValue<float>(skillName, "triggerRadius");
            float height = SkillsInfo.GetValue<float>(skillName, "portalHeight");

            for (int i = 0; i < pair.Portals.Length; i++)
            {
                var origin = pair.Portals[i]!.Origin;
                float dx = position.X - origin.X, dy = position.Y - origin.Y, dz = position.Z - origin.Z;
                if (dx * dx + dy * dy <= radius * radius && dz > -radius && dz < height)
                    return i;
            }
            return -1;
        }

        private static void MarkPawnsInside(uint ownerKey, PortalPair pair)
        {
            if (pair.Portals[0] == null || pair.Portals[1] == null) return;

            foreach (var player in PlayerManager.GetTickPlayers())
            {
                var pawn = player.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) continue;
                if (GetPortalAt(pair, pawn.AbsOrigin) >= 0)
                    insidePortal.Add((ownerKey, pawn.Index));
            }
        }

        private static float GetCooldown(PortalPair pair)
        {
            float time = (float)Math.Ceiling((pair.LastPlaced.AddSeconds(SkillsInfo.GetValue<float>(skillName, "cooldown")) - DateTime.Now).TotalSeconds);
            return Math.Max(time, 0);
        }

        private static void UpdateHUD(CCSPlayerController player, PortalPair pair)
        {
            var playerInfo = PlayerManager.GetPlayer(player);
            if (playerInfo?.Skill != skillName) return;

            float cooldown = GetCooldown(pair);
            if (cooldown > 0)
                playerInfo.PrintHTML = $"{player.GetTranslation("hud_info", $"<font color='#FF0000'>{cooldown}</font>")}";
            else
                playerInfo.PrintHTML = $"{player.GetTranslation("portal_next_info", $"<font color='{portalNames[pair.NextSlot]}'>{(pair.NextSlot == 0 ? "A" : "B")}</font>")}";
        }

        private class PortalInfo
        {
            public required Vector Origin { get; init; }
            public List<uint> Beams { get; } = [];
        }

        private class PortalPair
        {
            public PortalInfo?[] Portals { get; } = new PortalInfo?[2];
            public int NextSlot { get; set; }
            public DateTime LastPlaced { get; set; } = DateTime.MinValue;
        }

        public class SkillConfig(Skills skill = skillName, bool active = true, string color = "#ff8c00", CsTeam onlyTeam = CsTeam.None, bool disableOnFreezeTime = true, bool needsTeammates = false, string requiredPermission = "", float? hudDuration = null, float? descriptionHudDuration = null, int maxPerServer = -1, Rarity rarity = Rarity.Rare, float cooldown = 3f, float triggerRadius = 20f, float portalWidth = 44f, float portalHeight = 76f, float beamWidth = 1.5f, bool teammatesCanUse = true, bool enemiesCanUse = false, float soundVolume = .5f) : SkillsInfo.DefaultSkillInfo(skill, active, color, onlyTeam, disableOnFreezeTime, needsTeammates, requiredPermission, hudDuration, descriptionHudDuration, maxPerServer, rarity)
        {
            public float Cooldown { get; set; } = cooldown;
            public float TriggerRadius { get; set; } = triggerRadius;
            public float PortalWidth { get; set; } = portalWidth;
            public float PortalHeight { get; set; } = portalHeight;
            public float BeamWidth { get; set; } = beamWidth;
            public bool TeammatesCanUse { get; set; } = teammatesCanUse;
            public bool EnemiesCanUse { get; set; } = enemiesCanUse;
            public float SoundVolume { get; set; } = soundVolume;
        }
    }
}
