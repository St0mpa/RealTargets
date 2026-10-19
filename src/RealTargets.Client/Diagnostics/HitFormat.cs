using System.Collections.Generic;
using System.Text;
using EFT;
using RealTargets.Hits;

namespace RealTargets.Diagnostics
{
    /// <summary>Shared text for hits: plain log lines, rich-text overlay rows.</summary>
    internal static class HitFormat
    {
        private const string Grey = "#9a9a9a";
        private const string Flesh = "#ff6b5b";
        private const string Armour = "#7fb2ff";
        private const string Name = "#ffd27a";
        private const string Heavy = "#ffa94d";

        internal static string Status(HitStatus status) => status switch
        {
            HitStatus.Killed => "KILLED",
            HitStatus.Corpse => "corpse",
            _ => "alive",
        };

        private static string SeverityText(HitSeverity severity) => severity switch
        {
            HitSeverity.Blocked => $"<color={Armour}>BLOCKED</color>",
            HitSeverity.Heavy => $"<color={Heavy}><b>HEAVY</b></color>",
            HitSeverity.Massive => $"<color={Flesh}><b>MASSIVE</b></color>",
            _ => "light",
        };

        internal static string Plate(HitSegment s) =>
            s.HasPlate ? s.Plate.ToString().Replace("Plate_", "") : null;

        private static string ShortId(string id) =>
            string.IsNullOrEmpty(id) ? "?" : id.Length > 8 ? id.Substring(id.Length - 8) : id;

        internal static string LogLine(HitEvent hit)
        {
            var sb = new StringBuilder(260);
            sb.Append($"[{hit.WallTime:HH:mm:ss.fff}] Hit {hit.TargetName} ai={hit.IsAI} weapon={hit.WeaponName} ")
              .Append($"shot={ShortId(hit.SourceId)}#{hit.FragmentIndex} type={hit.DamageType} dist={hit.Distance:F1}m ");

            if (hit.HasBallistics)
            {
                sb.Append($"ammo={hit.AmmoName} cal={hit.Caliber} m={hit.BulletMassGram:F2}g ")
                  .Append($"v={hit.ImpactSpeed:F0}/{hit.MuzzleSpeed:F0}m/s{(hit.IsFragment ? " frag" : "")} E={hit.ImpactEnergy:F0}J Eeff={hit.EffectiveEnergy:F0}J ");
            }

#if !PUBLIC
            var dummy = Lab.TestLab.Find(hit.Target);
            if (dummy != null)
            {
                sb.Append($"lab=\"{dummy.Label}\" ");
            }
#endif

            sb.Append($"status={Status(hit.Status)} segments={hit.Segments.Count}:");

            for (int i = 0; i < hit.Segments.Count; i++)
            {
                var s = hit.Segments[i];
                sb.Append(i == 0 ? " " : " | ")
                  .Append($"{s.BodyPart}/{s.Collider}");
                if (s.HasPlate)
                {
                    sb.Append($" plate={Plate(s)}");
                }
                sb.Append($" dmg={s.DamageIn:F1}->{s.DamageOut:F1} pen={s.PenetrationIn:F1} penetrated={s.Penetrated}");
                if (s.Armour.Any)
                {
                    sb.Append($" armour=\"{s.Armour.Name}\" cl{s.Armour.Class} {s.Armour.Outcome.ToString().ToLowerInvariant()}");
                }
                if (s.ImpactSpeed > 0f && i > 0)
                {
                    sb.Append($" v={s.ImpactSpeed:F0}");
                }
                if (hit.HealthAfter.TryGetValue(s.BodyPart, out var hp))
                {
                    sb.Append($" hp={hp.current:F0}/{hp.maximum:F0}");
                }
            }

            // The way through flesh: "legs=2: LeftForearm->LeftForearm | RibcageUp->SpineTop (inferred)".
            if (hit.Legs.Count > 0)
            {
                sb.Append($" legs={hit.Legs.Count}:");
                for (int i = 0; i < hit.Legs.Count; i++)
                {
                    var l = hit.Legs[i];
                    sb.Append(i == 0 ? " " : " | ").Append(hit.Segments[l.EntrySegment].Collider);
                    if (l.FromInside) sb.Append(" (from inside)");
                    sb.Append(l.Exited ? $"->{l.ExitCollider}{(l.InferredExit ? " (inferred)" : "")}" : "->stayed in");
                }
            }
            if (hit.ContinuesFrom != null) sb.Append(" continues=earlier frame");

            return sb.ToString();
        }

