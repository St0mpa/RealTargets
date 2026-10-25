using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using Systems.Effects;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Physical blood: one world-space particle system per raid. Droplets get a velocity, fall
    /// under gravity and die on their first collision; where they land on the environment they leave
    /// our own GroundBlood marks (drips merge into spreading spots, spurts and exit sprays leave
    /// stretched splats; EFT's decal ring recycled them within a minute, 0.9.0 test). Droplets that
    /// hit a body just die (EFT's body painter was too coarse, 0.9.0). Visual only.
    /// EFT has no physical blood particles of its own; the material is borrowed from its body-hit
    /// effect when possible (see ResolveMaterial).
    /// </summary>
    internal sealed class BloodParticles : MonoBehaviour
    {
        private const int MaxParticles = 2500;
        // Token bucket: exit sprays land 20-60 drops at once, sustained bleeding far fewer.
        private const float EnvDecalsPerSecond = 25f;
        private const float EnvDecalBurst = 80f;

        internal static BloodParticles Instance { get; private set; }
        internal static string MaterialSource { get; private set; } = "none";
        /// <summary>perf-3 (dev split benchmark): no droplets are emitted while set. Never set in the public build.</summary>
        internal static bool PerfHidden = false;

        /// <summary>Droplets in flight, all systems (for the [Perf] line).</summary>
        internal int AliveCount
        {
            get
            {
                int n = 0;
                foreach (var system in _systems)
                {
                    if (system != null) n += system.particleCount;
                }
                return n;
            }
        }

        // One system per blood tone (1.2.0): a collision event carries no particle colour, so the tone of the mark a
        // droplet leaves comes from the system it flew in.
        // blood-3: plus a fine-spray system per tone (coughs, the fine part of an exit spray): its landings leave mm speckles.
        private readonly ParticleSystem[] _systems = new ParticleSystem[6];
        private readonly List<ParticleCollisionEvent> _events = new List<ParticleCollisionEvent>(32);
        private readonly List<Vector3> _recentSplats = new List<Vector3>(64);
        private float _envBudget = EnvDecalsPerSecond;
        private float _nextSplatPrune;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Particles);
            _envBudget = Mathf.Min(EnvDecalBurst, _envBudget + EnvDecalsPerSecond * Time.deltaTime);
            if (Time.time >= _nextSplatPrune)
            {
                _nextSplatPrune = Time.time + 2f;
                _recentSplats.Clear();
            }
        }

        internal void ResetRaid()
        {
            ClearDroplets();
            _recentSplats.Clear();
        }

        /// <summary>Removes every droplet in flight (raid reset; perf-3 split benchmark).</summary>
        internal void ClearDroplets()
        {
            foreach (var system in _systems)
            {
                if (system != null) system.Clear();
            }
        }

        /// <summary>Emits droplets: a spurt burst (fast, many) or a drip (slow, one), coloured by <paramref name="tone"/>.</summary>
        internal void Emit(Vector3 position, Vector3 velocity, float spreadDegrees, int count, float size, BloodTone tone = BloodTone.Venous,
            bool fine = false)
        {
            if (PerfHidden || !EnsureSystem())
            {
                return;
            }
            using var part = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.Drips);

            var system = _systems[(int)tone + (fine ? 3 : 0)] ?? _systems[0];
            // blood-5 (blood-4 test: flat bright squares glowing over a dark floor): the shader is unlit, so the colour follows
            // the light where the drops leave, as the mist does.
            var colour = BloodPalette.Droplet(tone) * Mathf.Lerp(0.45f, 0.85f, SceneLight.At(position, Vector3.up));
            colour.a = BloodPalette.Droplet(tone).a;
            var p = new ParticleSystem.EmitParams
            {
                applyShapeToPosition = false,
                startColor = colour,
            };

            for (int i = 0; i < count; i++)
            {
                var jitter = Quaternion.Euler(Random.Range(-spreadDegrees, spreadDegrees), Random.Range(-spreadDegrees, spreadDegrees), 0f);
                p.position = position + Random.insideUnitSphere * 0.004f;
                p.velocity = jitter * velocity * Random.Range(0.8f, 1.15f);
                p.startSize = size * Random.Range(0.6f, 1.3f);
                p.startLifetime = Random.Range(1.8f, 2.6f);
                system.Emit(p, 1);
            }
        }

        /// <summary>Creates the droplet system while the raid loads (0.10.10).</summary>
        internal void Warm() => EnsureSystem();

        private bool EnsureSystem()
        {
            if (_systems[0] != null)
            {
                return true;
            }

            var material = ResolveMaterial();
            for (int t = 0; t < _systems.Length; t++)
            {
                _systems[t] = Build((BloodTone)(t % 3), material, t >= 3);
            }
            Plugin.Log.LogInfo($"Blood particles ready (material: {MaterialSource}, {_systems.Length} tones).");
            return true;
        }

        private ParticleSystem Build(BloodTone tone, Material material, bool fine)
        {
            var go = new GameObject("RealTargets.Blood." + tone + (fine ? ".Fine" : ""));
            go.transform.SetParent(transform, false);
            var system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = system.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 1f;
            main.maxParticles = MaxParticles;
            main.startColor = BloodPalette.Droplet(tone);
            main.startSize = 0.012f;
            main.startLifetime = 2.2f;
            main.startSpeed = 0f;
            main.playOnAwake = false;
            main.loop = false;

            var emission = system.emission;
            emission.enabled = false;
            var shape = system.shape;
            shape.enabled = false;

            var collision = system.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.quality = ParticleSystemCollisionQuality.Medium;
            collision.bounce = 0f;
            collision.dampen = 1f;
            collision.lifetimeLoss = 1f;
            collision.radiusScale = 0.5f;
            collision.sendCollisionMessages = true;
            collision.collidesWith = LayersMaskController.HighPolyWithTerrainMask | LayersMaskController.HitColliderMask |
                                     (1 << LayersMaskController.DeadbodyLayer);

            // blood-5: stretched along the velocity (a falling drop reads as a short streak at 60 fps), not a static square.
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.012f;
            renderer.lengthScale = 1f;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterial = material;

            var receiver = go.AddComponent<BloodCollisionReceiver>();
            receiver.Owner = this;
            receiver.System = system;
            receiver.Tone = tone;
            receiver.Fine = fine;
            system.Play();
            return system;
        }

        /// <summary>Material: EFT's body-hit particle material, else built-in particle shaders.</summary>
        private static Material ResolveMaterial()
        {
            try
            {
                if (Singleton<Effects>.Instantiated)
                {
                    var effect = Singleton<Effects>.Instance.Get(MaterialType.Body);
                    var mediator = effect?.BasicParticleSystemMediator;
                    if (mediator?._particleSystems != null)
                    {
                        foreach (var ps in mediator._particleSystems)
                        {
                            var r = ps != null ? ps.GetComponent<ParticleSystemRenderer>() : null;
                            if (r != null && r.sharedMaterial != null)
                            {
                                MaterialSource = $"EFT body effect ({r.sharedMaterial.name}, {r.sharedMaterial.shader?.name})";
                                return new Material(r.sharedMaterial);
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Blood: borrowing EFT's material failed: {ex.Message}");
            }

            foreach (var name in new[] { "Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended", "Sprites/Default", "Hidden/Internal-Colored" })
            {
                var shader = Shader.Find(name);
                if (shader != null)
                {
                    MaterialSource = name + ", round drop sprite";
                    // White: the particle colour (per tone) tints it. blood-5: with no texture every drop was a full square
                    // (blood-4 test: "square boxes that fly"); HollywoodFX empties EFT's Body effect, so this is the usual path.
                    var m = new Material(shader) { color = Color.white, mainTexture = DropTexture(), hideFlags = HideFlags.HideAndDontSave };
                    if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
                    GroundBlood.NoSoftFade(m);
                    return m;
                }
            }

            MaterialSource = "none (no shader found)";
            return null;
        }

        /// <summary>A drop: a round, slightly soft disc (white RGB, tinted per particle), a little denser in the middle.</summary>
        private static Texture2D DropTexture()
        {
            const int size = 32;
            var pixels = new Color[size * size];
            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    float x = (i + 0.5f) / size - 0.5f, y = (j + 0.5f) / size - 0.5f;
                    float r = Mathf.Sqrt(x * x + y * y) * 2f;
                    float a = Mathf.Clamp01((1f - r) / 0.25f);
                    pixels[j * size + i] = new Color(1f, 1f, 1f, a * a * (3f - 2f * a));
                }
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "RealTargetsBloodDrop",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixels(pixels);
            tex.Apply(true, false);
            return tex;
        }

        internal void OnDropletCollision(ParticleSystem system, BloodTone tone, bool fine, GameObject other)
        {
            int count = system.GetCollisionEvents(other, _events);
            if (count == 0)
            {
                return;
            }

            if (other.GetComponent<BodyPartCollider>() != null)
            {
                return;
            }

            // Only the static world keeps a mark (0.10.3). Droplets also stop on corpses and flying gore
            // pieces (Deadbody layer); a mark left there stayed in mid-air once the gib flew on or the
            // corpse fell (0.10.2 test: "blood floating mid air" behind a head destroyed by a slug).
            if (other.layer == LayersMaskController.DeadbodyLayer)
            {
                return;
            }
            // 0.10.14: nor doors, hatches, container lids and windows (they open or break; the mark would hang in the air).
            if (GroundBlood.Movable(other.transform)) return;
            var body = other.GetComponentInParent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                return;
            }

            var ground = GroundBlood.Instance;
            if (ground == null)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                var e = _events[i];
                if (fine)
                {
                    // Fine spray: half the drops leave a speckle, at half the budget, never merged or spaced.
                    if (_envBudget < 0.5f || Random.value < 0.5f) continue;
                    _envBudget -= 0.5f;
                    ground.Speckle(e.intersection, e.normal, e.velocity, tone);
                    continue;
                }
                // Drops falling straight onto a floor leave drip marks; anything hitting a wall or
                // arriving with sideways speed (a spurt) splatters. Falling drops pass 2 m/s within
                // 0.2 s, so total speed alone can't tell them apart.
                var horizontal = new Vector2(e.velocity.x, e.velocity.z).magnitude;
                bool drip = e.normal.y > 0.7f && horizontal < 0.8f;
                // blood-5 (blood-4 test: a standing bleeder's drops never merged): a drip landing near a recent one is exactly
                // the drop that should join that spot, so drips skip the 5 cm spacing (GroundBlood.Drip merges them).
                if (_envBudget < 1f || (!drip && NearRecent(e.intersection)))
                {
                    continue;
                }

                _envBudget -= 1f;
                _recentSplats.Add(e.intersection);
                if (drip)
                {
                    ground.Drip(e.intersection, e.normal, tone);
                }
                else
                {
                    ground.Splat(e.intersection, e.normal, e.velocity, tone);
                }
            }
        }

        private bool NearRecent(Vector3 point)
        {
            foreach (var p in _recentSplats)
            {
                if ((p - point).sqrMagnitude < 0.0025f) return true; // 5 cm
            }
            return false;
        }
    }

    /// <summary>Unity sends particle collisions only to components on the particle system's GameObject.</summary>
    internal sealed class BloodCollisionReceiver : MonoBehaviour
    {
        internal BloodParticles Owner;
        internal ParticleSystem System;
        internal BloodTone Tone;
        internal bool Fine;

        private void OnParticleCollision(GameObject other)
        {
            // 0.10.10: droplets landing make the ground marks (and their light lookups); count it too.
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Particles);
            if (Owner != null && other != null)
            {
                try
                {
                    Owner.OnDropletCollision(System, Tone, Fine, other);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"Blood collision failed: {ex.Message}");
                }
            }
        }
    }
}
