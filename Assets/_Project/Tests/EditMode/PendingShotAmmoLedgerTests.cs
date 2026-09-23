using Game.Gameplay.Weapon;
using NUnit.Framework;

namespace Game.Gameplay.Tests
{
    public sealed class PendingShotAmmoLedgerTests
    {
        [Test]
        public void RejectN_ReplaysOnlyNPlusOneAndNPlusTwo()
        {
            var ledger = new PendingShotAmmoLedger();
            ledger.Register(10, "rifle", 7);
            ledger.Register(11, "rifle", 7);
            ledger.Register(12, "rifle", 7);

            int debt = ledger.ConsumeAcknowledgedAndCountRemaining("rifle", 7, 10);

            Assert.That(debt, Is.EqualTo(2), "rejecting N must not refund N+1/N+2 already predicted locally");
        }

        [Test]
        public void DifferentWeaponAcknowledgement_DoesNotCountItsShotsAsCurrentWeaponDebt()
        {
            var ledger = new PendingShotAmmoLedger();
            ledger.Register(21, "rifle", 3);
            ledger.Register(22, "pistol", 3);

            Assert.That(ledger.ConsumeAcknowledgedAndCountRemaining("pistol", 3, 20), Is.EqualTo(1));
            Assert.That(ledger.ConsumeAcknowledgedAndCountRemaining("rifle", 3, 20), Is.EqualTo(1));
        }

        [Test]
        public void NewLife_DropsOldRequestsAndCannotReplayThem()
        {
            var ledger = new PendingShotAmmoLedger();
            ledger.Register(30, "rifle", 4);
            ledger.Register(31, "rifle", 5);

            Assert.That(ledger.ConsumeAcknowledgedAndCountRemaining("rifle", 5, 0), Is.EqualTo(1));
            Assert.That(ledger.Count, Is.EqualTo(1), "only the current-life pending request remains");
        }

        [Test]
        public void OlderAcknowledgement_IsUnableToIncreaseRemainingDebt()
        {
            var ledger = new PendingShotAmmoLedger();
            ledger.Register(40, "rifle", 1);
            ledger.Register(41, "rifle", 1);

            Assert.That(ledger.ConsumeAcknowledgedAndCountRemaining("rifle", 1, 41), Is.Zero);
            Assert.That(ledger.ConsumeAcknowledgedAndCountRemaining("rifle", 1, 40), Is.Zero,
                "late ACK must not resurrect an already processed prediction");
        }
    }
}
