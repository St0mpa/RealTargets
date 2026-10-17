using System.Collections.Generic;
using DrakiaXYZ.BigBrain.Brains;
using EFT;

namespace RealTargets.Lab
{
    /// <summary>
    /// Test-lab dummies stand still, upright, facing where the player stood when they spawned:
    /// pure targets. Priority 97 is above every SAIN layer (SAIN's highest normal layer is Flashed at 85).
    /// </summary>
    internal sealed class DummyLayer : CustomLayer
    {
        internal const int LayerPriority = 97;

        // SAIN's brain list (decompiled AIBrains) plus a few vanilla extras; unknown names are harmless.
        private static readonly List<string> Brains = new List<string>
        {
            "Assault", "BigPipe", "BirdEye", "BossBoar", "BossBully", "BossGluhar", "BossKojaniy", "BossKolontay",
            "BossPartisan", "BossSanitar", "BossTest", "CursAssault", "FlBoar", "FlBoarCl", "FlBoarSt", "FlKlnAslt",
            "FollowerBully", "FollowerGluharAssault", "FollowerGluharProtect", "FollowerGluharScout", "FollowerKojaniy",
            "FollowerSanitar", "Gifter", "HelperAgro", "Killa", "KillaAgro", "Knight", "KolonSec", "Obdolbs", "Oni",
            "PMC", "PmcBear", "PmcUsec", "PrizrakSt", "SctPredvst", "SectantPriest", "SectantWarrior", "Tagilla",
            "TagillaAgro", "TagillaFollower", "ExUsec", "ArenaFighter",
        };

        internal static void Register()
        {
            BrainManager.AddCustomLayer(typeof(DummyLayer), Brains, LayerPriority);
        }

        public DummyLayer(BotOwner botOwner, int priority)
            : base(botOwner, priority)
        {
        }

        public override string GetName() => "RealTargetsLabDummy";

        public override bool IsActive()
        {
            var player = BotOwner?.GetPlayer;
            return player != null && TestLab.Find(player) != null;
        }

        public override Action GetNextAction() => new Action(typeof(DummyLogic), "Lab dummy");

        public override bool IsCurrentActionEnding() => false;
    }

    internal sealed class DummyLogic : CustomLogic
    {
        public DummyLogic(BotOwner botOwner)
            : base(botOwner)
        {
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (BotOwner.Mover == null)
            {
                return;
            }

            BotOwner.StopMove();
            BotOwner.SetPose(1f);

            var dummy = TestLab.Find(BotOwner.GetPlayer);
            if (dummy != null && BotOwner.Steering != null)
            {
                BotOwner.Steering.LookToPoint(dummy.LookAt);
            }
        }
    }
}
