using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Fine blood mist (0.10.0): a cloud of soft billboards that bursts out of an exit wound (or a
    /// cough), expands, drifts a little and fades in under a second, as high-speed footage of
    /// through-shots shows. One world-space particle system, no collisions. Alpha-blended, so each
    /// puff's colour is scaled by the light where it happens (SceneLight) to keep it from glowing
    /// in dark rooms.
    /// </summary>
    internal sealed class BloodMist : MonoBehaviour
    {
        private const int MaxParticles = 600;
        private static readonly Color Blood = new Color(0.5f, 0.04f, 0.035f, 1f);

        internal static BloodMist Instance { get; private set; }

        private ParticleSystem _system;
        private bool _failed;

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        internal void ResetRaid()
        {
            if (_system != null) _system.Clear();
        }

        /// <summary>
        /// A puff at <paramref name="position"/> moving along <paramref name="direction"/>.
        /// size = start radius of each billboard (m), density 0..1 = opacity, count = billboards.
        /// <paramref name="burst"/> (0.10.4): x faster and 1/x as long-lived, for an explosive cloud (head
        /// or limb coming apart). At 1 the puffs brake to 0.4 m/s and hang ~1 s, which read as slow-motion
        /// particles drifting out of an exploding head (0.10.3 test).
        /// </summary>
        internal void Puff(Vector3 position, Vector3 direction, float size, float density, int count, float burst = 1f)
        {
            if (!Plugin.VisualsMist.Value || !Ensure()) return;

            // 0.10.1: floor raised from 0.3 (in Factory's light the mist came out near black).
            // 0.10.3: top end lowered (SceneLight saturates on Factory, unlit colours glowed).
            float light = Mathf.Lerp(0.55f, 0.8f, SceneLight.At(position, -direction));
            var color = Blood * light;
            color.a = Mathf.Clamp01(density);
            var dir = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.up;

            var p = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < count; i++)
            {
                p.position = position + Random.insideUnitSphere * size * 0.4f;
                // Most of the cloud keeps going with the bullet; some spreads sideways. 0.10.2: faster and
                // wider, so an exit cloud gets out from behind the body (0.10.1 stopped within ~20 cm, hidden).
                p.velocity = (dir * Random.Range(1.5f, 5f) + Random.insideUnitSphere * 1.6f) * Mathf.Lerp(0.6f, 1.3f, Mathf.Clamp01(size / 0.25f)) * burst;
                p.startSize = size * Random.Range(0.7f, 1.4f);
                p.startLifetime = Random.Range(0.5f, 1.1f) * Mathf.Lerp(0.8f, 1.5f, Mathf.Clamp01(size / 0.25f)) / Mathf.Max(1f, burst);
                p.rotation = Random.Range(0f, 360f);
                p.startColor = color;
                _system.Emit(p, 1);
            }
        }

        /// <summary>Creates the mist system while the raid loads (0.10.10).</summary>
        internal void Warm() => Ensure();

        private bool Ensure()
        {
            if (_system != null) return true;
            if (_failed) return false;

            var shader = GroundBlood.FindShader(true, out var shaderName);
            if (shader == null || !shaderName.Contains("Alpha Blended"))
            {
                _failed = true;
                Plugin.Log.LogWarning($"Blood mist: no alpha-blended particle shader ({shaderName}).");
                return false;
            }

            var go = new GameObject("RealTargets.BloodMist");
            go.transform.SetParent(transform, false);
            _system = go.AddComponent<ParticleSystem>();
            _system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = _system.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0.04f;
            main.maxParticles = MaxParticles;
            main.startSpeed = 0f;
            main.playOnAwake = false;
            main.loop = false;

            var emission = _system.emission;
            emission.enabled = false;
            var shape = _system.shape;
            shape.enabled = false;

            // Air drag: the cloud stops within ~0.3 s.
            var limit = _system.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = 0.4f;
            limit.dampen = 0.06f;

            var sizeOver = _system.sizeOverLifetime;
            sizeOver.enabled = true;
            sizeOver.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.6f), new Keyframe(0.3f, 1.6f), new Keyframe(1f, 2.6f)));

            var colorOver = _system.colorOverLifetime;
            colorOver.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.7f, 0.6f, 0.6f), 1f) },
                new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.55f, 0.25f), new GradientAlphaKey(0f, 1f) });
            colorOver.color = gradient;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var material = new Material(shader) { mainTexture = MistTexture(), hideFlags = HideFlags.HideAndDontSave };
            // Soft particles: fade only within ~8 cm of a surface (0.10.0 used 3 = ~30 cm, which ate the
            // puffs born at the body).
            if (material.HasProperty("_InvFade")) material.SetFloat("_InvFade", 12f);
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            renderer.sharedMaterial = material;

            _system.Play();
            Plugin.Log.LogInfo($"Blood mist ready ({shaderName}).");
            return true;
        }

        /// <summary>Soft, clumpy round puff: white RGB (tinted per particle), noisy falling-off alpha.</summary>
        private static Texture2D MistTexture()
        {
            const int size = 64;
            var pixels = new Color[size * size];
            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    float x = (i + 0.5f) / size - 0.5f, y = (j + 0.5f) / size - 0.5f;
                    float r = Mathf.Sqrt(x * x + y * y) * 2f;
                    float noise = Mathf.PerlinNoise(x * 7f + 13f, y * 7f + 5f) * 0.6f + Mathf.PerlinNoise(x * 19f + 2f, y * 19f + 9f) * 0.4f;
                    float a = Mathf.Clamp01(1f - r) * Mathf.Clamp01(1f - r);
                    a *= Mathf.Lerp(0.45f, 1.15f, noise);
                    pixels[j * size + i] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
                }
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixels(pixels);
            tex.Apply(true, false);
            return tex;
        }
    }
}
