using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using RealTargets.Hits;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Head destruction and limb loss (0.10.2, roadmap G2/G3), on the killing shot or on a corpse
    /// (never on a living bot: EFT's gameplay stays intact). Realistic thresholds (ADR 0008):
    /// - head: buckshot with >= `Head pellets` pellets in the head within `Shotgun range`; a slug or
    ///   other >= 12 mm projectile with >= `Large-calibre energy`; anything with >= `Any-calibre
    ///   energy` (.338 LM, 12.7x108); a grenade within ~0.3 m. A single bullet with >= `Skull burst
    ///   energy` below that bursts the skull open at the exit (0.10.5, BurstSkull); weaker headshots
    ///   only get the big exit spray (ImpactBlood). Handgun calibres (.50 AE) never count as 12 mm;
    /// - limbs: 12.7 mm+ (or 20/30 mm) hits with >= `Heavy-calibre energy`, and grenades within
    ///   ~0.5 m (forearm/calf) or ~0.3 m (upper arm/thigh), scaled by charge.
    /// How (decompiled 2026-10-14): EFT's ragdoll bodies and the skinned meshes share one skeleton,
    /// and rigid head gear (helmet, glasses, face cover) is parented to PlayerBones.Head.Original.
    /// So a part is removed by collapsing its bone (and same-named twist bones) to 1 % scale: every
    /// vertex skinned to it, its hit colliders and the gear under it shrink into the joint, which a
    /// stump cap covers. A severed limb is first cut out of the baked meshes by bone weights and
    /// thrown as its own physics piece; the helmet is cloned (the real model belongs to EFT's pool)
    /// and thrown. Bodies come from EFT's pool too: when a corpse goes away (or the raid ends)
    /// every scale is restored and every stump removed, so a reused body is whole again.
    /// </summary>
    internal sealed class Dismemberment : MonoBehaviour
    {
        private const float Collapsed = 0.01f;
        private const int MaxPieces = 220;

        private sealed class Body
        {
            public Player Player;
            public bool HeadGone;
            public bool HeadBurst;
            public bool HelmetThrown;
            // Rounds into the head of the dead body so far (a shotgun shell counts once) and their energy (0.10.8).
            public int HeadHits;
            public float HeadEnergy;
            public readonly HashSet<string> Cut = new HashSet<string>();
            public readonly List<(Transform bone, Vector3 scale)> Scaled = new List<(Transform, Vector3)>();
            public readonly List<GameObject> Attached = new List<GameObject>();
            // Skins given a copy of their mesh with the burst hole left out (0.10.5); put back on release.
            public readonly List<(SkinnedMeshRenderer renderer, Mesh original, Mesh copy)> Meshes = new List<(SkinnedMeshRenderer, Mesh, Mesh)>();
        }


        /// <summary>The transform was collapsed (severed limb, destroyed head) or hangs below a collapsed bone.</summary>
        internal bool IsRemoved(Player player, Transform t)
        {
            var body = Find(player);
            if (body == null || t == null) return false;
            foreach (var (bone, _) in body.Scaled)
            {
                if (bone != null && (t == bone || t.IsChildOf(bone))) return true;
            }
            return false;
        }

        internal static Dismemberment Instance { get; private set; }
        internal int PieceCount => _pieces.Count;

        private readonly Dictionary<int, Body> _bodies = new Dictionary<int, Body>();
        private readonly Queue<GameObject> _pieces = new Queue<GameObject>();
        private readonly List<int> _release = new List<int>();

        private static readonly EBodyPartColliderType[] Limbs =
        {
            EBodyPartColliderType.LeftForearm, EBodyPartColliderType.RightForearm,
            EBodyPartColliderType.LeftCalf, EBodyPartColliderType.RightCalf,
            EBodyPartColliderType.LeftUpperArm, EBodyPartColliderType.RightUpperArm,
            EBodyPartColliderType.LeftThigh, EBodyPartColliderType.RightThigh,
        };

        private void Awake()
        {
            Instance = this;
            HitCollector.PublishedVolley += OnVolley;
        }

        private void OnDestroy()
        {
            HitCollector.PublishedVolley -= OnVolley;
            ResetRaid();
        }

        internal void ResetRaid()
        {
            foreach (var body in _bodies.Values) Restore(body);
            _bodies.Clear();
            while (_pieces.Count > 0)
            {
                var go = _pieces.Dequeue();
                if (go != null) Destroy(go);
            }
        }

        // ---- Triggers ----------------------------------------------------------------------------

        private void OnVolley(HitVolley volley)
        {
            var target = volley.Target;
            if (!Plugin.GoreEnabled.Value || target == null || !volley.First.IsAI || volley.First.IsLocalPlayer || volley.Status == HitStatus.Alive)
            {
                return;
            }

            try
            {
                bool corpse = volley.Status == HitStatus.Corpse;
                TryHead(volley, corpse);
                TryLimbs(volley, corpse);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Dismemberment: {ex}");
            }
        }

        private void TryHead(HitVolley volley, bool corpse)
        {
            if (Find(volley.Target)?.HeadGone == true) return;

            int pellets = 0;
            float total = 0f;
            HitEvent dominant = null;
            WoundPath dominantPath = default;
            string why = null;
            HitEvent burst = null;
            WoundPath burstPath = default;
            foreach (var p in volley.Projectiles)
            {
                if (!WoundPath.Resolve(p, out var path) || !WoundPath.IsHead(path.Entry.Collider)) continue;
                pellets++;
                total += path.Energy;
                if (dominant == null || path.Energy > dominantPath.Energy)
                {
                    dominant = p;
                    dominantPath = path;
                }

                float calibre = WoundDecals.Calibre(p.Caliber);
                if (p.ProjectileCount <= 1 && calibre >= 12f && !PistolCalibre(p.Caliber) && path.Energy >= Plugin.HeadLargeCalibreEnergy.Value)
                {
                    why = $"{p.AmmoName} {calibre:F1} mm, {path.Energy:F0} J";
                }
                else if (path.Energy >= Plugin.HeadAnyEnergy.Value)
                {
                    why = $"{p.AmmoName} {path.Energy:F0} J";
                }
                else if (p.ProjectileCount <= 1 && path.Energy >= Plugin.SkullBurstEnergy.Value && (burst == null || path.Energy > burstPath.Energy))
                {
                    burst = p;
                    burstPath = path;
                }
            }

            if (dominant == null) return;
            // Pellet count and range only: EFT's buckshot masses aren't physical (7 mm buck: 0.09 g, ~8 J
            // per pellet), so energy can't tell a point-blank shell from birdshot.
            // 0.10.12 (Stompa, 2026-10-16: "no matter how many times you shoot a npc to head with shotgun with normal
            // pellets the head did not disappear"): 0.10.8's extra 1000 J floor is gone. EFT's 5.25 mm buckshot reads
            // ~500 J a shell (8 x 63 J), so it never passed, and a real 12-gauge shell carries ~3 kJ whatever the load:
            // five pellets in the head from within `Shotgun range` is a shell that hit the head.
            if (why == null && dominant.ProjectileCount > 1 && pellets >= Plugin.HeadPellets.Value &&
                dominant.Distance <= Plugin.ShotgunRange.Value)
            {
                why = $"{pellets} pellets ({dominant.AmmoName}) at {dominant.Distance:F1} m, {total:F0} J";
            }

            // Cumulative (0.10.8, Stompa: "head should get destroyed if shot enough times even with smaller calibres"):
            // every round that entered the head of this dead body adds up; enough hits and energy break the skull
            // apart. Defaults 3 hits / 6000 J: 9x19 ~13 hits, 5.45 ~5, 5.56 ~4, 7.62x39 3, a burst head 2 more.
            var body = GetBody(volley.Target);
            body.HeadHits += pellets > 0 && dominant.ProjectileCount > 1 ? 1 : pellets;
            body.HeadEnergy += total;
            if (why == null && body.HeadHits >= Plugin.HeadCumulativeHits.Value && body.HeadEnergy >= Plugin.HeadCumulativeEnergy.Value)
            {
                why = $"cumulative: {body.HeadHits} hits, {body.HeadEnergy:F0} J";
            }
            else if (pellets > 0 && Plugin.LogBlood.Value)
            {
                Plugin.Log.LogInfo($"[Gore] {volley.Target.Profile?.Nickname}: head took {total:F0} J ({body.HeadHits} hits, {body.HeadEnergy:F0} J so far)");
            }
            if (why == null)
            {
                // Below the destruction rules a full-power rifle (or .50 AE) bullet still bursts the skull
                // open at the exit (0.10.5, Stompa): the head stays on.
                if (burst != null) BurstSkull(volley.Target, burstPath, $"{burst.AmmoName} {burstPath.Energy:F0} J", corpse);
                return;
            }

            DestroyHead(volley.Target, dominantPath.Direction, Mathf.Max(total, dominantPath.Energy), why, corpse);
        }

        /// <summary>
        /// Handgun calibres of 12 mm or more (.50 AE; 0.10.5): no limb loss and not the slug/12.7 mm head
        /// rule. The 0.10.4 test: a Desert Eagle Hawk JSP (2850 J) took heads, a calf and a thigh as "12.7 mm".
        /// </summary>
        private static bool PistolCalibre(string caliber) =>
            !string.IsNullOrEmpty(caliber) && (caliber.StartsWith("127x33") || caliber.StartsWith("1143x23") || caliber.StartsWith("9x33"));

        private void TryLimbs(HitVolley volley, bool corpse)
        {
            foreach (var p in volley.Projectiles)
            {
                if (!WoundPath.Resolve(p, out var path) || !Limbs.Contains(path.Entry.Collider)) continue;
                if (!HeavyCalibre(p.Caliber) || path.Energy < Plugin.LimbEnergy.Value) continue;
                Sever(volley.Target, path.Entry.Collider, path.Direction * Mathf.Lerp(3f, 8f, Mathf.Clamp01(path.Energy / 8000f)),
                    $"{p.AmmoName} {path.Energy:F0} J", corpse);
            }
        }

        /// <summary>12.7 mm rifle/MG and up (12.7x55, 12.7x99, 12.7x108, 30 mm, 40 mm); shotguns and the .50 AE pistol don't count for limbs.</summary>
        private static bool HeavyCalibre(string caliber) =>
            !string.IsNullOrEmpty(caliber) && !PistolCalibre(caliber) &&
            (caliber.StartsWith("127") || caliber.StartsWith("30x") || caliber.StartsWith("40x"));

        /// <summary>
        /// Called by Blast after a grenade explodes: once EFT has applied its damage, every dead bot
        /// near the charge may lose the head (~0.3 m) or up to two limbs (~0.3-0.5 m), scaled by the
        /// charge's cube root (RGD-5 = 1).
        /// </summary>
        internal void OnExplosion(Vector3 at, float tntKg, string name)
        {
            if (!Plugin.GoreEnabled.Value || tntKg <= 0f) return;
            StartCoroutine(ExplosionLater(at, tntKg, name));
        }

        private IEnumerator ExplosionLater(Vector3 at, float tntKg, string name)
        {
            yield return null;
            yield return null;
            if (!Singleton<GameWorld>.Instantiated) yield break;

            float scale = Mathf.Pow(tntKg / 0.11f, 1f / 3f) * Plugin.BlastLimbRange.Value;
            foreach (var player in Singleton<GameWorld>.Instance.AllPlayersEverExisted.ToList())
            {
                if (player == null || !player.IsAI || player.IsYourPlayer || player.ActiveHealthController == null ||
                    player.ActiveHealthController.IsAlive || player.PlayerBones == null)
                {
                    continue;
                }
                var colliders = player.PlayerBones.BodyPartCollidersDictionary;
                if (colliders == null) continue;

                try
                {
                    // 0.10.6: distances to each part's SURFACE (0.10.5 measured to the collider centre: a grenade
                    // against a thigh read ~10 cm further than it was), "[Blast]" line per body within 2.5 m.
                    var parts = new List<string>();
                    var done = new List<string>();
                    float nearest = float.MaxValue;
                    if (colliders.TryGetValue(EBodyPartColliderType.HeadCommon, out var head) && head?.Collider != null)
                    {
                        var p = SurfacePoint(head.Collider, at);
                        float d = Vector3.Distance(p, at);
                        nearest = Mathf.Min(nearest, d);
                        float reach = 0.3f * scale;
                        bool blocked = d < reach && Blocked(at, p);
                        parts.Add($"head {d:F2}/{reach:F2}{(blocked ? " blocked" : "")}");
                        if (d < reach && !blocked && Random.value < (d < 0.12f * scale ? 0.95f : 0.85f))
                        {
                            DestroyHead(player, Away(at, head.Collider.bounds.center), 3000f, $"{name} at {d:F2} m", corpse: true);
                            done.Add("head destroyed");
                        }
                    }

                    int severed = 0;
                    foreach (var type in Limbs)
                    {
                        if (!colliders.TryGetValue(type, out var bpc) || bpc?.Collider == null) continue;
                        bool distal = type == EBodyPartColliderType.LeftForearm || type == EBodyPartColliderType.RightForearm ||
                                      type == EBodyPartColliderType.LeftCalf || type == EBodyPartColliderType.RightCalf;
                        float range = (distal ? 0.5f : 0.3f) * scale;
                        var p = SurfacePoint(bpc.Collider, at);
                        float d = Vector3.Distance(p, at);
                        nearest = Mathf.Min(nearest, d);
                        bool blocked = d < range && Blocked(at, p);
                        parts.Add($"{type} {d:F2}/{range:F2}{(blocked ? " blocked" : "")}");
                        if (severed >= 2 || d >= range || blocked) continue;
                        // Against the limb: near certain; otherwise falling off with distance.
                        float chance = d < 0.12f * scale ? 0.95f : 0.8f * (1f - d / range) + 0.2f;
                        if (Random.value >= chance) continue;
                        if (Sever(player, type, Away(at, bpc.Collider.bounds.center) * Mathf.Lerp(8f, 3f, d / range), $"{name} at {d:F2} m", corpse: true))
                        {
                            severed++;
                            done.Add($"{type} severed");
                        }
                    }
                    if (nearest < 2.5f && Plugin.LogBlood.Value)
                    {
                        Plugin.Log.LogInfo($"[Blast] {name} ({tntKg:F3} kg TNT, reach x{scale:F2}) {player.Profile?.Nickname}: " +
                                           $"{string.Join(", ", parts)} -> {(done.Count > 0 ? string.Join(", ", done) : "nothing came off")}");
                    }
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"Dismemberment (blast): {ex}");
                }
            }
        }

        /// <summary>
        /// Solid geometry between the charge and the body part. Starts 5 cm above the charge and stops 3 cm
        /// short of the part (a grenade lying on the floor under a leg isn't "behind" that floor).
        /// </summary>
        private static bool Blocked(Vector3 from, Vector3 to)
        {
            from += Vector3.up * 0.05f;
            var d = to - from;
            float length = d.magnitude - 0.03f;
            return length > 0.01f && Physics.Raycast(from, d / d.magnitude, length, LayersMaskController.HighPolyWithTerrainMask);
        }

        /// <summary>The point of the collider's surface nearest <paramref name="at"/> (at itself when inside).</summary>
        private static Vector3 SurfacePoint(Collider c, Vector3 at) =>
            c is MeshCollider mesh && !mesh.convex ? c.bounds.ClosestPoint(at) : c.ClosestPoint(at);

        private static Vector3 Away(Vector3 from, Vector3 to)
        {
            var d = to - from;
            d.y = Mathf.Max(d.y, 0f);
            return (d.normalized + Vector3.up * 0.4f).normalized;
        }

        // ---- Head ----------------------------------------------------------------------------------

        private void DestroyHead(Player player, Vector3 dir, float energy, string why, bool corpse)
        {
            var bones = player.PlayerBones;
            var head = bones?.Head?.Original;
            var neck = bones?.Neck;
            if (head == null || neck == null) return;

            var body = GetBody(player);
            if (body.HeadGone) return;
            body.HeadGone = true;

            var centre = head.position;
            var colliders = bones.BodyPartCollidersDictionary;
            if (colliders != null && colliders.TryGetValue(EBodyPartColliderType.HeadCommon, out var hc) && hc?.Collider != null)
            {
                centre = hc.Collider.bounds.center;
            }
            var up = head.position - neck.position;
            up = up.sqrMagnitude > 1e-4f ? up.normalized : Vector3.up;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : -up;
            var stumpAt = head.position;
            int layer = BodyLayer(player);

            if (!body.HelmetThrown) ThrowHeadwear(player, dir, energy);
            body.HelmetThrown = true;
            Collapse(body, head);

            var stump = GoreMeshes.Stump(neck, stumpAt, up, 0.06f, layer);
            if (stump != null) body.Attached.Add(stump);

            // Carotids spurt (while the heart or its last beats pump), the jugular pours; the rest of
            // the head's blood and what the neck vessels hold run out over the next minutes.
            var side = Vector3.Cross(up, dir);
            side = side.sqrMagnitude > 1e-4f ? side.normalized : Vector3.Cross(up, Vector3.forward).normalized;
            var front = Vector3.Cross(side, up).normalized;
            var bleed = BleedSystem.Instance;
            if (bleed != null)
            {
                bleed.AddStump(player, corpse, neck, EBodyPart.Head, EBodyPartColliderType.NeckFront,
                    stumpAt + side * 0.02f + front * 0.015f, up + side * 0.25f, 1300f, true, 0f, "neck (left carotid)");
                bleed.AddStump(player, corpse, neck, EBodyPart.Head, EBodyPartColliderType.NeckFront,
                    stumpAt - side * 0.02f + front * 0.015f, up - side * 0.25f, 1300f, true, 0f, "neck (right carotid)");
                bleed.AddStump(player, corpse, neck, EBodyPart.Head, EBodyPartColliderType.NeckBack,
                    stumpAt, up, 450f, false, 900f, "neck (jugulars, head)");
            }

            // The head comes apart along the shot: gibs, skull pieces, a large cloud, a big splash.
            // 0.10.4: faster everything (0.10.3 test: "particles flying out very slowly"). Buckshot's
            // ~1950 J gave gibs at 1.3-4.5 m/s and the mist hung ~1 s; a head bursting throws fragments
            // at well over 10 m/s and the cloud is gone within a few tenths of a second.
            if (Plugin.GoreGibs.Value) Gibs(player, centre, dir, Random.Range(16, 26), 0.35f, Mathf.Clamp(energy / 2000f, 1f, 1.8f), 0.3f);
            var mist = BloodMist.Instance;
            mist?.Puff(centre, dir, 0.3f, 1f, 28, 3f);
            mist?.Puff(centre, -dir, 0.14f, 0.7f, 8, 2.5f);
            mist?.Puff(centre, up, 0.18f, 0.8f, 10, 2.5f);
            var drops = BloodParticles.Instance;
            drops?.Emit(centre, dir * 10f, 30f, 70, 0.012f);
            drops?.Emit(centre, (dir + up).normalized * 6f, 60f, 35, 0.01f);
            Splash(centre, dir, 3.5f, 50, 0.3f, Random.Range(3, 6));

            Plugin.Log.LogInfo($"[Gore] {player.Profile?.Nickname}: head destroyed ({why}){(corpse ? " [corpse]" : " [killing shot]")}");
        }

        /// <summary>The helmet flies off: a visual clone of its renderers with physics (the real model is EFT's).</summary>
        private void ThrowHeadwear(Player player, Vector3 dir, float energy)
        {
            if (player.PlayerBody?.SlotViews == null || !player.PlayerBody.SlotViews.TryGetByKey(EquipmentSlot.Headwear, out var view) ||
                view?.Model == null)
            {
                return;
            }

            var model = view.Model.transform;
            var root = new GameObject("RealTargetsThrownHelmet") { layer = LayersMaskController.DeadbodyLayer };
            root.transform.SetPositionAndRotation(model.position, model.rotation);
            var bounds = new Bounds(model.position, Vector3.zero);
            bool any = false;
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                Mesh mesh = null;
                if (r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var filter)) mesh = filter.sharedMesh;
                else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    mesh = new Mesh { name = "RealTargetsHelmetBake" };
                    smr.BakeMesh(mesh, false);
                }
                if (mesh == null) continue;

                var part = new GameObject("part") { layer = r.gameObject.layer };
                part.transform.SetParent(root.transform, false);
                part.transform.SetPositionAndRotation(r.transform.position, r.transform.rotation);
                part.transform.localScale = r.transform.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = r.sharedMaterials;
                bounds.Encapsulate(r.bounds);
                any = true;
            }
            if (!any)
            {
                Destroy(root);
                return;
            }

            var box = root.AddComponent<BoxCollider>();
            box.center = root.transform.InverseTransformPoint(bounds.center);
            box.size = bounds.size * 0.8f;
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 1.3f;
            rb.velocity = dir * Mathf.Lerp(3f, 7f, Mathf.Clamp01(energy / 4000f)) + Vector3.up * 2f;
            rb.angularVelocity = Random.insideUnitSphere * 12f;
            var piece = root.AddComponent<GorePiece>();
            piece.Bloody = false;
            piece.Label = $"{player.Profile?.Nickname} helmet";
            piece.Init(rb, player);
            Track(root);
        }

        // ---- Skull burst (0.10.5) ------------------------------------------------------------------

        private sealed class HeadSkinCut
        {
            public SkinnedMeshRenderer Renderer;
            public Mesh Mesh;
            public bool Lod0;
            public Vector3[] World;
            public float[] Share;
            public int[][] Tris;
            public readonly List<(int sub, int tri)> Removed = new List<(int, int)>();
        }

        /// <summary>
        /// A full-power rifle or .50 AE bullet through the head, below the destruction rules: the head
        /// stays on, but the skull bursts open around the exit (Kronlein-type wound; Stompa chose this tier
        /// for the .50 AE, 2026-10-16). Every skin skinned to the head (all LODs: face, balaclava, hair)
        /// gets a copy of its mesh without the triangles in an irregular radius around the exit (restored
        /// when the body goes back to EFT's pool); a crater built on the hole's own rim (skull edge, brain,
        /// clot) closes it, skull shards stick out of the rim; gibs, mist and a splash along the shot. A
        /// helmet over the exit flies off. Wound patches over the hole are removed.
        /// </summary>
        private void BurstSkull(Player player, WoundPath path, string why, bool corpse)
        {
            var bones = player.PlayerBones;
            var head = bones?.Head?.Original;
            if (head == null) return;
            var body = GetBody(player);
            if (body.HeadGone || body.HeadBurst) return;

            Collider headCollider = null;
            var centre = head.position;
            var colliders = bones.BodyPartCollidersDictionary;
            if (colliders != null && colliders.TryGetValue(EBodyPartColliderType.HeadCommon, out var hc) && hc?.Collider != null)
            {
                headCollider = hc.Collider;
                centre = headCollider.bounds.center;
            }
            var dir = path.Direction.sqrMagnitude > 1e-4f ? path.Direction.normalized : (centre - path.Start).normalized;

            var skins = HeadSkins(player, head);
            if (skins.Count == 0)
            {
                Plugin.Log.LogInfo($"[Gore] {player.Profile?.Nickname}: skull burst ({why}) skipped: no readable head skin");
                return;
            }

            // The exit on the drawn surface: the farthest head triangle (LOD0) on the shot line. EFT's exit
            // point is on the hit collider, which only roughly follows the head mesh.
            var exit = Vector3.zero;
            float best = -1f;
            foreach (var skin in skins)
            {
                if (!skin.Lod0) continue;
                for (int sub = 0; sub < skin.Mesh.subMeshCount; sub++)
                {
                    var tris = skin.Tris[sub];
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                        if (skin.Share[a] < 0.4f || skin.Share[b] < 0.4f || skin.Share[c] < 0.4f) continue;
                        var mid = (skin.World[a] + skin.World[b] + skin.World[c]) / 3f;
                        var rel = mid - path.Start;
                        float along = Vector3.Dot(rel, dir);
                        if (along < 0.03f || along > 0.35f || along <= best) continue;
                        if ((rel - dir * along).sqrMagnitude < 0.03f * 0.03f)
                        {
                            best = along;
                            exit = mid;
                        }
                    }
                }
            }
            string exitFrom = "mesh";
            if (best < 0f)
            {
                exitFrom = path.Exited ? "EFT exit" : "collider far side";
                exit = path.Exited ? path.End : centre + dir * 0.09f;
                if (!path.Exited && headCollider != null && headCollider.Raycast(new Ray(path.Start + dir * 0.5f, -dir), out var far, 0.5f))
                {
                    exit = far.point;
                }
            }

            var outward = exit - centre;
            outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : dir;
            var n = (outward * 0.7f + dir * 0.3f).normalized;
            float energy = path.Energy;
            float radius = Mathf.Lerp(0.035f, 0.06f, Mathf.InverseLerp(Plugin.SkullBurstEnergy.Value, Plugin.HeadAnyEnergy.Value, energy));
            var u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
            var v = Vector3.Cross(n, u);
            float p1 = Random.Range(0f, 6.28f), p2 = Random.Range(0f, 6.28f), p3 = Random.Range(0f, 6.28f);

            // Torn, not round: the radius wobbles with the angle around the exit.
            bool Inside(Vector3 point)
            {
                var q = point - exit;
                float depth = Vector3.Dot(q, n);
                if (depth < -0.05f) return false;
                var planar = q - n * depth;
                float r = planar.magnitude;
                if (r < 1e-5f) return true;
                float angle = Mathf.Atan2(Vector3.Dot(planar, v), Vector3.Dot(planar, u));
                return r < radius * (1f + 0.22f * Mathf.Sin(3f * angle + p1) + 0.12f * Mathf.Sin(5f * angle + p2) + 0.07f * Mathf.Sin(9f * angle + p3));
            }

            int removed = 0, cutSkins = 0;
            HeadSkinCut rimSkin = null;
            foreach (var skin in skins)
            {
                var copy = Instantiate(skin.Mesh);
                copy.name = skin.Mesh.name + " (RealTargets burst)";
                bool any = false;
                for (int sub = 0; sub < skin.Mesh.subMeshCount; sub++)
                {
                    var tris = skin.Tris[sub];
                    var kept = new List<int>(tris.Length);
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                        if (skin.Share[a] >= 0.4f && skin.Share[b] >= 0.4f && skin.Share[c] >= 0.4f &&
                            Inside((skin.World[a] + skin.World[b] + skin.World[c]) / 3f))
                        {
                            skin.Removed.Add((sub, t));
                            any = true;
                            continue;
                        }
                        kept.Add(a);
                        kept.Add(b);
                        kept.Add(c);
                    }
                    copy.SetTriangles(kept, sub, false);
                }
                if (!any)
                {
                    Destroy(copy);
                    continue;
                }
                skin.Renderer.sharedMesh = copy;
                body.Meshes.Add((skin.Renderer, skin.Mesh, copy));
                removed += skin.Removed.Count;
                cutSkins++;
                if (skin.Lod0 && (rimSkin == null || skin.Removed.Count > rimSkin.Removed.Count)) rimSkin = skin;
            }
            if (removed == 0)
            {
                Plugin.Log.LogInfo($"[Gore] {player.Profile?.Nickname}: skull burst ({why}) found no head surface at the exit ({exitFrom})");
                return;
            }
            body.HeadBurst = true;
            int layer = BodyLayer(player);

            // The helmet over the exit flies off (the burst lifts it); the real one shrinks away with the
            // collapse machinery so it can't come back on.
            if (player.PlayerBody?.SlotViews != null && player.PlayerBody.SlotViews.TryGetByKey(EquipmentSlot.Headwear, out var view) &&
                view?.Model != null && OverExit(view.Model, exit))
            {
                ThrowHeadwear(player, dir, energy);
                body.HelmetThrown = true;
                Collapse(body, view.Model.transform);
            }

            int rim = rimSkin != null ? BuildCrater(body, head, rimSkin, exit, n, radius, layer) : 0;

            WoundDecals.Instance?.ClearNear(player, exit, radius * 1.5f);
            StartCoroutine(ClearLater(player, exit, radius * 1.5f));

            BleedSystem.Instance?.AddStump(player, corpse, head, EBodyPart.Head, EBodyPartColliderType.HeadCommon, exit, n, 350f, false, 500f, "skull burst");

            if (Plugin.GoreGibs.Value) Gibs(player, exit, (dir + n).normalized, Random.Range(8, 14), 0.4f, Mathf.Clamp(energy / 2500f, 0.8f, 1.5f), 0.4f);
            var mist = BloodMist.Instance;
            mist?.Puff(exit, dir, 0.22f, 0.9f, 18, 2.2f);
            mist?.Puff(exit, n, 0.12f, 0.6f, 6, 2f);
            BloodParticles.Instance?.Emit(exit, dir * 8f, 35f, 45, 0.011f);
            Splash(exit, dir, 3f, 35, 0.25f, Random.Range(2, 5));

            Plugin.Log.LogInfo($"[Gore] {player.Profile?.Nickname}: skull burst ({why}){(corpse ? " [corpse]" : " [killing shot]")}: exit from {exitFrom}, " +
                               $"r {radius * 100f:F1} cm, {removed} triangles out of {cutSkins} skin(s), crater rim {rim} edges");
        }

        /// <summary>Every skin (all LODs) with vertices skinned to the head bone or below it, baked to world now.</summary>
        private static List<HeadSkinCut> HeadSkins(Player player, Transform head)
        {
            var lod0 = new HashSet<SkinnedMeshRenderer>(GoreMeshes.Lod0Skins(player));
            var list = new List<HeadSkinCut>();
            foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr == null || smr.sharedMesh == null || smr.name.StartsWith("RealTargets") || !smr.sharedMesh.isReadable) continue;
                var bones = smr.bones;
                if (bones == null) continue;
                var inHead = new bool[bones.Length];
                bool anyBone = false;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] != null && (bones[i] == head || bones[i].IsChildOf(head)))
                    {
                        inHead[i] = true;
                        anyBone = true;
                    }
                }
                if (!anyBone) continue;

                var mesh = smr.sharedMesh;
                var weights = mesh.boneWeights;
                if (weights == null || weights.Length != mesh.vertexCount) continue;
                var share = new float[weights.Length];
                bool anyVertex = false;
                for (int vi = 0; vi < weights.Length; vi++)
                {
                    var w = weights[vi];
                    float s = 0f;
                    if (w.boneIndex0 < inHead.Length && inHead[w.boneIndex0]) s += w.weight0;
                    if (w.boneIndex1 < inHead.Length && inHead[w.boneIndex1]) s += w.weight1;
                    if (w.boneIndex2 < inHead.Length && inHead[w.boneIndex2]) s += w.weight2;
                    if (w.boneIndex3 < inHead.Length && inHead[w.boneIndex3]) s += w.weight3;
                    share[vi] = s;
                    if (s >= 0.4f) anyVertex = true;
                }
                if (!anyVertex) continue;

                var baked = new Mesh();
                smr.BakeMesh(baked, false);
                var local = baked.vertices;
                Destroy(baked);
                var world = new Vector3[local.Length];
                for (int vi = 0; vi < local.Length; vi++) world[vi] = smr.transform.TransformPoint(local[vi]);
                if (world.Length != share.Length) continue;

                var tris = new int[mesh.subMeshCount][];
                for (int sub = 0; sub < tris.Length; sub++) tris[sub] = mesh.GetTriangles(sub);
                list.Add(new HeadSkinCut { Renderer = smr, Mesh = mesh, Lod0 = lod0.Contains(smr), World = world, Share = share, Tris = tris });
            }
            return list;
        }

        private static bool OverExit(GameObject model, Vector3 exit)
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else bounds.Encapsulate(r.bounds);
            }
            if (!any) return false;
            bounds.Expand(0.03f);
            return bounds.Contains(exit);
        }

        /// <summary>
        /// Closes the hole with a crater built on its own rim: the open edges of the removed triangles of
        /// the outermost cut skin (welded by position, so UV seams don't count), each joined to a ragged
        /// middle ring and a centre deep in the skull; drawn facing out of the hole. Parented to the head
        /// bone (the rim vertices move rigidly with it). A few skull shards stick out of the rim.
        /// Returns the rim edge count.
        /// </summary>
        private int BuildCrater(Body body, Transform head, HeadSkinCut skin, Vector3 exit, Vector3 n, float radius, int layer)
        {
            var material = GoreMeshes.CraterMaterial;
            if (material == null) return 0;

            long Key(Vector3 p) =>
                ((long)(Mathf.RoundToInt(p.x * 1000f) & 0xFFFFF) << 40) | ((long)(Mathf.RoundToInt(p.y * 1000f) & 0xFFFFF) << 20) |
                (long)(Mathf.RoundToInt(p.z * 1000f) & 0xFFFFF);

            var count = new Dictionary<(long, long), int>();
            var where = new Dictionary<(long, long), (Vector3, Vector3)>();
            foreach (var (sub, t) in skin.Removed)
            {
                var tris = skin.Tris[sub];
                for (int e = 0; e < 3; e++)
                {
                    var p = skin.World[tris[t + e]];
                    var q = skin.World[tris[t + (e + 1) % 3]];
                    long a = Key(p), b = Key(q);
                    if (a == b) continue;
                    var k = a < b ? (a, b) : (b, a);
                    count.TryGetValue(k, out int c);
                    count[k] = c + 1;
                    where[k] = (p, q);
                }
            }

            var centre = exit - n * (radius * 0.85f);
            var u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
            var w = Vector3.Cross(n, u);
            var vertices = new List<Vector3> { head.InverseTransformPoint(centre) };
            var uvs = new List<Vector2> { new Vector2(0.5f, 1f) };
            var triangles = new List<int>();
            var rimIndex = new Dictionary<long, int>();
            var rimPoints = new List<Vector3>();
            var viewer = exit + n * 0.4f;

            int Rim(Vector3 p)
            {
                long k = Key(p);
                if (rimIndex.TryGetValue(k, out int i)) return i;
                i = vertices.Count;
                rimIndex[k] = i;
                var planar = Vector3.ProjectOnPlane(p - exit, n);
                float uCoord = (Mathf.Atan2(Vector3.Dot(planar, w), Vector3.Dot(planar, u)) / (2f * Mathf.PI) + 1f) % 1f;
                vertices.Add(head.InverseTransformPoint(p));
                uvs.Add(new Vector2(uCoord, 0f));
                // The ring halfway down, a little ragged: torn tissue rather than a smooth funnel.
                var mid = Vector3.Lerp(p, centre, 0.45f) - n * 0.004f + Random.insideUnitSphere * 0.004f;
                vertices.Add(head.InverseTransformPoint(mid));
                uvs.Add(new Vector2(uCoord, 0.45f));
                rimPoints.Add(p);
                return i;
            }

            void Tri(int a, int b, int c)
            {
                var pa = head.TransformPoint(vertices[a]);
                var normal = Vector3.Cross(head.TransformPoint(vertices[b]) - pa, head.TransformPoint(vertices[c]) - pa);
                var mid = (pa + head.TransformPoint(vertices[b]) + head.TransformPoint(vertices[c])) / 3f;
                triangles.Add(a);
                // Front face toward someone looking into the hole (lit shaders cull back faces).
                if (Vector3.Dot(normal, viewer - mid) >= 0f)
                {
                    triangles.Add(b);
                    triangles.Add(c);
                }
                else
                {
                    triangles.Add(c);
                    triangles.Add(b);
                }
            }

            int edges = 0;
            foreach (var pair in count)
            {
                if (pair.Value != 1) continue;
                var (p, q) = where[pair.Key];
                int ip = Rim(p), iq = Rim(q);
                int mp = ip + 1, mq = iq + 1;
                Tri(ip, iq, mq);
                Tri(ip, mq, mp);
                Tri(mp, mq, 0);
                edges++;
            }
            if (edges == 0) return 0;

            var mesh = new Mesh { name = "RealTargetsCrater" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject("RealTargetsCrater") { layer = layer };
            go.transform.SetParent(head, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            GoreMeshes.Shade(r);
            go.AddComponent<OwnedMesh>().Mesh = mesh;
            body.Attached.Add(go);

            // Skull shards standing out of the rim.
            var bone = GoreMeshes.GibMaterial(true);
            int shards = Mathf.Min(rimPoints.Count, Random.Range(4, 8));
            for (int i = 0; i < shards && bone != null; i++)
            {
                var at = rimPoints[Random.Range(0, rimPoints.Count)];
                var shard = new GameObject("RealTargetsSkullShard") { layer = layer };
                shard.transform.SetParent(head, true);
                var up = (n + (at - exit).normalized * 0.6f + Random.insideUnitSphere * 0.35f).normalized;
                shard.transform.SetPositionAndRotation(at + n * 0.003f, Quaternion.LookRotation(up) * Quaternion.Euler(90f, Random.Range(0f, 360f), 0f));
                float size = Random.Range(0.012f, 0.026f);
                var lossy = head.lossyScale;
                shard.transform.localScale = new Vector3(size / Mathf.Max(lossy.x, 1e-4f), size * 0.7f / Mathf.Max(lossy.y, 1e-4f), size * 1.3f / Mathf.Max(lossy.z, 1e-4f));
                shard.AddComponent<MeshFilter>().sharedMesh = GoreMeshes.Gib(true);
                var sr = shard.AddComponent<MeshRenderer>();
                sr.sharedMaterial = bone;
                GoreMeshes.Shade(sr);
                body.Attached.Add(shard);
            }
            return edges;
        }

        /// <summary>Wound patches are placed before the volley reaches us; a patch of the same frame may land a frame later.</summary>
        private IEnumerator ClearLater(Player player, Vector3 at, float radius)
        {
            yield return null;
            if (player != null) WoundDecals.Instance?.ClearNear(player, at, radius);
        }

        // ---- Limbs ---------------------------------------------------------------------------------

        private bool Sever(Player player, EBodyPartColliderType type, Vector3 velocity, string why, bool corpse)
        {
            var colliders = player.PlayerBones?.BodyPartCollidersDictionary;
            if (colliders == null || !colliders.TryGetValue(type, out var bpc) || bpc == null) return false;

            var skins = GoreMeshes.Lod0Skins(player);
            var skinBones = new HashSet<Transform>();
            foreach (var smr in skins)
            {
                foreach (var b in smr.bones)
                {
                    if (b != null) skinBones.Add(b);
                }
            }

            // The skin bone carrying this collider (the collider sits on it or on a child of it).
            var bone = bpc.TransformCached;
            while (bone != null && !skinBones.Contains(bone)) bone = bone.parent;
            if (bone == null || bone.parent == null) return false;

            var body = GetBody(player);
            string key = Prefix(bone.name);
            if (body.Cut.Contains(key)) return false;
            foreach (var s in body.Scaled)
            {
                if (s.bone != null && bone.IsChildOf(s.bone)) return false; // already gone with a higher cut
            }

            var group = TwistGroup(player, bone, key);
            var frame = BodyFrame.Build(player);
            Vector3 proximal = bone.position, axis = (bone.position - bone.parent.position).normalized;
            float radius = 0.05f;
            if (frame != null && frame.Limbs.TryGetValue(type, out var limb))
            {
                proximal = limb.Proximal;
                axis = (limb.Distal - limb.Proximal).normalized;
                radius = Mathf.Clamp(limb.Radius * 1.05f, 0.035f, 0.09f);
            }
            int layer = BodyLayer(player);

            int triangles = ExtractPiece(player, $"{player.Profile?.Nickname} {type}", skins, group, proximal, axis, radius, layer, velocity, Mass(type));
            body.Cut.Add(key);
            foreach (var b in group) Collapse(body, b);

            var stump = GoreMeshes.Stump(bone.parent, proximal, axis, radius, layer);
            if (stump != null) body.Attached.Add(stump);

            var part = type == EBodyPartColliderType.LeftForearm || type == EBodyPartColliderType.LeftUpperArm ? EBodyPart.LeftArm
                : type == EBodyPartColliderType.RightForearm || type == EBodyPartColliderType.RightUpperArm ? EBodyPart.RightArm
                : type == EBodyPartColliderType.LeftThigh || type == EBodyPartColliderType.LeftCalf ? EBodyPart.LeftLeg : EBodyPart.RightLeg;
            float flow = type == EBodyPartColliderType.LeftThigh || type == EBodyPartColliderType.RightThigh ? 1400f
                : type == EBodyPartColliderType.LeftUpperArm || type == EBodyPartColliderType.RightUpperArm ? 900f
                : type == EBodyPartColliderType.LeftCalf || type == EBodyPartColliderType.RightCalf ? 500f : 300f;
            BleedSystem.Instance?.AddStump(player, corpse, bone.parent, part, type, proximal, axis, flow, true, flow * 0.4f, $"{type} stump");

            var dir = velocity.sqrMagnitude > 1e-4f ? velocity.normalized : axis;
            if (Plugin.GoreGibs.Value) Gibs(player, proximal, dir, Random.Range(4, 9), 0.3f, 0.8f);
            BloodMist.Instance?.Puff(proximal, dir, 0.18f, 0.9f, 14, 2f);
            BloodParticles.Instance?.Emit(proximal, dir * 6f, 40f, 40, 0.011f);
            Splash(proximal, dir, 2.5f, 25, 0.2f);

            Plugin.Log.LogInfo($"[Gore] {player.Profile?.Nickname}: {type} severed ({why}){(corpse ? " [corpse]" : " [killing shot]")}: " +
                               $"{group.Count} bone(s) collapsed, piece {triangles} triangles");
            return true;
        }

        private static float Mass(EBodyPartColliderType type)
        {
            switch (type)
            {
                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm: return 1.8f;
                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.RightUpperArm: return 4f;
                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightCalf: return 4.5f;
                default: return 12f;
            }
        }

        /// <summary>"Base HumanLForearm2" -> "Base HumanLForearm" (twist bones share it).</summary>
        private static string Prefix(string name) => name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        /// <summary>
        /// The cut bone plus same-named twist bones (Forearm1/2/3, Thigh1/2) that aren't already
        /// under another member; collapsing them all removes the whole segment and what hangs below.
        /// </summary>
        private static List<Transform> TwistGroup(Player player, Transform bone, string key)
        {
            var all = new List<Transform> { bone };
            foreach (var t in player.GetComponentsInChildren<Transform>())
            {
                if (t != bone && t.name.StartsWith(key) && Prefix(t.name) == key) all.Add(t);
            }
            var group = new List<Transform>();
            foreach (var t in all)
            {
                bool nested = false;
                foreach (var other in all)
                {
                    if (other != t && t.IsChildOf(other)) nested = true;
                }
                if (!nested) group.Add(t);
            }
            return group;
        }

        /// <summary>
        /// Cuts the severed part out of the body's current pose: every LOD0 skin's triangles whose three
        /// vertices are mostly (>= 50 %) skinned to the removed bones become a static mesh with the
        /// skin's own materials, under one rigidbody thrown with <paramref name="velocity"/>.
        /// Returns the triangle count (0 = nothing found: no piece).
        /// </summary>
        private int ExtractPiece(Player owner, string label, List<SkinnedMeshRenderer> skins, List<Transform> cut, Vector3 proximal, Vector3 axis, float radius,
            int layer, Vector3 velocity, float mass)
        {
            var root = new GameObject("RealTargetsSevered") { layer = LayersMaskController.DeadbodyLayer };
            root.transform.SetPositionAndRotation(proximal, Quaternion.LookRotation(axis, Mathf.Abs(axis.y) < 0.95f ? Vector3.up : Vector3.forward));
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool boundsSet = false;
            int total = 0;
            var openEdge = new List<Vector3>();

            foreach (var smr in skins)
            {
                var mesh = smr.sharedMesh;
                var bones = smr.bones;
                if (!mesh.isReadable || bones == null) continue;
                var weights = mesh.boneWeights;
                if (weights == null || weights.Length != mesh.vertexCount) continue;

                var inCut = new bool[bones.Length];
                bool anyBone = false;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    foreach (var c in cut)
                    {
                        if (bones[i].IsChildOf(c))
                        {
                            inCut[i] = true;
                            anyBone = true;
                            break;
                        }
                    }
                }
                if (!anyBone) continue;

                var share = new float[weights.Length];
                for (int v = 0; v < weights.Length; v++)
                {
                    var w = weights[v];
                    float s = 0f;
                    if (w.boneIndex0 < inCut.Length && inCut[w.boneIndex0]) s += w.weight0;
                    if (w.boneIndex1 < inCut.Length && inCut[w.boneIndex1]) s += w.weight1;
                    if (w.boneIndex2 < inCut.Length && inCut[w.boneIndex2]) s += w.weight2;
                    if (w.boneIndex3 < inCut.Length && inCut[w.boneIndex3]) s += w.weight3;
                    share[v] = s;
                }

                var baked = new Mesh();
                smr.BakeMesh(baked, false);
                var bv = baked.vertices;
                var bn = baked.normals;
                var uv = mesh.uv;
                Destroy(baked);

                var map = new Dictionary<int, int>();
                var vertices = new List<Vector3>();
                var normals = new List<Vector3>();
                var uvs = new List<Vector2>();
                var submeshes = new List<List<int>>();
                var materials = new List<Material>();
                var src = smr.sharedMaterials;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    var tris = mesh.GetTriangles(sub);
                    var kept = new List<int>();
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                        if (share[a] < 0.5f || share[b] < 0.5f || share[c] < 0.5f) continue;
                        foreach (int v in new[] { a, b, c })
                        {
                            if (!map.TryGetValue(v, out int nv))
                            {
                                nv = vertices.Count;
                                map[v] = nv;
                                var world = smr.transform.TransformPoint(bv[v]);
                                var local = root.transform.InverseTransformPoint(world);
                                vertices.Add(local);
                                normals.Add(root.transform.InverseTransformDirection(smr.transform.TransformDirection(v < bn.Length ? bn[v] : Vector3.up)));
                                uvs.Add(v < uv.Length ? uv[v] : Vector2.zero);
                                if (!boundsSet)
                                {
                                    bounds = new Bounds(local, Vector3.zero);
                                    boundsSet = true;
                                }
                                else bounds.Encapsulate(local);
                            }
                            kept.Add(nv);
                        }
                    }
                    if (kept.Count == 0) continue;
                    submeshes.Add(kept);
                    materials.Add(sub < src.Length ? src[sub] : src.LastOrDefault());
                    total += kept.Count / 3;
                }
                if (submeshes.Count == 0) continue;
                CollectOpenEdges(vertices, submeshes, openEdge);

                var part = new Mesh { name = "RealTargetsSeveredPart" };
                if (vertices.Count > 65000) part.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                part.SetVertices(vertices);
                part.SetNormals(normals);
                part.SetUVs(0, uvs);
                part.subMeshCount = submeshes.Count;
                for (int s = 0; s < submeshes.Count; s++) part.SetTriangles(submeshes[s], s);
                part.RecalculateBounds();

                var go = new GameObject("part") { layer = layer };
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = part;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = materials.ToArray();
                go.AddComponent<OwnedMesh>().Mesh = part;
            }

            if (total == 0)
            {
                Destroy(root);
                return 0;
            }

            // The piece's own cut end, facing back toward the body, on the piece's real open end (0.10.4).
            // The cut follows the skin weights, a few cm past the joint: a cap at the joint floated above the
            // severed leg with a visible gap (0.10.3 test).
            var capAt = proximal;
            float capRadius = radius;
            if (FitOpenEnd(openEdge, out var ringCentre, out float ringRadius))
            {
                capAt = root.transform.TransformPoint(ringCentre);
                capRadius = Mathf.Clamp(ringRadius * 1.1f, 0.03f, 0.1f);
            }
            GoreMeshes.Stump(root.transform, capAt, -axis, capRadius, layer);
            if (Plugin.LogBlood.Value)
            {
                Plugin.Log.LogInfo($"[Gore] {label}: piece cap {Vector3.Distance(capAt, proximal) * 100f:F1} cm from the joint, r {capRadius * 100f:F1} cm " +
                                   $"({openEdge.Count} open-edge points)");
            }

            // 0.10.7 (0.10.6 test: a severed leg "left floating 20-30 cm from the ground"): one box of 85 % of
            // the bounds stood a bent leg on its corner. Now a convex hull per extracted skin (trousers, boot,
            // skin), on the Deadbody layer, as one compound collider; the box only if no hull could be made.
            int hulls = 0;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null || filter.sharedMesh.vertexCount < 4) continue;
                var hull = new GameObject("hull") { layer = LayersMaskController.DeadbodyLayer };
                hull.transform.SetParent(filter.transform, false);
                var mc = hull.AddComponent<MeshCollider>();
                mc.convex = true;
                mc.sharedMesh = filter.sharedMesh;
                hulls++;
            }
            if (hulls == 0)
            {
                var box = root.AddComponent<BoxCollider>();
                box.center = bounds.center;
                box.size = Vector3.Max(bounds.size * 0.85f, Vector3.one * 0.04f);
            }
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.drag = 0.05f;
            // Out along the limb as well as away from the blast (0.10.3: a grenade at the feet threw
            // the calf straight up into its own knee).
            rb.velocity = velocity + axis * (velocity.magnitude * 0.5f + 1f) + Vector3.up * 1.5f;
            rb.angularVelocity = Random.insideUnitSphere * 6f;
            var piece = root.AddComponent<GorePiece>();
            piece.Label = label;
            piece.Init(rb, owner);
            Track(root);
            return total;
        }

        /// <summary>
        /// Adds the end points of every open edge (used by one triangle only) of one extracted skin to
        /// <paramref name="into"/> (piece-local). Vertices are welded by position (1 mm) first, so UV seams
        /// don't count as open.
        /// </summary>
        private static void CollectOpenEdges(List<Vector3> vertices, List<List<int>> submeshes, List<Vector3> into)
        {
            long Key(Vector3 p) =>
                ((long)(Mathf.RoundToInt(p.x * 1000f) & 0xFFFFF) << 40) | ((long)(Mathf.RoundToInt(p.y * 1000f) & 0xFFFFF) << 20) |
                (long)(Mathf.RoundToInt(p.z * 1000f) & 0xFFFFF);

            var count = new Dictionary<(long, long), int>();
            var where = new Dictionary<(long, long), (Vector3, Vector3)>();
            foreach (var tris in submeshes)
            {
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        var p = vertices[tris[t + e]];
                        var q = vertices[tris[t + (e + 1) % 3]];
                        long a = Key(p), b = Key(q);
                        if (a == b) continue;
                        var k = a < b ? (a, b) : (b, a);
                        count.TryGetValue(k, out int n);
                        count[k] = n + 1;
                        where[k] = (p, q);
                    }
                }
            }
            foreach (var pair in count)
            {
                if (pair.Value != 1) continue;
                var (p, q) = where[pair.Key];
                into.Add(p);
                into.Add(q);
            }
        }

        /// <summary>
        /// The ring where the piece was cut: open-edge points within 8 cm of the most proximal one (piece
        /// local +Z runs down the limb; a trouser hem at the ankle is open too and is left out). Centre =
        /// their mean; radius = 80th percentile of their distance from the limb axis through it (the
        /// outermost layer, clothes over skin).
        /// </summary>
        private static bool FitOpenEnd(List<Vector3> points, out Vector3 centre, out float radius)
        {
            centre = Vector3.zero;
            radius = 0f;
            if (points.Count < 6) return false;
            float minZ = float.MaxValue;
            foreach (var p in points) minZ = Mathf.Min(minZ, p.z);
            var ring = new List<Vector3>();
            foreach (var p in points)
            {
                if (p.z < minZ + 0.08f) ring.Add(p);
            }
            if (ring.Count < 6) return false;
            foreach (var p in ring) centre += p;
            centre /= ring.Count;
            var d = new List<float>(ring.Count);
            foreach (var p in ring) d.Add(new Vector2(p.x - centre.x, p.y - centre.y).magnitude);
            d.Sort();
            radius = d[Mathf.Min(d.Count - 1, Mathf.FloorToInt(d.Count * 0.8f))];
            return radius > 0.01f;
        }

        // ---- Shared --------------------------------------------------------------------------------

        private void Collapse(Body body, Transform bone)
        {
            body.Scaled.Add((bone, bone.localScale));
            bone.localScale = bone.localScale * Collapsed;
            // A shrunken collider gives a near-zero inertia tensor: keep the ragdoll body stable.
            if (bone.TryGetComponent<Rigidbody>(out var rb))
            {
                rb.inertiaTensor = Vector3.one * 0.02f;
                rb.inertiaTensorRotation = Quaternion.identity;
            }
        }

        /// <summary>
        /// Blood render test (0.10.3): a stump cap lying on the floor and a handful of flesh and skull
        /// gibs dropped next to it, to judge the lit gore materials without dismembering anyone.
        /// </summary>
        internal string RenderTest(Vector3 floorPoint)
        {
            var material = GoreMeshes.StumpMaterial;
            if (material == null) return "gore: no material";
            var stump = GoreMeshes.Stump(null, floorPoint + Vector3.up * 0.02f, Vector3.up, 0.07f, LayersMaskController.DeadbodyLayer);
            if (stump != null) Track(stump);
            Gibs(null, floorPoint + Vector3.up * 0.4f + Vector3.right * 0.25f, Vector3.up, 6, 0.4f, 0.25f);
            return $"stump + 6 gibs ({(GoreMeshes.Lit ? "lit " + LitBlood.OpaqueName : "unlit fallback")})";
        }

        private enum GibKind { Flesh, Bone, Brain }

        /// <summary>
        /// Flesh, bone and (heads, 0.10.6) brain chunks thrown along <paramref name="dir"/>; each leaves a splat
        /// where it lands. Wet pieces may stick to a wall or ceiling, slide and smear (brain 85 %, flesh 60 %,
        /// bone 20 %; GorePiece.StickNow).
        /// </summary>
        private void Gibs(Player owner, Vector3 at, Vector3 dir, int count, float boneShare, float force, float brainShare = 0f)
        {
            if (GoreMeshes.GibMaterial(false) == null) return;
            for (int i = 0; i < count; i++)
            {
                var kind = Random.value < brainShare ? GibKind.Brain : Random.value < boneShare ? GibKind.Bone : GibKind.Flesh;
                var go = MakeGib(kind, at + Random.insideUnitSphere * 0.06f, out var rb);
                if (go == null) continue;
                rb.velocity = (dir * Random.Range(4f, 10f) + Random.insideUnitSphere * 3f + Vector3.up * 1.2f) * force;
                rb.angularVelocity = Random.insideUnitSphere * 20f;
                var piece = go.AddComponent<GorePiece>();
                piece.StickChance = kind == GibKind.Brain ? 0.85f : kind == GibKind.Flesh ? 0.6f : 0.2f;
                piece.Init(rb, owner);
                Track(go);
            }
        }

        private static GameObject MakeGib(GibKind kind, Vector3 at, out Rigidbody rb)
        {
            rb = null;
            bool bone = kind == GibKind.Bone;
            var material = kind == GibKind.Brain ? GoreMeshes.BrainMaterial : GoreMeshes.GibMaterial(bone);
            if (material == null) return null;
            var go = new GameObject(kind == GibKind.Bone ? "RealTargetsGibBone" : kind == GibKind.Brain ? "RealTargetsGibBrain" : "RealTargetsGib")
            {
                layer = LayersMaskController.DeadbodyLayer,
            };
            go.transform.SetPositionAndRotation(at, Random.rotation);
            float size = bone ? Random.Range(0.02f, 0.05f) : kind == GibKind.Brain ? Random.Range(0.012f, 0.035f) : Random.Range(0.015f, 0.045f);
            go.transform.localScale = new Vector3(size, size * Random.Range(0.6f, 1.2f), size * Random.Range(0.7f, 1.3f));
            go.AddComponent<MeshFilter>().sharedMesh = GoreMeshes.Gib(bone);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            GoreMeshes.Shade(r);
            go.AddComponent<SphereCollider>().radius = 0.45f;
            rb = go.AddComponent<Rigidbody>();
            rb.mass = bone ? 0.03f : kind == GibKind.Brain ? 0.02f : 0.05f;
            rb.drag = 0.2f;
            // Fast and small (0.10.4): keep them from tunnelling through thin walls.
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            return go;
        }

        /// <summary>
        /// A big splash on the first surface along <paramref name="dir"/> within <paramref name="range"/>; for a
        /// head, <paramref name="brainBits"/> pieces of brain stuck in it (sliding down a wall, 0.10.6).
        /// </summary>
        private void Splash(Vector3 from, Vector3 dir, float range, int count, float radius, int brainBits = 0)
        {
            var ground = GroundBlood.Instance;
            if (ground == null || !Physics.Raycast(from, dir, out var hit, range, LayersMaskController.HighPolyWithTerrainMask) || GroundBlood.Movable(hit.collider)) return;
            float falloff = 1f - hit.distance / range;
            float spread = radius + hit.distance * 0.12f;
            ground.Spatter(hit.point, hit.normal, dir * 7f, spread, Mathf.Max(6, Mathf.RoundToInt(count * falloff)));

            int bits = Mathf.RoundToInt(brainBits * Mathf.Clamp01(falloff + 0.3f));
            var t = Vector3.Cross(hit.normal, Mathf.Abs(hit.normal.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
            var b = Vector3.Cross(hit.normal, t);
            for (int i = 0; i < bits; i++)
            {
                var offset = Random.insideUnitCircle * spread * 0.6f;
                var at = hit.point + t * offset.x + b * offset.y;
                var go = MakeGib(GibKind.Brain, at + hit.normal * 0.02f, out var rb);
                if (go == null) break;
                var piece = go.AddComponent<GorePiece>();
                piece.Init(rb, null);
                piece.StickNow(at, hit.normal);
                Track(go);
            }
        }

        private void Track(GameObject go)
        {
            _pieces.Enqueue(go);
            while (_pieces.Count > MaxPieces)
            {
                var old = _pieces.Dequeue();
                if (old != null) Destroy(old);
            }
        }

        private static int BodyLayer(Player player)
        {
            foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr != null && smr.enabled) return smr.gameObject.layer;
            }
            return player.gameObject.layer;
        }

        private Body Find(Player player) => player != null && _bodies.TryGetValue(player.Id, out var b) ? b : null;

        private Body GetBody(Player player)
        {
            if (!_bodies.TryGetValue(player.Id, out var b))
            {
                b = new Body { Player = player };
                _bodies[player.Id] = b;
            }
            return b;
        }

        /// <summary>
        /// Keeps collapsed bones collapsed (an animator or a pose driver may write scale), and gives a
        /// body back whole when EFT reuses or removes it.
        /// </summary>
        private void LateUpdate()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Gore);
            if (_bodies.Count == 0) return;
            _release.Clear();
            foreach (var pair in _bodies)
            {
                var body = pair.Value;
                var p = body.Player;
                bool gone = p == null || p.gameObject == null || !p.gameObject.activeInHierarchy ||
                            (p.HealthController != null && p.HealthController.IsAlive);
                if (gone)
                {
                    _release.Add(pair.Key);
                    continue;
                }
                foreach (var (bone, scale) in body.Scaled)
                {
                    if (bone == null) continue;
                    var want = scale * Collapsed;
                    if ((bone.localScale - want).sqrMagnitude > 1e-8f) bone.localScale = want;
                }
            }
            foreach (var id in _release)
            {
                Restore(_bodies[id]);
                _bodies.Remove(id);
            }
        }

        private static void Restore(Body body)
        {
            bool changed = body.Scaled.Count > 0 || body.Attached.Count > 0 || body.Meshes.Count > 0;
            for (int i = body.Scaled.Count - 1; i >= 0; i--)
            {
                var (bone, scale) = body.Scaled[i];
                if (bone != null) bone.localScale = scale;
            }
            body.Scaled.Clear();
            foreach (var go in body.Attached)
            {
                if (go != null) Destroy(go);
            }
            body.Attached.Clear();
            // Whole skins again. EFT's HeadSkin swaps its mesh itself when the face cover changes: only
            // put ours back where our copy is still on.
            foreach (var (renderer, original, copy) in body.Meshes)
            {
                if (renderer != null && renderer.sharedMesh == copy) renderer.sharedMesh = original;
                if (copy != null) Destroy(copy);
            }
            body.Meshes.Clear();
            body.HeadBurst = false;
            body.HelmetThrown = false;
            if (changed && body.Player != null && Plugin.LogBlood.Value) Plugin.Log.LogInfo($"[Gore] {body.Player.Profile?.Nickname}: body restored (released by EFT or raid end)");
        }
    }
}
