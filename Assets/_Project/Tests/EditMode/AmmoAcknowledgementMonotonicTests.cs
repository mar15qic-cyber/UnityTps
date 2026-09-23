using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class AmmoAcknowledgementMonotonicTests
    {
        [Test]
        public void DuplicateOldRequest_CannotMoveLastProcessedShotBackward()
        {
            var root = new GameObject("AmmoAckMonotonic");
            try
            {
                var authority = root.AddComponent<NetworkCombatAuthority>();
                authority.RecordProcessedShotForTests(42);
                authority.RecordProcessedShotForTests(17); // duplicate/late reject

                Assert.That(authority.LastProcessedShotRequestId, Is.EqualTo(42u));
                Assert.That(authority.SnapshotAcknowledgementForTests, Is.EqualTo(42u),
                    "RejectShot must publish the monotonic ACK, never the stale rejected request id");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
