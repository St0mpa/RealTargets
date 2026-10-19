using System;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace RealTargets.Features
{
    /// <summary>
    /// Grenade blasts (0.8.0; injuries removed in 0.10.0, EFT decides damage again): after each
    /// explosion the peak overpressure at every corpse's chest (Kinney-Graham, TNT equivalent per
    /// grenade, x1.8 for a ground burst, x0.35 behind cover) throws corpses within 6 m away from it.
    /// RGD-5: ~300 kPa at 1 m, ~60 at 2 m, ~30 at 3 m. Phase 2 (limb loss) will hook in here.
    /// </summary>
    internal static class Blast
    {
        internal static void Enable()
        {
            if (ExplosionPatch.Target() != null)
            {
                new ExplosionPatch().Enable();
            }
            else
            {
                Plugin.Log.LogWarning("Blast: ExplosionSharedMethods.Explosion not found; no blast pushes.");
            }
        }

        private class ExplosionPatch : ModulePatch
        {
            internal static MethodBase Target() => AccessTools.FirstMethod(typeof(ExplosionSharedMethods), m =>
                m.Name == "Explosion" && m.GetParameters().Length >= 5 && m.GetParameters()[0].ParameterType == typeof(IExplosiveItem));

            protected override MethodBase GetTargetMethod() => Target();

            [PatchPrefix]
            private static void Prefix(IExplosiveItem grenadeItem, Vector3 explosionPosition)
            {
                try
                {
                    Before.Clear();
                    if (Plugin.ModEnabled.Value && Plugin.AutopsyEnabled.Value && grenadeItem != null && !grenadeItem.IsDummy)
                    {
                        Snapshot(grenadeItem, explosionPosition);
                    }
                }
                catch (Exception ex)
                {
                    Before.Clear();
                    Plugin.Log.LogWarning($"Blast: health snapshot failed: {ex.Message}");
                }
            }

            [PatchPostfix]
            private static void Postfix(IExplosiveItem grenadeItem, Vector3 explosionPosition, string playerProfileIDWhoThrew)
            {
                try
                {
                    if (Before.Count > 0) RecordBlastDamage(grenadeItem, explosionPosition, playerProfileIDWhoThrew);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Blast: autopsy record failed: {ex.Message}");
                }
                finally
                {
                    Before.Clear();
                }
                try
                {
                    if (Plugin.ModEnabled.Value && Plugin.BlastEnabled.Value && grenadeItem != null && !grenadeItem.IsDummy)
                    {
                        Apply(grenadeItem, explosionPosition);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Blast: {ex.Message}");
                }
            }
        }

        // ---- The autopsy's BLAST row (1.1.0, Stompa) ---------------------------------------------------------------
        // EFT's blast damage (ExplosionSharedMethods.AffectPlayersShared -> BodyPartCollider.ApplyEnvironmentalDamage ->
        // Player.ApplyDamageInfo, decompiled 2026-10-18) never goes through ApplyShot, so a bot killed by the wave alone had
        // no row. It is applied synchronously inside Explosion, to living players only; the fragments are only queued
        // there (BallisticsCalculator.Shots, flown next update). So health before vs after Explosion = the blast alone.

        private static readonly EBodyPart[] Parts =
        {
            EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach, EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg,
        };

        private static readonly System.Collections.Generic.List<(Player player, float[] hp)> Before =
            new System.Collections.Generic.List<(Player, float[])>(8);

        private static void Snapshot(IExplosiveItem grenadeItem, Vector3 at)
        {
            if (!Singleton<GameWorld>.Instantiated) return;
            float range = Mathf.Max(grenadeItem.MaxExplosionDistance * 2f, 10f);
            foreach (var player in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (player == null || !player.IsAI || player.IsYourPlayer || player.HealthController == null || !player.HealthController.IsAlive) continue;
                if ((player.Position - at).sqrMagnitude > range * range) continue;
                var hp = new float[Parts.Length];
                for (int i = 0; i < Parts.Length; i++) hp[i] = player.HealthController.GetBodyPartHealth(Parts[i], true).Current;
                Before.Add((player, hp));
            }
        }

        private static void RecordBlastDamage(IExplosiveItem grenadeItem, Vector3 at, string throwerId)
        {
            string name = NameOf(grenadeItem);
            float charge = Charge(name, grenadeItem);
            bool ground = Physics.Raycast(at + Vector3.up * 0.05f, Vector3.down, 0.6f, LayersMaskController.HighPolyWithTerrainMask);
            float w = charge * (ground ? 1.8f : 1f);
            Player thrower = null;
            if (!string.IsNullOrEmpty(throwerId) && Singleton<GameWorld>.Instantiated)
            {
                thrower = Singleton<GameWorld>.Instance.GetEverExistedPlayerByID(throwerId);
            }

            foreach (var (player, before) in Before)
            {
                var health = player != null ? player.HealthController : null;
                if (health == null) continue;
                bool alive = health.IsAlive;
                float total = 0f;
                var damage = new float[Parts.Length];
                for (int i = 0; i < Parts.Length; i++)
                {
                    damage[i] = Mathf.Max(0f, before[i] - health.GetBodyPartHealth(Parts[i], true).Current);
                    total += damage[i];
                }
                if (total < 0.5f) continue;

                var chest = Chest(player);
                float d = Vector3.Distance(chest, at);
                var hit = new Hits.HitEvent
                {
                    Target = player,
                    TargetName = player.Profile?.Nickname ?? "?",
                    IsAI = player.IsAI,
                    IsLocalPlayer = false,
                    IsBlast = true,
                    DamageType = EDamageType.Explosion,
                    Weapon = ItemOf(grenadeItem),
                    WeaponName = name,
                    Origin = at,
                    Direction = d > 1e-3f ? (chest - at) / d : Vector3.down,
                    Distance = d,
                    WallTime = DateTime.Now,
                    WasAlive = true,
                    Status = alive ? Hits.HitStatus.Alive : Hits.HitStatus.Killed,
                    BlastCharge = charge,
                    BlastKpa = Overpressure(w, d) * Cover(at, chest),
                };
                if (thrower != null)
                {
                    // Read off the Player directly: converting it to IPlayer needs the DissonanceVoip reference.
                    var profile = thrower.Profile;
                    hit.ShooterProfileId = thrower.ProfileId;
                    hit.ShooterIsYou = thrower.IsYourPlayer;
                    hit.ShooterName = profile?.Nickname ?? "?";
                    hit.ShooterSide = thrower.Side;
                    if (profile?.Info?.Settings != null) hit.ShooterRole = profile.Info.Settings.Role;
                }

                var colliders = player.PlayerBones != null ? player.PlayerBones.BodyPartCollidersDictionary : null;
                for (int i = 0; i < Parts.Length; i++)
                {
                    if (damage[i] < 0.05f) continue;
                    var collider = MainCollider(Parts[i]);
                    var point = colliders != null && colliders.TryGetValue(collider, out var bpc) && bpc?.Collider != null
                        ? bpc.Collider.bounds.center : chest;
                    hit.Segments.Add(new Hits.HitSegment(Parts[i], collider, (EArmorPlateCollider)0, point, damage[i], damage[i], 0f, false, alive, 0f, hit.Direction));
                    var value = health.GetBodyPartHealth(Parts[i], true);
                    hit.HealthAfter[Parts[i]] = (value.Current, value.Maximum);
                }
                if (Plugin.LogBlood.Value)
                {
                    Plugin.Log.LogInfo($"[Blast] {name} {hit.TargetName}: blast damage {total:F0} HP at {d:F1} m (~{hit.BlastKpa:F0} kPa)" +
                                       $"{(alive ? "" : ", KILLED")}, thrower {(thrower != null ? thrower.Profile?.Nickname : "unknown")}");
                }
                Autopsy.AutopsyRecords.OnBlast(hit);
            }
        }

        private static EBodyPartColliderType MainCollider(EBodyPart part) => part switch
        {
            EBodyPart.Head => EBodyPartColliderType.HeadCommon,
            EBodyPart.Chest => EBodyPartColliderType.RibcageUp,
            EBodyPart.Stomach => EBodyPartColliderType.RibcageLow,
            EBodyPart.LeftArm => EBodyPartColliderType.LeftUpperArm,
            EBodyPart.RightArm => EBodyPartColliderType.RightUpperArm,
            EBodyPart.LeftLeg => EBodyPartColliderType.LeftThigh,
            _ => EBodyPartColliderType.RightThigh,
        };

        /// <summary>
        /// The explosive's item: a hand grenade is one; a 40 mm or VOG round's charge is a component of the round
        /// (`ExplosiveAmmoComponent`). Gore build test: an FN40GL round logged as "explosive" and got the 0.08 kg fallback.
        /// </summary>
        private static Item ItemOf(IExplosiveItem explosive) => explosive as Item ?? (explosive as ItemComponent)?.Item;

        /// <summary>
        /// The explosive's short name. Gore retest (2026-10-18): the FN40GL round still read "explosive": EFT passes the round's
        /// `ExplosiveAmmoComponent`, a `ReadonlyItemComponent` that holds only its template (no Item, decompiled
        /// ItemComponent.Types), so the name comes from the template's localized short name ("M441").
        /// </summary>
        private static string NameOf(IExplosiveItem explosive)
        {
            var item = ItemOf(explosive);
            if (item != null) return Hits.HitCollector.ItemName(item);
            if (explosive is ExplosiveAmmoComponent round && round.Template is ItemTemplate template)
            {
                try
                {
                    string name = template.ShortNameLocalizationKey.Localized();
                    if (!string.IsNullOrWhiteSpace(name) && name != template.ShortNameLocalizationKey) return name;
                }
                catch (Exception)
                {
                    // Localization not ready: fall through to the template's own name.
                }
                if (!string.IsNullOrWhiteSpace(template._name)) return template._name;
            }
            return "explosive";
        }

        /// <summary>A launched round's calibre (`Caliber40x46`, `Caliber40mmRU`, `Caliber30x29`); empty for a hand grenade.</summary>
        private static string CaliberOf(IExplosiveItem explosive) =>
            explosive is ExplosiveAmmoComponent round && round.Template is AmmoTemplate ammo ? ammo.Caliber ?? "" : "";

        /// <summary>TNT-equivalent charge in kg (public figures, rounded).</summary>
        private static float Charge(string name, IExplosiveItem item)
        {
            string n = (name ?? "").ToUpperInvariant();
            string calibre = CaliberOf(item).ToUpperInvariant();
            // 40x46 HE (M406, M441, M381, M433, M386): 32-45 g Comp B. VOG-25 (40 mm RU) ~48 g A-IX-1, VOG-30 (30x29) ~36 g.
            if (calibre.Contains("40X46")) return 0.045f;
            if (calibre.Contains("40MMRU")) return 0.05f;
            if (calibre.Contains("30X29")) return 0.04f;
            if (n.Contains("M67")) return 0.24f;      // 185 g Comp B
            if (n.Contains("RGD")) return 0.11f;      // 110 g TNT
            if (n.Contains("F-1") || n.Contains("F1")) return 0.06f; // 60 g TNT
            if (n.Contains("RGN") || n.Contains("RGO")) return 0.12f;
            if (n.Contains("VOG-25") || n.Contains("VOG25")) return 0.05f;
            if (n.Contains("VOG")) return 0.04f;
            if (n.Contains("40X46") || n.Contains("M406") || n.Contains("M433")) return 0.045f; // 32-45 g Comp B (0.10.5)
            if (n.Contains("V40")) return 0.015f;
            if (n.Contains("ZARYA") || n.Contains("FLASH")) return 0.01f;  // flashbang: ears, not lungs
            if (n.Contains("RDG") || n.Contains("M18") || n.Contains("SMOKE")) return 0f;
            return item.FragmentsCount > 0 ? 0.08f : 0f;
        }

        /// <summary>Peak incident overpressure, kPa (Kinney-Graham), for TNT kg at metres.</summary>
        internal static float Overpressure(float kg, float metres)
        {
            if (kg <= 0f) return 0f;
            float z = Mathf.Max(metres, 0.05f) / Mathf.Pow(kg, 1f / 3f);
            float num = 808f * (1f + Sq(z / 4.5f));
            float den = Mathf.Sqrt((1f + Sq(z / 0.048f)) * (1f + Sq(z / 0.32f)) * (1f + Sq(z / 1.35f)));
            return 101.325f * num / den;
        }

        private static float Sq(float x) => x * x;

        private static void Apply(IExplosiveItem grenadeItem, Vector3 at)
        {
            string name = NameOf(grenadeItem);
            float charge = Charge(name, grenadeItem);
            if (charge <= 0f || !Singleton<GameWorld>.Instantiated)
            {
                return;
            }

            // Near the ground the reflected wave adds up (hemispherical burst).
            bool ground = Physics.Raycast(at + Vector3.up * 0.05f, Vector3.down, 0.6f, LayersMaskController.HighPolyWithTerrainMask);
            float w = charge * (ground ? 1.8f : 1f);

            // Heads and limbs next to the charge (dead bodies only, once EFT's damage is in).
            Gore.Dismemberment.Instance?.OnExplosion(at, charge, name);

            // Corpses in range are thrown.
            foreach (var player in Singleton<GameWorld>.Instance.AllPlayersEverExisted.ToList())
            {
                if (player == null || player.ActiveHealthController == null || player.ActiveHealthController.IsAlive) continue;
                var chest = Chest(player);
                float d = Vector3.Distance(chest, at);
                if (d > 6f) continue;
                float kpa = Overpressure(w, d) * Cover(at, chest);
                if (kpa < 20f) continue;
                Impacts.Instance?.Push(player, chest, Away(at, chest) * Mathf.Min(60f, kpa * 0.12f), corpse: true, "blast");
            }
        }

        private static Vector3 Chest(Player player)
        {
            var bones = player.PlayerBones;
            return bones?.Ribcage?.Original != null ? (bones.Ribcage.Original.position + bones.Pelvis.Original.position) * 0.5f : player.Position + Vector3.up * 1f;
        }

        private static Vector3 Away(Vector3 from, Vector3 to)
        {
            var d = to - from;
            d.y = Mathf.Max(d.y, 0f);
            return (d.normalized + Vector3.up * 0.35f).normalized;
        }

        /// <summary>Solid geometry between blast and body: the wave diffracts but loses most of its peak.</summary>
        private static float Cover(Vector3 from, Vector3 to) =>
            Physics.Linecast(from, to, LayersMaskController.HighPolyWithTerrainMask) ? 0.35f : 1f;
    }
}
