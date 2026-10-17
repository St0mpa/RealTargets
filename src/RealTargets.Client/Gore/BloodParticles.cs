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

        private ParticleSystem _system;
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
            if (_system != null)
            {
                _system.Clear();
            }
            _recentSplats.Clear();
        }

        /// <summary>Emits droplets: a spurt burst (fast, many) or a drip (slow, one).</summary>
        internal void Emit(Vector3 position, Vector3 velocity, float spreadDegrees, int count, float size)
        {
            if (!EnsureSystem())
            {
                return;
            }

            var p = new ParticleSystem.EmitParams
            {
                applyShapeToPosition = false,
            };

            for (int i = 0; i < count; i++)
            {
                var jitter = Quaternion.Euler(Random.Range(-spreadDegrees, spreadDegrees), Random.Range(-spreadDegrees, spreadDegrees), 0f);
                p.position = position + Random.insideUnitSphere * 0.004f;
                p.velocity = jitter * velocity * Random.Range(0.8f, 1.15f);
                p.startSize = size * Random.Range(0.6f, 1.3f);
                p.startLifetime = Random.Range(1.8f, 2.6f);
                _system.Emit(p, 1);
            }
        }

        /// <summary>Creates the droplet system while the raid loads (0.10.10).</summary>
        internal void Warm() => EnsureSystem();

        private bool EnsureSystem()
        {
            if (_system != null)
            {
                return true;
            }

            var go = new GameObject("RealTargets.Blood");
            go.transform.SetParent(transform, false);
            _system = go.AddComponent<ParticleSystem>();
            _system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = _system.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 1f;
            main.maxParticles = MaxParticles;
            main.startColor = new Color(0.32f, 0.01f, 0.01f, 0.95f);
            main.startSize = 0.012f;
            main.startLifetime = 2.2f;
            main.startSpeed = 0f;
            main.playOnAwake = false;
            main.loop = false;

            var emission = _system.emission;
            emission.enabled = false;
            var shape = _system.shape;
            shape.enabled = false;

            var collision = _system.collision;
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

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = ResolveMaterial();

            go.AddComponent<BloodCollisionReceiver>().Owner = this;
            _system.Play();
            Plugin.Log.LogInfo($"Blood particles ready (material: {MaterialSource}).");
            return true;
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
                    MaterialSource = name;
                    return new Material(shader) { color = new Color(0.32f, 0.01f, 0.01f, 0.95f) };
                }
            }

            MaterialSource = "none (no shader found)";
            return null;
        }

        internal void OnDropletCollision(GameObject other)
        {
            int count = _system.GetCollisionEvents(other, _events);
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
                if (_envBudget < 1f || NearRecent(e.intersection))
                {
                    continue;
                }

                _envBudget -= 1f;
                _recentSplats.Add(e.intersection);

                // Drops falling straight onto a floor leave drip marks; anything hitting a wall or
                // arriving with sideways speed (a spurt) splatters. Falling drops pass 2 m/s within
                // 0.2 s, so total speed alone can't tell them apart.
                var horizontal = new Vector2(e.velocity.x, e.velocity.z).magnitude;
                bool floor = e.normal.y > 0.7f;
                if (floor && horizontal < 0.8f)
                {
                    ground.Drip(e.intersection, e.normal);
                }
                else
                {
                    ground.Splat(e.intersection, e.normal, e.velocity);
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

        private void OnParticleCollision(GameObject other)
        {
            // 0.10.10: droplets landing make the ground marks (and their light lookups); count it too.
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Particles);
            if (Owner != null && other != null)
            {
                try
                {
                    Owner.OnDropletCollision(other);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"Blood collision failed: {ex.Message}");
                }
            }
        }
    }
}
