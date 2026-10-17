using System;

namespace RealTargets.Diagnostics
{
    /// <summary>Which part of the mod a timing scope belongs to (0.10.10: the worst frame names its cause).</summary>
    internal enum PerfArea { Hits, Bleed, Particles, Wounds, Ground, Gore, Impacts }

#if PUBLIC
    /// <summary>
    /// Public build (Stompa, 2026-10-17: performance tools stripped): `Perf.Measure` scopes stay in the
    /// per-frame entry points but cost nothing. The dev build's Perf.cs replaces this.
    /// </summary>
    internal static class Perf
    {
        internal readonly struct Scope : IDisposable
        {
            public void Dispose() { }
        }

        internal static Scope Measure(PerfArea area) => default;
    }
#endif
}
