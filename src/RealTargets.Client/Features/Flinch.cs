using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using RealTargets.Hits;
using RootMotion.FinalIK;
using SPT.Reflection.Patching;
using UnityEngine;

namespace RealTargets.Features
{
    /// <summary>
    /// M2 living flinch. EFT ships a FinalIK HitReaction rig on every player, but offline it is
    /// created disabled (Player init and PlayerPoolObject set HitReaction.enabled = false; only
    /// online ObservedPlayerView enables it), so bots barely react to hits. This feature enables
    /// the rig on a bot when it is first hit and drives it once per volley with a force scaled by
    /// the hit model: FinalIK's offsets are linear in the force vector, and EFT's own call
    /// (Player.ShotReactions) always passes a unit vector. EFT's per-collider call is suppressed
    /// while this feature is on, so a shotgun shell is one reaction, not eight. SAIN also
    /// prefixes HitReaction.Hit (skipping it by default), so we drive the hit points directly.
    /// </summary>
    internal static class Flinch
    {
        private static readonly AccessTools.FieldRef<Player, float> TurnOffFbbikAt =
            AccessTools.FieldRefAccess<Player, float>("_turnOffFbbikAt");
        private static readonly AccessTools.FieldRef<Player, FullBodyBipedIK> Fbbik =
            AccessTools.FieldRefAccess<Player, FullBodyBipedIK>("_fbbik");

        internal static bool Enabled => Plugin.FlinchEnabled.Value;

        internal static void Enable()
        {
            new SuppressNativeHitPatch().Enable();
            // Subscribed before the overlay so FlinchStrength is set when the overlay formats.
            HitCollector.PublishedVolley += OnVolley;
        }

        private static void OnVolley(HitVolley volley)
        {
            if (!Enabled || volley.Status != HitStatus.Alive)
            {
                return;
            }

            var target = volley.Target;
            if (target == null || target.IsYourPlayer || target.HitReaction == null)
            {
                return;
            }

            // Far bots run a simplified skeleton driven by the animator, not FBBIK.
            if (target.UsedSimplifiedSkeleton || target.PointOfView != EPointOfView.ThirdPerson)
            {
                return;
            }

            float strength = Strength(volley);
            if (strength <= 0f)
            {
                return;
            }

            var dominant = volley.Dominant;
            var entry = dominant.Entry;
            Vector3 direction = dominant.Direction.sqrMagnitude > 0.0001f ? dominant.Direction.normalized : -target.Transform.forward;

            // Same preparation EFT does before its own hit reaction: run the full FBBIK solve.
            var fbbik = Fbbik(target);
            if (fbbik == null)
            {
                return;
            }
            TurnOffFbbikAt(target) = Time.time + Plugin.FlinchHoldSeconds.Value;
            fbbik.solver.Quick = false;

            target.HitReaction.enabled = true;

            if (DriveHitPoints(target.HitReaction, entry.Collider, entry.BodyPart, direction * strength, entry.Point))
            {
                volley.FlinchStrength = strength;
            }
        }

        /// <summary>
        /// Same selection as HitReaction.Hit (decompiled): every effector and bone hit point whose
        /// bodyParts or colliderTypes match gets Hit(force, point). Called on the hit points
        /// directly because SAIN's PlayerHitReactionDisablePatch skips HitReaction.Hit itself
        /// while its HIT_REACTION_TOGGLE is on (default), which blocked the 0.3.0 flinch.
        /// </summary>
        private static bool DriveHitPoints(HitReaction reaction, EBodyPartColliderType collider, EBodyPart part, Vector3 force, Vector3 point)
        {
            if (reaction.ik == null)
            {
                return false;
            }

            bool any = false;
            if (reaction.effectorHitPoints != null)
            {
                foreach (var hitPoint in reaction.effectorHitPoints)
                {
                    if (Matches(hitPoint, collider, part))
                    {
                        hitPoint.Hit(force, point);
                        any = true;
                    }
                }
            }

            if (reaction.boneHitPoints != null)
            {
                foreach (var hitPoint in reaction.boneHitPoints)
                {
                    if (Matches(hitPoint, collider, part))
                    {
                        hitPoint.Hit(force, point);
                        any = true;
                    }
                }
            }

            return any;
        }

        private static bool Matches(HitReaction.HitPoint hitPoint, EBodyPartColliderType collider, EBodyPart part)
        {
            if (hitPoint.bodyParts != null && Array.IndexOf(hitPoint.bodyParts, part) >= 0)
            {
                return true;
            }

            return hitPoint.colliderTypes != null && Array.IndexOf(hitPoint.colliderTypes, collider) >= 0;
        }

        /// <summary>
        /// Force multiplier relative to EFT's unit force: sqrt of effective energy against the
        /// reference energy, times the global strength, clamped. sqrt keeps a .308 (~3300 J)
        /// clearly above a 9 mm (~500 J) without the rifle looking absurd.
        /// </summary>
        internal static float Strength(HitVolley volley)
        {
            float energy = volley.EffectiveEnergy;
            if (energy <= 0f)
            {
                // No ballistics (unknown shot): fall back to EFT's own unit force.
                return Plugin.FlinchStrength.Value;
            }

            float scaled = Plugin.FlinchStrength.Value * Mathf.Sqrt(energy / Plugin.ReferenceEnergy.Value);
            return Mathf.Clamp(scaled, Plugin.FlinchMin.Value, Plugin.FlinchMax.Value);
        }

        /// <summary>Skips EFT's own per-collider HitReaction.Hit while the flinch feature is on.</summary>
        private class SuppressNativeHitPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() =>
                AccessTools.Method(typeof(HitReaction), nameof(HitReaction.Hit),
                    new[] { typeof(EBodyPartColliderType), typeof(EBodyPart), typeof(Vector3), typeof(Vector3), typeof(bool) });

            [PatchPrefix]
            private static bool Prefix() => !Enabled;
        }
    }
}
