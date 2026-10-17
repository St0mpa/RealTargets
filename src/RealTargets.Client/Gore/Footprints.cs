using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Bloody footprints (0.10.6, Stompa): anyone who walks through a wet pool (you or a bot) picks up blood on
    /// the soles and leaves boot prints after it, alternating feet ~0.75 m apart, fainter with every step
    /// (x0.85 per print, ~15 prints) and as the blood on the soles dries (half-life 90 s); a dried-out pool
    /// gives none. Prints are ordinary ground marks (GroundBlood: wet / drying / faint print cells, the left
    /// foot mirrored). Sampled every 0.1 s; nothing while prone or off the ground.
    /// </summary>
    internal sealed class Footprints : MonoBehaviour
    {
        private const float Tick = 0.1f;
        private const float Stride = 0.75f;
        private const float FootOffset = 0.09f;
        private const float PerPrint = 0.85f;
        private const float SoleHalfLife = 90f;
        private const float MinLoad = 0.06f;

        private sealed class Walker
        {
            public Vector3 Last;
            public bool HasLast;
            public float Travel;
            public float Load;
            public bool LeftNext;
            public int Prints;
            public float LoggedAt = -100f;
            public float SeenAt;
        }

        private readonly Dictionary<int, Walker> _walkers = new Dictionary<int, Walker>();
        private readonly List<int> _gone = new List<int>();
        private float _next;

        internal void ResetRaid() => _walkers.Clear();

        private void Update()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Ground);
            if (!Plugin.ModEnabled.Value || !Plugin.VisualsFootprints.Value || !Plugin.VisualsEnabled.Value || !Singleton<GameWorld>.Instantiated) return;
            float now = Time.time;
            if (now < _next) return;
            float dt = Tick + Mathf.Max(0f, now - _next);
            _next = now + Tick;
            var ground = GroundBlood.Instance;
            if (ground == null) return;

            var cam = Camera.main;
            float maxDistance = Plugin.VisualsDistance.Value;
            foreach (var player in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (player == null || !player.gameObject.activeInHierarchy) continue;
                var pos = player.Position;
                if (cam != null && (pos - cam.transform.position).sqrMagnitude > maxDistance * maxDistance) continue;
                try
                {
                    Step(player, pos, dt, now, ground);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"Footprints: {ex.Message}");
                }
            }

            // Forget walkers not seen for a while (dead, despawned).
            _gone.Clear();
            foreach (var pair in _walkers)
            {
                if (now - pair.Value.SeenAt > 5f) _gone.Add(pair.Key);
            }
            foreach (var id in _gone) _walkers.Remove(id);
        }

        private void Step(Player player, Vector3 pos, float dt, float now, GroundBlood ground)
        {
            if (!_walkers.TryGetValue(player.Id, out var w))
            {
                w = new Walker();
                _walkers[player.Id] = w;
            }
            w.SeenAt = now;

            float wet = ground.PoolWetness(pos);
            if (wet > 0f)
            {
                // Standing in it: soles soaked; the prints start where they step out.
                if (wet > w.Load + 0.1f && now - w.LoggedAt > 5f && Plugin.LogBlood.Value)
                {
                    w.LoggedAt = now;
                    Plugin.Log.LogInfo($"[Footprints] {player.Profile?.Nickname ?? "?"} stepped in blood (wet {wet:F2})");
                }
                w.Load = Mathf.Max(w.Load, wet);
                w.Prints = 0;
                w.Last = pos;
                w.HasLast = true;
                w.Travel = 0f;
                return;
            }

            w.Load *= Mathf.Pow(0.5f, dt / SoleHalfLife);
            if (w.Load < MinLoad || player.IsInPronePose)
            {
                w.Last = pos;
                w.HasLast = true;
                w.Travel = 0f;
                return;
            }
            if (!w.HasLast)
            {
                w.Last = pos;
                w.HasLast = true;
                return;
            }

            var move = pos - w.Last;
            move.y = 0f;
            float length = move.magnitude;
            // A teleport or a vehicle-like jump: start over.
            if (length > 3f)
            {
                w.Last = pos;
                w.Travel = 0f;
                return;
            }
            if (length < 0.01f) return;
            var dir = move / length;
            // Prints point where the walker FACES (0.10.8, Stompa: walking backwards or strafing, the toes still point
            // the view direction); the feet sit left and right of that facing. Strides count along the path.
            var facing = player.Transform.forward;
            facing.y = 0f;
            facing = facing.sqrMagnitude > 1e-4f ? facing.normalized : dir;
            var right = Vector3.Cross(Vector3.up, facing);
            // Distance along this segment at which the next stride completes.
            float along = Stride - w.Travel;
            w.Travel += length;
            while (w.Travel >= Stride && w.Load >= MinLoad)
            {
                w.Travel -= Stride;
                var at = w.Last + dir * Mathf.Clamp(along, 0f, length) + right * (w.LeftNext ? -FootOffset : FootOffset);
                along += Stride;
                if (Physics.Raycast(at + Vector3.up * 0.4f, Vector3.down, out var hit, 0.9f, LayersMaskController.HighPolyWithTerrainMask) && hit.normal.y > 0.6f &&
                    !GroundBlood.Movable(hit.collider))
                {
                    ground.Footprint(hit.point, hit.normal, facing, w.LeftNext, w.Load, w.Prints < 2);
                    w.Prints++;
                }
                w.LeftNext = !w.LeftNext;
                w.Load *= PerPrint;
            }
            w.Last = pos;
        }
    }
}
