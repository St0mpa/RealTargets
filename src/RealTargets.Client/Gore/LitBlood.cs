using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Lit materials for blood and gore (0.10.3). Until 0.10.2 every colour pass was an unlit particle
    /// shader tinted by our own light estimate (SceneLight). EFT's auto exposure then lifted that fixed
    /// colour far above the really-lit scene: every pool logged "light 255" and blood glowed in dark
    /// rooms (0.10.2 test: "way too bright compared to anything else"). These materials use Unity's
    /// built-in lit shaders instead, so the map's own lights, probes, shadows and exposure decide how
    /// bright blood is; the specular ones give fresh blood a wet sheen.
    /// Shader names were read from the game's globalgamemanagers shader list (2026-10-14); each is
    /// checked with Shader.Find + isSupported and the result logged once. Missing = old unlit path.
    /// Cutout first: alpha-tested surface shaders render in EFT's deferred pass, so they take the sun's
    /// shadows like the floor they lie on. Alpha-blended lit shaders render forward and in Unity's
    /// built-in pipeline never receive shadows: indoors the sun through a window would light blood
    /// behind walls (the 0.9.0 red glow, again). Cost: hard edges (fine for blood) and no soft halo.
    /// </summary>
    internal static class LitBlood
    {
        private static readonly string[] TransparentNames =
        {
            // 0.10.3 test: Cutout/Specular is not in the build (got Cutout/Diffuse: matte). Bumped Specular
            // (flat default normal map) may be; it would give fresh blood its wet highlight.
            "Legacy Shaders/Transparent/Cutout/Specular", "Legacy Shaders/Transparent/Cutout/Bumped Specular",
            "Legacy Shaders/Transparent/Cutout/Diffuse",
            "Legacy Shaders/Transparent/Specular", "Legacy Shaders/Transparent/Diffuse",
        };
        private static readonly string[] OpaqueNames = { "Legacy Shaders/Specular", "Legacy Shaders/Diffuse", "Standard" };

        private static bool _resolved;
        private static Shader _transparent, _opaque;
        internal static string TransparentName { get; private set; } = "none";
        internal static string OpaqueName { get; private set; } = "none";

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int SpecId = Shader.PropertyToID("_SpecColor");
        private static readonly int ShininessId = Shader.PropertyToID("_Shininess");
        private static readonly int GlossId = Shader.PropertyToID("_Glossiness");

        private static readonly int CutoffId = Shader.PropertyToID("_Cutoff");

        /// <summary>The lit decal shader is alpha-tested (deferred, shadowed): drawn alone, no multiply pass under it.</summary>
        internal static bool IsCutout
        {
            get
            {
                Resolve();
                return TransparentName.Contains("Cutout");
            }
        }

        /// <summary>A lit decal shader (cutout or alpha-blended) exists (ground blood, wound colour pass).</summary>
        internal static bool HasTransparent
        {
            get
            {
                Resolve();
                return _transparent != null;
            }
        }

        /// <summary>A lit opaque shader exists (stumps, gibs).</summary>
        internal static bool HasOpaque
        {
            get
            {
                Resolve();
                return _opaque != null;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            _transparent = Find(TransparentNames, out var tn);
            _opaque = Find(OpaqueNames, out var on);
            TransparentName = tn;
            OpaqueName = on;
            Plugin.Log.LogInfo($"Lit blood: transparent {TransparentName}, opaque {OpaqueName}");
        }

        private static Shader Find(string[] names, out string found)
        {
            foreach (var name in names)
            {
                var shader = Shader.Find(name);
                if (shader != null && shader.isSupported)
                {
                    found = name;
                    return shader;
                }
            }
            found = "none";
            return null;
        }

        /// <summary>
        /// Lit decal material: texture RGB x <paramref name="color"/> is the albedo, texture alpha x
        /// color alpha the coverage (cutout: drawn where it exceeds <paramref name="cutoff"/>).
        /// <paramref name="gloss"/> 0..1 (wet fresh blood ~0.7). <paramref name="queue"/> applies to the
        /// alpha-blended fallback only; cutout keeps the AlphaTest queue (deferred).
        /// </summary>
        internal static Material Transparent(Texture texture, Color color, float gloss, int queue, string name, float cutoff = 0.35f)
        {
            Resolve();
            if (_transparent == null) return null;
            var m = new Material(_transparent) { mainTexture = texture, name = name, hideFlags = HideFlags.HideAndDontSave };
            Configure(m, color, gloss);
            if (m.HasProperty(CutoffId)) m.SetFloat(CutoffId, cutoff);
            if (!IsCutout) m.renderQueue = queue;
            return m;
        }

        /// <summary>Opaque lit material (stump caps, gibs).</summary>
        internal static Material Opaque(Texture texture, Color color, float gloss, string name)
        {
            Resolve();
            if (_opaque == null) return null;
            var m = new Material(_opaque) { mainTexture = texture, name = name, hideFlags = HideFlags.HideAndDontSave };
            Configure(m, color, gloss);
            return m;
        }

        private static void Configure(Material m, Color color, float gloss)
        {
            if (m.HasProperty(ColorId)) m.SetColor(ColorId, color);
            // Blinn-Phong: a dim white highlight, tight when wet.
            if (m.HasProperty(SpecId)) m.SetColor(SpecId, new Color(0.35f, 0.33f, 0.33f, 1f) * gloss);
            if (m.HasProperty(ShininessId)) m.SetFloat(ShininessId, Mathf.Lerp(0.2f, 0.7f, gloss));
            if (m.HasProperty(GlossId)) m.SetFloat(GlossId, gloss * 0.8f);
        }

        /// <summary>Renderer settings for a lit blood/gore object: lit by nearby probes, no shadows cast.</summary>
        internal static void Setup(Renderer r)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = true;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;
        }

        /// <summary>Per-renderer colour on one material slot (pools drying darker).</summary>
        internal static void SetColor(Renderer r, int slot, Color color, MaterialPropertyBlock block)
        {
            if (r == null) return;
            r.GetPropertyBlock(block, slot);
            block.SetColor(ColorId, color);
            r.SetPropertyBlock(block, slot);
        }
    }
}
