using System;

namespace RealTargets.Diagnostics
{
    /// <summary>Which part of the mod a timing scope belongs to (0.10.10: the worst frame names its cause).</summary>
    /// <remarks>Overlay (perf-3): the dev build's debug overlay (IMGUI, OnGUI), counted from 1.2.0.</remarks>
    internal enum PerfArea { Hits, Bleed, Particles, Wounds, Ground, Gore, Impacts, Overlay }

    /// <summary>
    /// perf-4 (perf-3: one 5-7 ms frame of ours per 10 s, area Ground or Bleed, cause not named): named steps inside the
    /// areas, timed with <c>Perf.Part</c>. They may nest (a body bake inside a stain), so they don't add up to the area; a
    /// frame of ours over 3 ms logs them as `[Perf] slow frame` (dev build).
    /// perf-5: a body bake split into its skinning (BodySkin) and its collider cooking; perf-6: no cooking (own ray test),
    /// BodyCopy = reading the baked vertices, SpawnPatch = a patch laid on the surface (its 25 rays, weights, batch).
    /// </summary>
    internal enum PerfPart
    {
        BleedTick, PoolFeed, Drips, Stains, BakeSetup, BodyBake, BakeDestroy, PatchUpload,
        PoolTick, PoolRaycasts, PoolMesh, PoolColour, PoolUpload, MarkUpload,
        BodySkin, BodyCopy, SpawnPatch,
    }

#if PUBLIC
    /// <summary>
    /// Public build (Stompa, 2026-10-17: performance tools stripped): `Perf.Measure` and `Perf.Part` scopes stay in the
    /// per-frame code but cost nothing. The dev build's Perf.cs replaces this.
    /// </summary>
    internal static class Perf
    {
        internal readonly struct Scope : IDisposable
        {
            public void Dispose() { }
        }

        internal static Scope Measure(PerfArea area) => default;
        internal static Scope Part(PerfPart part) => default;
    }
#endif
}
