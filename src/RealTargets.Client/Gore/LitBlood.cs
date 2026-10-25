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
    /// <summary>Which brightness slider a blood material follows (blood-3; GroundEft blood-5: ground blood on EFT's own shader).</summary>
    internal enum BloodSurface { Bodies, Ground, Gore, GroundEft }

    /// <summary>
    /// What ground blood (pools, drops, splats, prints, drags) is drawn with in the Lit decal mode (blood-5, ADR 0016).
    /// EftWet: EFT's p0 alpha-tested specular shader with its reflections, so the game's own lighting, exposure and sheen
    /// apply (our stock shader lit 2-4x brighter than EFT's surfaces, went black in shade and neon in sun, and pools read matte).
    /// EftMatte: EFT's p0 alpha-tested diffuse shader, stencil set like characters' (a test: does the map's grime stop showing
    /// on pools?), no sheen. Stock: Unity's legacy cutout specular shader as in 1.1.0 (the brightness slider tames it).
    /// </summary>
    internal enum GroundShaderMode { EftWet, EftMatte, Stock }

    internal static class LitBlood
    {
        /// <summary>Every lit blood material with its base colour and surface, for the live brightness sliders.</summary>
        private static readonly System.Collections.Generic.List<(Material m, Color baseColor, BloodSurface surface)> Registered =
            new System.Collections.Generic.List<(Material, Color, BloodSurface)>();

        /// <summary>The slider value for a surface (1 = the texture as drawn).</summary>
        internal static float Brightness(BloodSurface surface)
        {
            switch (surface)
            {
                case BloodSurface.Bodies: return Plugin.BrightnessBodies?.Value ?? 1f;
                case BloodSurface.Ground: return Plugin.BrightnessGround?.Value ?? 1f;
                case BloodSurface.GroundEft: return Plugin.BrightnessGroundEft?.Value ?? 1f;
                default: return Plugin.BrightnessGore?.Value ?? 1f;
            }
        }

        /// <summary>The slider ground blood follows now: its own one while it is drawn with EFT's shader.</summary>
        internal static BloodSurface GroundSurface => GroundIsEft ? BloodSurface.GroundEft : BloodSurface.Ground;

        /// <summary>A colour scaled by a surface's slider, alpha kept.</summary>
        internal static Color Scaled(Color c, BloodSurface surface)
        {
            float b = Brightness(surface);
            return new Color(c.r * b, c.g * b, c.b * b, c.a);
        }

        /// <summary>Sliders moved: every registered material's _Color again; pools and tinted mark chunks re-shade.</summary>
        internal static void ApplyBrightness()
        {
            foreach (var (m, baseColor, surface) in Registered)
            {
                if (m != null && m.HasProperty(ColorId)) m.SetColor(ColorId, Scaled(baseColor, surface));
            }
            foreach (var m in EftGroundMaterials) ApplyWetness(m);
            GroundBlood.ApplyDecalShader();
        }

        // ---- EFT's own shaders for ground blood (blood-5, ADR 0016) -------------------------------

        private const string EftWetName = "p0/Reflective/Bumped Specular SMap Transparent Cutoff";
        private const string EftMatteName = "p0/Cutout/Bumped Diffuse";
        private static bool _eftResolved;
        private static Shader _eftWet, _eftMatte;
        private static Material _eftWetTemplate, _eftMatteTemplate;
        private static Cubemap _cube;
        private static Texture2D _specMap;
        private static readonly System.Collections.Generic.List<Material> EftGroundMaterials = new System.Collections.Generic.List<Material>();
        /// <summary>Per EFT ground material: the template's sheen values the wetness slider scales.</summary>
        private static readonly System.Collections.Generic.Dictionary<Material, (float spec, Color reflect)> EftSheen =
            new System.Collections.Generic.Dictionary<Material, (float, Color)>();

        private static readonly int SpecularnessId = Shader.PropertyToID("_Specularness");
        private static readonly int ReflectColorId = Shader.PropertyToID("_ReflectColor");

        /// <summary>Ground blood is drawn with an EFT shader right now (Lit decal mode, an EFT mode picked, its shader found).</summary>
        internal static bool GroundIsEft
        {
            get
            {
                if (Plugin.DecalShader == null || Plugin.GroundShader == null) return false;
                if (Plugin.DecalShader.Value != DecalShaderMode.Lit) return false;
                ResolveEft();
                switch (Plugin.GroundShader.Value)
                {
                    case GroundShaderMode.EftWet: return _eftWet != null;
                    case GroundShaderMode.EftMatte: return _eftMatte != null;
                    default: return false;
                }
            }
        }

        /// <summary>
        /// Finds EFT's shaders and, for each, a material of the map that already uses it (its values are the game's own tuning
        /// for that shader: sheen, reflection, cubemap), once per session at raid load. Logged with every value.
        /// </summary>
        private static void ResolveEft()
        {
            if (_eftResolved) return;
            _eftResolved = true;
            _eftWet = Supported(EftWetName);
            _eftMatte = Supported(EftMatteName);
            try
            {
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null || m.shader == null) continue;
                    if (_eftWet != null && m.shader == _eftWet && (_eftWetTemplate == null || (_eftWetTemplate.GetTexture("_Cube") == null && m.GetTexture("_Cube") != null)))
                        _eftWetTemplate = m;
                    else if (_eftMatte != null && m.shader == _eftMatte && _eftMatteTemplate == null)
                        _eftMatteTemplate = m;
                }
                if (_eftWetTemplate == null || _eftWetTemplate.GetTexture("_Cube") == null)
                {
                    foreach (var c in Resources.FindObjectsOfTypeAll<Cubemap>())
                    {
                        if (c == null) continue;
                        if (_cube == null || c.name.Contains("cubemap")) _cube = c;
                        if (c.name.Contains("matte")) break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Shader] template search: {ex.Message}");
            }
            Plugin.Log.LogInfo($"[Shader] ground blood EFT shaders: wet {(_eftWet != null ? "found" : "missing")} (template {(_eftWetTemplate != null ? _eftWetTemplate.name : "none")}" +
                               $"{(_cube != null ? $", cubemap {_cube.name}" : "")}), matte {(_eftMatte != null ? "found" : "missing")} (template {(_eftMatteTemplate != null ? _eftMatteTemplate.name : "none")})");
            DumpMaterial("wet template", _eftWetTemplate);
            DumpMaterial("matte template", _eftMatteTemplate);
        }

        private static Shader Supported(string name)
        {
            var s = Shader.Find(name);
            return s != null && s.isSupported ? s : null;
        }

        /// <summary>
        /// A ground blood material on EFT's shader for <paramref name="mode"/>, or null (not found: the caller keeps the stock
        /// one). Texture RGB x colour = albedo, texture alpha x colour alpha against <paramref name="cutoff"/>, as the stock
        /// cutout material. Its own normal map is flat; the map's material is only the source of the shader's other values.
        /// </summary>
        internal static Material EftGround(GroundShaderMode mode, Texture texture, Color color, string name, float cutoff)
        {
            ResolveEft();
            var shader = mode == GroundShaderMode.EftWet ? _eftWet : mode == GroundShaderMode.EftMatte ? _eftMatte : null;
            if (shader == null) return null;
            var template = mode == GroundShaderMode.EftWet ? _eftWetTemplate : _eftMatteTemplate;
            var m = template != null ? new Material(template) : new Material(shader);
            m.name = name;
            m.hideFlags = HideFlags.HideAndDontSave;
            EftParams[m] = (mode, texture, color, cutoff, template != null);
            ApplyEftOverrides(m);
            return Register(m, color, BloodSurface.GroundEft);
        }

        /// <summary>What each EFT ground material was made for, to re-apply our overrides after a late template copy.</summary>
        private static readonly System.Collections.Generic.Dictionary<Material, (GroundShaderMode mode, Texture texture, Color color, float cutoff, bool templated)> EftParams =
            new System.Collections.Generic.Dictionary<Material, (GroundShaderMode, Texture, Color, float, bool)>();

        /// <summary>The map's own reflection cubemap, read off the first body material at the first hit (blood-5b).</summary>
        private static Texture _mapCube;
        private static bool _lateResolved;
        private static Texture2D _flatNormal;

        /// <summary>
        /// Our settings on an EFT ground material (over a map material's copy, or the shader's defaults).
        /// blood-5b (blood-5 test: EftWet white in shade, red only in direct sun; EftMatte black up close at any brightness,
        /// dark red from a distance): the normal map was set to null, i.e. the shader's declared default, which read as a
        /// normal pointing sideways (no light from above, view-angle dependent), and with no map material loaded at raid
        /// load the guessed reflection (a metal cubemap at 0.25) lit what the light didn't. Now an explicit flat normal map,
        /// the map's own cubemap and a low guessed reflection.
        /// </summary>
        private static void ApplyEftOverrides(Material m)
        {
            if (!EftParams.TryGetValue(m, out var p)) return;
            m.mainTexture = p.texture;
            m.mainTextureScale = Vector2.one;
            m.mainTextureOffset = Vector2.zero;
            if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", FlatNormal());
            if (m.HasProperty("_BumpTiling")) m.SetFloat("_BumpTiling", 1f);
            if (m.HasProperty(CutoffId)) m.SetFloat(CutoffId, p.cutoff);
            // No rain ripples on a pool, not a skinned mesh, and characters' stencil (the map's deferred decals skip those).
            if (m.HasProperty("_USERAIN")) m.SetFloat("_USERAIN", 0f);
            if (m.HasProperty("_SkinnedMeshMaterial")) m.SetFloat("_SkinnedMeshMaterial", 0f);
            if (m.HasProperty("_StencilType")) m.SetFloat("_StencilType", 1f);
            // blood-5c test for EftWet: the map material copied for it has its diffuse texture in _SpecMap and a gloss texture in
            // _MainTex, so this shader may take its colour from _SpecMap (our flat grey map would explain the white). Our texture
            // goes in both (pools: per renderer, BloodPool.Shade).
            if (m.HasProperty("_SpecMap")) m.SetTexture("_SpecMap", p.mode == GroundShaderMode.EftWet && p.texture != null ? p.texture : SpecMap());
            if (!p.templated && p.mode == GroundShaderMode.EftWet)
            {
                // No map material to copy: a wet but dim sheen (the skin material's 1.5 / white reflection lit pools white).
                if (m.HasProperty(SpecId)) m.SetColor(SpecId, Color.white);
                if (m.HasProperty("_Glossness")) m.SetFloat("_Glossness", 1.5f);
                if (m.HasProperty(SpecularnessId)) m.SetFloat(SpecularnessId, 0.6f);
                if (m.HasProperty(ReflectColorId)) m.SetColor(ReflectColorId, new Color(0.06f, 0.06f, 0.06f, 0.5f));
                if (m.HasProperty("_SpecVals")) m.SetVector("_SpecVals", new Vector4(1f, 2f, 0f, 0f));
                if (m.HasProperty("_DefVals")) m.SetVector("_DefVals", new Vector4(1f, 0.7f, 0f, 0f));
            }
            if (m.HasProperty("_Cube") && (!p.templated || m.GetTexture("_Cube") == null))
            {
                var cube = _mapCube != null ? _mapCube : _cube;
                if (cube != null) m.SetTexture("_Cube", cube);
            }
            // After the map's decals queued before the deferred pass ends (as the stock cutout material, blood-2).
            m.renderQueue = 2500;
            if (m.HasProperty(ColorId)) m.SetColor(ColorId, Scaled(p.color, BloodSurface.GroundEft));
            if (m.HasProperty(SpecularnessId) || m.HasProperty(ReflectColorId))
            {
                EftSheen[m] = (m.HasProperty(SpecularnessId) ? m.GetFloat(SpecularnessId) : 0f, m.HasProperty(ReflectColorId) ? m.GetColor(ReflectColorId) : Color.black);
                if (!EftGroundMaterials.Contains(m)) EftGroundMaterials.Add(m);
                ApplyWetness(m);
            }
        }

        /// <summary>
        /// blood-5b: at the first hit the map is loaded (at raid load it wasn't: the blood-5 log said "template none"). Takes
        /// the map's cubemap from the first body material, looks for map materials on the two shaders again and copies a
        /// found one onto the ground materials (then our overrides again). Once per session.
        /// </summary>
        internal static void LateResolve(Component body)
        {
            if (_lateResolved || !_eftResolved) return;
            _lateResolved = true;
            try
            {
                if (body != null)
                {
                    foreach (var r in body.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        var bm = r != null ? r.sharedMaterial : null;
                        if (bm != null && bm.HasProperty("_Cube") && bm.GetTexture("_Cube") != null)
                        {
                            _mapCube = bm.GetTexture("_Cube");
                            break;
                        }
                    }
                }
                foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (mat == null || mat.shader == null || EftParams.ContainsKey(mat)) continue;
                    if (_eftWet != null && mat.shader == _eftWet && (_eftWetTemplate == null || (_eftWetTemplate.GetTexture("_Cube") == null && mat.GetTexture("_Cube") != null)))
                        _eftWetTemplate = mat;
                    else if (_eftMatte != null && mat.shader == _eftMatte && _eftMatteTemplate == null)
                        _eftMatteTemplate = mat;
                }
                foreach (var pair in new System.Collections.Generic.List<Material>(EftParams.Keys))
                {
                    var p = EftParams[pair];
                    var template = p.mode == GroundShaderMode.EftWet ? _eftWetTemplate : _eftMatteTemplate;
                    if (!p.templated && template != null)
                    {
                        pair.CopyPropertiesFromMaterial(template);
                        pair.shaderKeywords = template.shaderKeywords;
                        EftParams[pair] = (p.mode, p.texture, p.color, p.cutoff, true);
                    }
                    ApplyEftOverrides(pair);
                }
                Plugin.Log.LogInfo($"[Shader] ground blood at the first hit: map cubemap {(_mapCube != null ? _mapCube.name : "none")}, wet template " +
                                   $"{(_eftWetTemplate != null ? _eftWetTemplate.name : "none")}, matte template {(_eftMatteTemplate != null ? _eftMatteTemplate.name : "none")}");
                DumpMaterial("wet template", _eftWetTemplate);
                DumpMaterial("matte template", _eftMatteTemplate);
                foreach (var m in EftParams.Keys) DumpMaterial("ground blood", m);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Shader] late ground blood setup: {ex.Message}");
            }
        }

        /// <summary>
        /// A flat tangent-space normal map that reads flat under Unity's UnpackNormal (x = r x a, y = g) and under plain AG
        /// unpacking alike: RGBA (1, 0.5, 1, 0.5), linear (sRGB sampling would move 0.5 to 0.21).
        /// </summary>
        private static Texture2D FlatNormal()
        {
            if (_flatNormal != null) return _flatNormal;
            _flatNormal = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { name = "RealTargetsFlatNormal", hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(1f, 0.5f, 1f, 0.5f);
            _flatNormal.SetPixels(px);
            _flatNormal.Apply(false, false);
            return _flatNormal;
        }

        /// <summary>`Ground blood wetness (EFT shader)`: scales the copied sheen and reflection.</summary>
        private static void ApplyWetness(Material m)
        {
            if (m == null || !EftSheen.TryGetValue(m, out var sheen)) return;
            float w = Plugin.WetnessGroundEft?.Value ?? 1f;
            if (m.HasProperty(SpecularnessId)) m.SetFloat(SpecularnessId, sheen.spec * w);
            if (m.HasProperty(ReflectColorId)) m.SetColor(ReflectColorId, new Color(sheen.reflect.r * w, sheen.reflect.g * w, sheen.reflect.b * w, sheen.reflect.a));
        }

        /// <summary>A flat specular/gloss map: the same sheen all over (what each channel means in EFT's shader is unknown).</summary>
        private static Texture2D SpecMap()
        {
            if (_specMap != null) return _specMap;
            _specMap = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "RealTargetsBloodSpec", hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[16];
            // blood-5b: was 0.6/0.8 (part of the white in shade).
            for (int i = 0; i < px.Length; i++) px[i] = new Color(0.4f, 0.4f, 0.4f, 0.6f);
            _specMap.SetPixels(px);
            _specMap.Apply(false, false);
            return _specMap;
        }

        /// <summary>Every property value of a material (textures by name), for the log.</summary>
        private static void DumpMaterial(string what, Material m)
        {
            if (m == null || m.shader == null) return;
            try
            {
                var shader = m.shader;
                var values = new System.Collections.Generic.List<string>();
                for (int i = 0; i < shader.GetPropertyCount(); i++)
                {
                    string p = shader.GetPropertyName(i);
                    switch (shader.GetPropertyType(i))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color: values.Add($"{p}={m.GetColor(p)}"); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector: values.Add($"{p}={m.GetVector(p)}"); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture: values.Add($"{p}={(m.GetTexture(p) != null ? m.GetTexture(p).name : "none")}"); break;
                        default: values.Add($"{p}={m.GetFloat(p):0.###}"); break;
                    }
                }
                Plugin.Log.LogInfo($"[Shader] {what} {m.name} ({shader.name}, queue {m.renderQueue}, keywords {string.Join(" ", m.shaderKeywords)}): {string.Join(", ", values)}");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogInfo($"[Shader] {what} dump: {ex.Message}");
            }
        }

        private static Material Register(Material m, Color baseColor, BloodSurface surface)
        {
            if (m == null) return null;
            Registered.Add((m, baseColor, surface));
            if (m.HasProperty(ColorId)) m.SetColor(ColorId, Scaled(baseColor, surface));
            return m;
        }

        /// <summary>
        /// blood-3: what EFT's own shaders take, for moving blood onto them later (our stock shaders light 2-4x brighter than
        /// EFT's characters). Logs each candidate's properties once, and the first body material's values (DumpBody).
        /// </summary>
        internal static void DumpEftShaders()
        {
            if (_dumped) return;
            _dumped = true;
            foreach (var name in new[] { "p0/Reflective/Bumped Specular SMap Transparent Cutoff", "p0/Reflective/Bumped Specular SMap_Decal",
                         "p0/Reflective/Bumped Specular SMap", "p0/Cutout/Bumped Diffuse" })
            {
                try
                {
                    var shader = Shader.Find(name);
                    if (shader == null)
                    {
                        Plugin.Log.LogInfo($"[Shader] {name}: not found");
                        continue;
                    }
                    var props = new System.Collections.Generic.List<string>();
                    int n = shader.GetPropertyCount();
                    for (int i = 0; i < n; i++) props.Add($"{shader.GetPropertyName(i)}:{shader.GetPropertyType(i)}");
                    Plugin.Log.LogInfo($"[Shader] {name}: supported {shader.isSupported}, queue {shader.renderQueue}, {n} properties: {string.Join(", ", props)}");
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogInfo($"[Shader] {name}: {ex.Message}");
                }
            }
            // blood-5: every p0 shader the game has loaded, to pick from next time without guessing names.
            try
            {
                var names = new System.Collections.Generic.SortedSet<string>();
                foreach (var s in Resources.FindObjectsOfTypeAll<Shader>())
                {
                    if (s != null && s.name.StartsWith("p0/")) names.Add(s.name);
                }
                Plugin.Log.LogInfo($"[Shader] loaded p0 shaders ({names.Count}): {string.Join(", ", names)}");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogInfo($"[Shader] p0 list: {ex.Message}");
            }
        }

        private static bool _dumped, _dumpedBody;

        /// <summary>The first body material on EFT's p0 shader: its property values (textures by name), once per session.</summary>
        internal static void DumpBody(Renderer r)
        {
            if (_dumpedBody || r == null) return;
            var m = r.sharedMaterial;
            if (m == null || m.shader == null || !m.shader.name.StartsWith("p0/")) return;
            _dumpedBody = true;
            try
            {
                var shader = m.shader;
                var values = new System.Collections.Generic.List<string>();
                for (int i = 0; i < shader.GetPropertyCount(); i++)
                {
                    string p = shader.GetPropertyName(i);
                    switch (shader.GetPropertyType(i))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color: values.Add($"{p}={m.GetColor(p)}"); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector: values.Add($"{p}={m.GetVector(p)}"); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture: values.Add($"{p}={(m.GetTexture(p) != null ? m.GetTexture(p).name : "none")}"); break;
                        default: values.Add($"{p}={m.GetFloat(p):0.###}"); break;
                    }
                }
                Plugin.Log.LogInfo($"[Shader] body material {m.name} ({shader.name}, queue {m.renderQueue}, keywords {string.Join(" ", m.shaderKeywords)}): {string.Join(", ", values)}");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogInfo($"[Shader] body material dump: {ex.Message}");
            }
        }
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
        internal static Material Transparent(Texture texture, Color color, float gloss, int queue, string name, float cutoff = 0.35f,
            BloodSurface surface = BloodSurface.Ground)
        {
            Resolve();
            if (_transparent == null) return null;
            var m = new Material(_transparent) { mainTexture = texture, name = name, hideFlags = HideFlags.HideAndDontSave };
            Configure(m, color, gloss);
            if (m.HasProperty(CutoffId)) m.SetFloat(CutoffId, cutoff);
            if (!IsCutout) m.renderQueue = queue;
            return Register(m, color, surface);
        }

        /// <summary>Opaque lit material (stump caps, gibs).</summary>
        internal static Material Opaque(Texture texture, Color color, float gloss, string name)
        {
            Resolve();
            if (_opaque == null) return null;
            var m = new Material(_opaque) { mainTexture = texture, name = name, hideFlags = HideFlags.HideAndDontSave };
            Configure(m, color, gloss);
            return Register(m, color, BloodSurface.Gore);
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
