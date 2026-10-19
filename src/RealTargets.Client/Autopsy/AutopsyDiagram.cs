using System.Collections.Generic;
using EFT;
using RealTargets.Hits;
using UnityEngine;

namespace RealTargets.Autopsy
{
    /// <summary>
    /// The canonical standing body the diagram is drawn from, metres: x = body right, y = up from the pelvis.
    /// Hit points are mapped onto it at hit time (AutopsyRecords.Map), so a crouched or ragdolled body still
    /// reads like a chart.
    /// </summary>
    internal static class CanonicalBody
    {
        internal const float NeckY = 0.56f;
        internal const float Bottom = -0.93f;
        internal const float Top = 0.81f;

        internal static readonly Vector2 HeadCentre = new Vector2(0f, 0.70f);
        internal const float HeadRadius = 0.105f;
        internal static readonly Vector2 NeckLow = new Vector2(0f, 0.53f), NeckHigh = new Vector2(0f, 0.62f);
        internal const float NeckRadius = 0.048f;
        internal static readonly Vector2 ChestCentre = new Vector2(0f, 0.37f), ChestHalf = new Vector2(0.155f, 0.17f);
        internal static readonly Vector2 StomachCentre = new Vector2(0f, 0.07f), StomachHalf = new Vector2(0.14f, 0.125f);

        private static readonly Vector2 Shoulder = new Vector2(0.20f, 0.49f), Elbow = new Vector2(0.25f, 0.21f), Wrist = new Vector2(0.28f, -0.03f);
        private static readonly Vector2 Hand = new Vector2(0.29f, -0.09f);
        private static readonly Vector2 Hip = new Vector2(0.085f, -0.04f), Knee = new Vector2(0.105f, -0.49f), Ankle = new Vector2(0.115f, -0.87f);
        private static readonly Vector2 Toe = new Vector2(0.125f, -0.92f);
        private const float UpperArmR = 0.048f, ForearmR = 0.04f, HandR = 0.038f, ThighR = 0.072f, CalfR = 0.052f, FootR = 0.036f;

        private static Vector2 Mirror(Vector2 v, bool left) => left ? new Vector2(-v.x, v.y) : v;

        internal static bool TryLimb(EBodyPartColliderType collider, out Vector2 a, out Vector2 b, out float r)
        {
            bool left = collider == EBodyPartColliderType.LeftUpperArm || collider == EBodyPartColliderType.LeftForearm ||
                        collider == EBodyPartColliderType.LeftThigh || collider == EBodyPartColliderType.LeftCalf;
            switch (collider)
            {
                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.RightUpperArm:
                    a = Mirror(Shoulder, left); b = Mirror(Elbow, left); r = UpperArmR; return true;
                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm:
                    a = Mirror(Elbow, left); b = Mirror(Wrist, left); r = ForearmR; return true;
                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.RightThigh:
                    a = Mirror(Hip, left); b = Mirror(Knee, left); r = ThighR; return true;
                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightCalf:
                    a = Mirror(Knee, left); b = Mirror(Ankle, left); r = CalfR; return true;
            }
            a = b = default;
            r = 0f;
            return false;
        }

        internal static Vector2 ClampNeck(Vector2 p) => new Vector2(Mathf.Clamp(p.x, -0.03f, 0.03f), Mathf.Clamp(p.y, NeckLow.y + 0.01f, NeckHigh.y - 0.01f));

        /// <summary>Keeps a mapped point inside its body part's shape (poses and armour plates push it out).</summary>
        internal static Vector2 ClampInto(EBodyPart part, Vector2 p)
        {
            switch (part)
            {
                case EBodyPart.Head:
                {
                    var d = p - HeadCentre;
                    float max = HeadRadius * 0.85f;
                    return d.magnitude > max ? HeadCentre + d.normalized * max : p;
                }
                case EBodyPart.Chest:
                    return new Vector2(Mathf.Clamp(p.x, -ChestHalf.x + 0.015f, ChestHalf.x - 0.015f),
                        Mathf.Clamp(p.y, ChestCentre.y - ChestHalf.y + 0.01f, ChestCentre.y + ChestHalf.y - 0.01f));
                case EBodyPart.Stomach:
                    return new Vector2(Mathf.Clamp(p.x, -StomachHalf.x + 0.015f, StomachHalf.x - 0.015f),
                        Mathf.Clamp(p.y, StomachCentre.y - StomachHalf.y + 0.01f, StomachCentre.y + StomachHalf.y - 0.01f));
                case EBodyPart.LeftArm:
                case EBodyPart.RightArm:
                {
                    bool left = part == EBodyPart.LeftArm;
                    var q1 = Nearest(p, Mirror(Shoulder, left), Mirror(Elbow, left));
                    var q2 = Nearest(p, Mirror(Elbow, left), Mirror(Wrist, left));
                    return (q1 - p).sqrMagnitude < (q2 - p).sqrMagnitude ? q1 : q2;
                }
                case EBodyPart.LeftLeg:
                case EBodyPart.RightLeg:
                {
                    bool left = part == EBodyPart.LeftLeg;
                    var q1 = Nearest(p, Mirror(Hip, left), Mirror(Knee, left));
                    var q2 = Nearest(p, Mirror(Knee, left), Mirror(Ankle, left));
                    return (q1 - p).sqrMagnitude < (q2 - p).sqrMagnitude ? q1 : q2;
                }
            }
            return p;
        }