        internal static string OverlayText(HitVolley volley)
        {
            var first = volley.First;
            var sb = new StringBuilder(400);
            string who = first.IsLocalPlayer ? "YOU" : first.IsAI ? "bot" : "player";
            var status = volley.Status;
            string statusColor = status == HitStatus.Killed ? Flesh : status == HitStatus.Corpse ? Grey : "#ffffff";

#if !PUBLIC
            var dummy = Lab.TestLab.Find(first.Target);
            if (dummy != null)
            {
                who = dummy.Label;
            }
#endif
            sb.Append($"<b>{first.WallTime:HH:mm:ss}</b>  <color={Name}>{first.TargetName}</color> [{who}]  {first.WeaponName}");
            if (!string.IsNullOrEmpty(first.AmmoName))
            {
                sb.Append($" <color={Grey}>| {first.AmmoName}</color>");
            }
            sb.Append($"  {first.Distance:F1} m  <color={statusColor}><b>{Status(status)}</b></color>");

            AppendModelLine(sb, volley);

            if (volley.Count == 1)
            {
                foreach (var s in first.Segments)
                {
                    sb.Append("\n    ").Append($"{s.BodyPart}/{s.Collider}");
                    AppendPlate(sb, s.HasPlate ? Plate(s) : null);
                    sb.Append($"   pen <b>{s.PenetrationIn:F1}</b>   dmg <b>{s.DamageIn:F1}</b>");
                    AppendDamageOut(sb, first.IsLocalPlayer, s.DamageIn, s.DamageOut, partAlreadyHit: s.DamageIn <= 0f);
                    AppendHp(sb, first, s.BodyPart);
                }
                return sb.ToString();
            }

            // Several projectiles from one cartridge (shotgun pellets, bullet or grenade fragments).
            sb.Append($"\n    <b>{volley.Count} projectiles</b>   dmg <b>{volley.DamageIn:F1}</b>");
            AppendDamageOut(sb, first.IsLocalPlayer, volley.DamageIn, volley.DamageOut, partAlreadyHit: false);

            foreach (var group in GroupByCollider(volley))
            {
                var s = group.sample;
                sb.Append("\n    ").Append($"{s.BodyPart}/{s.Collider} x{group.count}");
                AppendPlate(sb, s.HasPlate ? Plate(s) : null);
                sb.Append($"   pen <b>{group.penMin:F1}-{group.penMax:F1}</b>   dmg <b>{group.dmgIn:F1}</b>");
                AppendDamageOut(sb, first.IsLocalPlayer, group.dmgIn, group.dmgOut, partAlreadyHit: group.dmgIn <= 0f);
                AppendHp(sb, group.owner, s.BodyPart);
            }

            return sb.ToString();
        }

        /// <summary>"impact 912 m/s  1660 J (eff 830 J)  HEAVY  flinch x1.4"</summary>
        private static void AppendModelLine(StringBuilder sb, HitVolley volley)
        {
            var first = volley.First;
            if (!first.HasBallistics)
            {
                return;
            }

            sb.Append("\n    ");
            if (volley.Count == 1)
            {
                sb.Append($"impact <b>{first.ImpactSpeed:F0}</b> m/s  <b>{first.ImpactEnergy:F0}</b> J");
            }
            else
            {
                sb.Append($"~<b>{AverageSpeed(volley):F0}</b> m/s  total <b>{volley.ImpactEnergy:F0}</b> J");
            }

            if (volley.EffectiveEnergy < volley.ImpactEnergy - 1f)
            {
                sb.Append($" <color={Armour}>(eff {volley.EffectiveEnergy:F0} J)</color>");
            }

            sb.Append($"  {SeverityText(volley.Severity)}");

            if (volley.FlinchStrength > 0f)
            {
                sb.Append($"  flinch <b>x{volley.FlinchStrength:F1}</b>");
            }
        }

        private static float AverageSpeed(HitVolley volley)
        {
            float total = 0f;
            int n = 0;
            foreach (var p in volley.Projectiles)
            {
                if (p.ImpactSpeed > 0f)
                {
                    total += p.ImpactSpeed;
                    n++;
                }
            }
            return n > 0 ? total / n : 0f;
        }

        private static void AppendPlate(StringBuilder sb, string plate)
        {
            if (plate != null)
            {
                sb.Append($" <color={Armour}>[{plate}]</color>");
            }
        }

        private static void AppendDamageOut(StringBuilder sb, bool localPlayer, float dmgIn, float dmgOut, bool partAlreadyHit)
        {
            if (localPlayer)
            {
                // The after-value for the local player was 0 in some raids; don't present it as fact.
                return;
            }

            if (partAlreadyHit)
            {
                sb.Append($" <color={Grey}>(part already hit)</color>");
                return;
            }

            float absorbed = dmgIn - dmgOut;
            string color = dmgOut > 0.05f ? Flesh : Grey;
            sb.Append($" -> <color={color}><b>{dmgOut:F1}</b></color>");
            if (absorbed > 0.05f)
            {
                sb.Append($" <color={Armour}>(armour -{absorbed:F1})</color>");
            }
        }

        private static void AppendHp(StringBuilder sb, HitEvent hit, EBodyPart part)
        {
            if (hit.HealthAfter.TryGetValue(part, out var hp))
            {
                sb.Append($"   <color={Grey}>HP {hp.current:F0}/{hp.maximum:F0}</color>");
            }
        }

        private static IEnumerable<(HitSegment sample, HitEvent owner, int count, float dmgIn, float dmgOut, float penMin, float penMax)>
            GroupByCollider(HitVolley volley)
        {
            var order = new List<(EBodyPartColliderType, EArmorPlateCollider)>();
            var groups = new Dictionary<(EBodyPartColliderType, EArmorPlateCollider),
                (HitSegment sample, HitEvent owner, int count, float dmgIn, float dmgOut, float penMin, float penMax)>();

            foreach (var hit in volley.Projectiles)
            {
                foreach (var s in hit.Segments)
                {
                    var key = (s.Collider, s.Plate);
                    if (!groups.TryGetValue(key, out var g))
                    {
                        order.Add(key);
                        g = (s, hit, 0, 0f, 0f, float.MaxValue, float.MinValue);
                    }

                    g.count++;
                    g.dmgIn += s.DamageIn;
                    g.dmgOut += s.DamageOut;
                    if (s.PenetrationIn < g.penMin) g.penMin = s.PenetrationIn;
                    if (s.PenetrationIn > g.penMax) g.penMax = s.PenetrationIn;
                    g.owner = hit; // latest projectile: its HP reading is the freshest
                    groups[key] = g;
                }
            }

            foreach (var key in order)
            {
                yield return groups[key];
            }
        }
    }
}
