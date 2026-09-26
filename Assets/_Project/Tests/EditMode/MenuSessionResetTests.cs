using Game.Gameplay.Menu;
using Game.Gameplay.Network;
using NUnit.Framework;

namespace Game.Tests.EditMode
{
    public sealed class MenuSessionResetTests
    {
        [Test] public void NewSessionClearsEndAndTransitionLocksForThreeRounds()
        {
            var machine=new GameplayMenuStateMachine();
            for(int round=0;round<3;round++)
            {
                machine.ForceCloseAndLock(round%2==0?GameplayMenuLockReason.MatchEnded:GameplayMenuLockReason.SceneTransition);
                Assert.That(machine.TryConsumeEscape(),Is.False);
                machine.ResetForNewSession();
                Assert.That(machine.LockReason,Is.EqualTo(GameplayMenuLockReason.None));
                Assert.That(machine.TryConsumeEscape(),Is.True);
                Assert.That(machine.State,Is.EqualTo(GameplayMenuState.PauseMenu));
                machine.TryResume();
            }
        }
        [TestCase("Arena")][TestCase("Map_Stackyard")][TestCase("Map_Depot55")][TestCase("Map_Ridgeline")][TestCase("Map_TrainingYard")][TestCase("Map_NightRelay")]
        public void EveryRegisteredBattleMapSupportsMenu(string scene) => Assert.That(GameMapCatalog.IsGameplayScene(scene),Is.True);
        [TestCase("Lobby")][TestCase("Login")]
        public void NonBattleScenesDoNotMountMenu(string scene) => Assert.That(GameMapCatalog.IsGameplayScene(scene),Is.False);
    }
}