        private static Vector2 Nearest(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return a + ab * t;
        }

        internal static float Capsule(Vector2 p, Vector2 a, Vector2 b, float r) => (p - Nearest(p, a, b)).magnitude - r;

        internal static float RoundRect(Vector2 p, Vector2 c, Vector2 half, float corner)
        {
            var q = new Vector2(Mathf.Abs(p.x - c.x), Mathf.Abs(p.y - c.y)) - half + new Vector2(corner, corner);
            var outside = new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f));
            return outside.magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - corner;
        }

        /// <summary>Signed distance (m) to the body part's outline, and which part is nearest.</summary>
        internal static float Distance(Vector2 p, out EBodyPart part)
        {
            float best = Circle(p, HeadCentre, HeadRadius);
            part = EBodyPart.Head;
            Take(ref best, ref part, Capsule(p, NeckLow, NeckHigh, NeckRadius), EBodyPart.Head);
            Take(ref best, ref part, RoundRect(p, ChestCentre, ChestHalf, 0.06f), EBodyPart.Chest);
            Take(ref best, ref part, RoundRect(p, StomachCentre, StomachHalf, 0.05f), EBodyPart.Stomach);
            for (int s = 0; s < 2; s++)
            {
                bool left = s == 0;
                var arm = left ? EBodyPart.LeftArm : EBodyPart.RightArm;
                var leg = left ? EBodyPart.LeftLeg : EBodyPart.RightLeg;
                Take(ref best, ref part, Capsule(p, Mirror(Shoulder, left), Mirror(Elbow, left), UpperArmR), arm);
                Take(ref best, ref part, Capsule(p, Mirror(Elbow, left), Mirror(Wrist, left), ForearmR), arm);
                Take(ref best, ref part, Circle(p, Mirror(Hand, left), HandR), arm);
                Take(ref best, ref part, Capsule(p, Mirror(Hip, left), Mirror(Knee, left), ThighR), leg);
                Take(ref best, ref part, Capsule(p, Mirror(Knee, left), Mirror(Ankle, left), CalfR), leg);
                Take(ref best, ref part, Capsule(p, Mirror(Ankle, left), Mirror(Toe, left), FootR), leg);
            }
            return best;
        }

        private static float Circle(Vector2 p, Vector2 c, float r) => (p - c).magnitude - r;

        private static void Take(ref float best, ref EBodyPart part, float d, EBodyPart candidate)
        {
            if (d < best)
            {
                best = d;
                part = candidate;
            }
        }
    }

    /// <summary>
    /// Draws the autopsy body chart into a texture: front and back views side by side, each body part filled
    /// by the health it had at death (EFT's own scheme: grey -> red, black = destroyed), entry dots, exit
    /// rings and the line between them, numbered like the shot list (the numbers are UI labels on top).
    /// </summary>
    internal sealed class AutopsyDiagram
    {
        internal const int Width = 300;
        internal const int Height = 310;
        private const float Scale = 160f;
        private const float Margin = 22f;
        private static readonly float[] ViewCentre = { 75f, 225f };

        private static readonly Color32 Healthy = new Color32(70, 74, 72, 255);
        private static readonly Color32 Hurt = new Color32(150, 25, 20, 255);
        private static readonly Color32 Destroyed = new Color32(16, 16, 16, 255);
        private static readonly Color32 Outline = new Color32(125, 130, 127, 255);
        private static readonly Color32 DestroyedOutline = new Color32(175, 32, 26, 255);
        internal static readonly Color32 Yours = new Color32(255, 196, 64, 255);
        internal static readonly Color32 Others = new Color32(140, 190, 255, 255);
        private static readonly Color32 Kill = new Color32(225, 40, 30, 255);

        public readonly Texture2D Texture;
        private readonly Color32[] _base = new Color32[Width * Height];
        private readonly Color32[] _pixels = new Color32[Width * Height];
        private BodyRecord _baseFor;
        private int _baseVersion = -1;

        public AutopsyDiagram()
        {
            Texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "RealTargets.AutopsyDiagram",
            };
        }

        /// <summary>Pixel (texture space, y up) of a diagram mark on its own view.</summary>
        internal static Vector2 Pixel(Vector2 body, bool back)
        {
            float sign = back ? 1f : -1f; // front view: the body's right is on the viewer's left
            return new Vector2(ViewCentre[back ? 1 : 0] + sign * body.x * Scale, Margin + (body.y - CanonicalBody.Bottom) * Scale);
        }

        internal static float ViewCentreX(bool back) => ViewCentre[back ? 1 : 0];

        /// <summary>Redraws: the silhouette is cached per record version, the marks are drawn fresh (hover).</summary>
        public void Draw(BodyRecord record, List<AutopsyShot> shots, int highlight)
        {
            if (record != _baseFor || record.Version != _baseVersion)
            {
                DrawBase(record);
                _baseFor = record;
                _baseVersion = record.Version;
            }
            System.Array.Copy(_base, _pixels, _pixels.Length);

            // Others first, the highlighted shot last (on top).
            foreach (var shot in shots)
            {
                if (shot.Number != highlight) DrawShot(shot, highlight == 0 ? 1f : 0.35f, false);
            }
            foreach (var shot in shots)
            {
                if (shot.Number == highlight) DrawShot(shot, 1f, true);
            }

            Texture.SetPixels32(_pixels);
            Texture.Apply(false);
        }

        private void DrawBase(BodyRecord record)
        {
            System.Array.Clear(_base, 0, _base.Length);
            float px = 1f / Scale;
            for (int view = 0; view < 2; view++)
            {
                bool back = view == 1;
                float sign = back ? 1f : -1f;
                int x0 = Mathf.FloorToInt(ViewCentre[view] - 70f), x1 = Mathf.CeilToInt(ViewCentre[view] + 70f);
                for (int y = 0; y < Height; y++)
                {
                    float by = (y - Margin) / Scale + CanonicalBody.Bottom;
                    if (by < CanonicalBody.Bottom - 0.02f || by > CanonicalBody.Top + 0.02f) continue;
                    for (int x = Mathf.Max(0, x0); x < Mathf.Min(Width, x1); x++)
                    {
                        float bx = (x - ViewCentre[view]) / (sign * Scale);
                        float d = CanonicalBody.Distance(new Vector2(bx, by), out var part);
                        if (d > 1.2f * px) continue;

                        bool destroyed = false;
                        float fraction = 1f;
                        if (record != null && record.HealthAtDeath.TryGetValue(part, out var hp))
                        {
                            destroyed = hp.destroyed;
                            fraction = hp.maximum > 0f ? Mathf.Clamp01(hp.current / hp.maximum) : 1f;
                        }

                        Color32 c;
                        if (d > -1.2f * px)
                        {
                            c = destroyed ? DestroyedOutline : Outline;
                            float a = Mathf.Clamp01(1f - Mathf.Abs(d) / (1.2f * px));
                            c.a = (byte)(255 * Mathf.Max(a, d < 0f ? 1f : 0f));
                        }
                        else
                        {
                            c = destroyed ? Destroyed : Color32.Lerp(Healthy, Hurt, 1f - fraction);
                        }
                        _base[y * Width + x] = c;
                    }
                }
            }
        }

        private void DrawShot(AutopsyShot shot, float alpha, bool highlighted)
        {
            var colour = shot.ByYou ? Yours : Others;
            float size = highlighted ? 1.35f : 1f;
            foreach (var p in shot.Projectiles)
            {
                if (!p.Entry.Valid) continue;
                // The path on both views (a front-to-back shot crosses the body), stronger on the entry's view.
                foreach (var exit in p.Exits)
                {
                    if (!exit.Valid) continue;
                    for (int view = 0; view < 2; view++)
                    {
                        bool back = view == 1;
                        float strength = back == p.Entry.Back ? 0.75f : 0.4f;
                        Line(Pixel(p.Entry.Point, back), Pixel(exit.Point, back), highlighted ? 1.6f : 1.1f, colour, alpha * strength);
                    }
                    Ring(Pixel(exit.Point, exit.Back), 4.5f * size, 1.4f, colour, alpha);
                }

                var at = Pixel(p.Entry.Point, p.Entry.Back);
                bool stopped = StoppedByArmour(p.Hit);
                if (stopped)
                {
                    Ring(at, 3.2f * size, 1.3f, colour, alpha);
                }
                else
                {
                    Disc(at, 3.4f * size, colour, alpha);
                }
                if (p.Hit.Status == HitStatus.Killed)
                {
                    Ring(at, 6.5f * size, 1.4f, Kill, alpha);
                }
                if (highlighted)
                {
                    Ring(at, 5f * size, 1f, new Color32(255, 255, 255, 255), alpha);
                }
            }
        }

        /// <summary>No segment reached flesh: armour stopped or deflected the projectile.</summary>
        /// <summary>
        /// Armour stopped or deflected the round before it wounded anything. Release check 4: an M855 through a forearm
        /// into the Korund-VM front plate read "stopped by armour, no wound" with the forearm shot through and bleeding:
        /// armour further along the path counts only in <see cref="StoppedLater"/>.
        /// </summary>
        internal static bool StoppedByArmour(HitEvent hit)
        {
            foreach (var s in hit.Segments)
            {
                if (Stops(s)) return true;
                if (!s.HasPlate && s.DamageOut > 0.5f) return false;
            }
            return false;
        }

        /// <summary>The armour that stopped the round after it had gone through flesh (forearm -> plate), else null.</summary>
        internal static HitSegment? StoppedLater(HitEvent hit)
        {
            bool flesh = false;
            foreach (var s in hit.Segments)
            {
                if (Stops(s)) return flesh ? s : (HitSegment?)null;
                if (!s.HasPlate && s.DamageOut > 0.5f) flesh = true;
            }
            return null;
        }

        private static bool Stops(HitSegment s) => s.Armour.Outcome == ArmourOutcome.Stopped || s.Armour.Outcome == ArmourOutcome.Deflected;

        private void Blend(int x, int y, Color32 c, float a)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height || a <= 0.003f) return;
            int i = y * Width + x;
            var dst = _pixels[i];
            float sa = Mathf.Clamp01(a);
            float da = dst.a / 255f;
            float oa = sa + da * (1f - sa);
            if (oa <= 0f) return;
            _pixels[i] = new Color32(
                (byte)((c.r * sa + dst.r * da * (1f - sa)) / oa),
                (byte)((c.g * sa + dst.g * da * (1f - sa)) / oa),
                (byte)((c.b * sa + dst.b * da * (1f - sa)) / oa),
                (byte)(oa * 255f));
        }

        private void Disc(Vector2 c, float r, Color32 colour, float alpha)
        {
            int x0 = Mathf.FloorToInt(c.x - r - 1), x1 = Mathf.CeilToInt(c.x + r + 1);
            int y0 = Mathf.FloorToInt(c.y - r - 1), y1 = Mathf.CeilToInt(c.y + r + 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float d = new Vector2(x - c.x, y - c.y).magnitude - r;
                    Blend(x, y, colour, alpha * Mathf.Clamp01(0.5f - d));
                }
            }
        }

        private void Ring(Vector2 c, float r, float width, Color32 colour, float alpha)
        {
            int x0 = Mathf.FloorToInt(c.x - r - width), x1 = Mathf.CeilToInt(c.x + r + width);
            int y0 = Mathf.FloorToInt(c.y - r - width), y1 = Mathf.CeilToInt(c.y + r + width);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float d = Mathf.Abs(new Vector2(x - c.x, y - c.y).magnitude - r) - width * 0.5f;
                    Blend(x, y, colour, alpha * Mathf.Clamp01(0.5f - d));
                }
            }
        }

        private void Line(Vector2 a, Vector2 b, float width, Color32 colour, float alpha)
        {
            int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) - width), x1 = Mathf.CeilToInt(Mathf.Max(a.x, b.x) + width);
            int y0 = Mathf.FloorToInt(Mathf.Min(a.y, b.y) - width), y1 = Mathf.CeilToInt(Mathf.Max(a.y, b.y) + width);
            var ab = b - a;
            float len2 = Mathf.Max(ab.sqrMagnitude, 1e-4f);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x, y);
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                    float d = (p - (a + ab * t)).magnitude - width * 0.5f;
                    Blend(x, y, colour, alpha * Mathf.Clamp01(0.5f - d));
                }
            }
        }
    }
}
