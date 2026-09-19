using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// I4a/P4 两段权威命中决策矩阵（Docs/26 §5 + R10 审计修正语义）：
    /// ① 枪口进墙（身体→枪口受阻）→ UseBodySegmentHit——【不豁免】枪口验证（旧语义放行伸墙射击，已修）；
    /// ② 枪口路径候选点前遮挡改判、同点容差；
    /// ③ 距离比较同源于枪口（candidateDistanceFromMuzzle）——旧实现枪口起量 vs 相机起量不可比，已修；
    /// ④ 逻辑枪口/身体锚点推导。
    /// （射线编排本身走 Physics——TwoStageHitPhysicsTests 锁定真实墙角/门框/伸墙/霰弹。）
    /// </summary>
    public sealed class TwoStageHitResolverTests
    {
        [Test]
        public void MuzzleInsideWall_BodySegmentHit_IsChosen_NotExempted()
        {
            // R10：伸墙（枪口进墙）时必须改判身体段遮挡命中——旧规则"信任相机候选"放行伸墙射击
            var decision = TwoStageHitResolver.Decide(
                cameraHit: true, candidateDistanceFromMuzzle: 60f,
                muzzleInsideWall: true, muzzleHit: true, muzzleDist: 2f);
            Assert.That(decision, Is.EqualTo(TwoStageHitResolver.TwoStageDecision.UseBodySegmentHit));
        }

        [Test]
        public void MuzzleInsideWall_EvenCleanMuzzlePath_CannotUseCameraCandidate()
        {
            var decision = TwoStageHitResolver.Decide(
                cameraHit: true, candidateDistanceFromMuzzle: 60f,
                muzzleInsideWall: true, muzzleHit: false, muzzleDist: 60f);
            Assert.That(decision, Is.EqualTo(TwoStageHitResolver.TwoStageDecision.UseBodySegmentHit));
        }

        [Test]
        public void MuzzleOccludedBeforeCandidate_UsesMuzzleHit()
        {
            var decision = TwoStageHitResolver.Decide(
                cameraHit: true, candidateDistanceFromMuzzle: 59f,
                muzzleInsideWall: false, muzzleHit: true, muzzleDist: 10f);
            Assert.That(decision, Is.EqualTo(TwoStageHitResolver.TwoStageDecision.UseMuzzleHit));
        }

        [Test]
        public void MuzzlePathReachesCandidate_UsesCameraCandidate()
        {
            var decision = TwoStageHitResolver.Decide(
                cameraHit: true, candidateDistanceFromMuzzle: 60f,
                muzzleInsideWall: false, muzzleHit: true, muzzleDist: 59.99f);
            Assert.That(decision, Is.EqualTo(TwoStageHitResolver.TwoStageDecision.UseCameraCandidate),
                "枪口路径命中点≈候选点（同点容差内）不是遮挡");
        }

        [Test]
        public void OcclusionEpsilon_BoundaryIsExclusive()
        {
            const float candidate = 60f;
            // 恰好差 0.05（容差）→ 同点；差 0.06 → 遮挡（两段距离同以枪口为原点）
            Assert.That(TwoStageHitResolver.Decide(true, candidate, false, true, candidate - TwoStageHitResolver.OcclusionEpsilon),
                Is.EqualTo(TwoStageHitResolver.TwoStageDecision.UseCameraCandidate));
            Assert.That(TwoStageHitResolver.Decide(true, candidate, false, true, candidate - TwoStageHitResolver.OcclusionEpsilon - 0.01f),
                Is.EqualTo(TwoStageHitResolver.TwoStageDecision.UseMuzzleHit));
        }

        [Test]
        public void CameraMissed_MuzzleHitBeforeFarPoint_UsesMuzzleHit()
        {
            // 相机未命中：候选点 = 相机远点；枪口到远点 100.5，枪口路径 30 处被挡 → 遮挡
            var decision = TwoStageHitResolver.Decide(
                cameraHit: false, candidateDistanceFromMuzzle: 100.5f,
                muzzleInsideWall: false, muzzleHit: true, muzzleDist: 30f);
            Assert.That(decision, Is.EqualTo(TwoStageHitResolver.TwoStageDecision.UseMuzzleHit));
        }

        [Test]
        public void CameraMissed_MuzzleAlsoClean_UsesCameraCandidateFarPoint()
        {
            var decision = TwoStageHitResolver.Decide(
                cameraHit: false, candidateDistanceFromMuzzle: 100.5f,
                muzzleInsideWall: false, muzzleHit: false, muzzleDist: 100.5f);
            Assert.That(decision, Is.EqualTo(TwoStageHitResolver.TwoStageDecision.UseCameraCandidate));
        }

        [Test]
        public void CameraOriginDistanceIsNotComparedAgainstMuzzlePath()
        {
            // R10 锁定：枪口起量与相机起量差 0.6m+（枪口偏移），旧实现 0.05 容差下误判遮挡的形态
            // 必须以"枪口→候选"为候选距离：枪口命中 59.7、枪口→候选 60.0（同源）→ 同点 → 相机候选
            var decision = TwoStageHitResolver.Decide(
                cameraHit: true, candidateDistanceFromMuzzle: 60f,
                muzzleInsideWall: false, muzzleHit: true, muzzleDist: 59.97f);
            Assert.That(decision, Is.EqualTo(TwoStageHitResolver.TwoStageDecision.UseCameraCandidate));
        }

        [Test]
        public void LogicalMuzzleAndBodyAnchor_DeriveFromRootPose()
        {
            var root = new Vector3(10f, 1f, 20f);
            var forward = Vector3.forward;
            var muzzle = TwoStageHitResolver.LogicalMuzzle(root, forward, Vector3.up);
            Assert.That(muzzle, Is.EqualTo(root + Vector3.up * TwoStageHitResolver.MuzzleUpMeters
                + Vector3.forward * TwoStageHitResolver.MuzzleForwardMeters));
            Assert.That(TwoStageHitResolver.BodyAnchor(root), Is.EqualTo(root + Vector3.up * TwoStageHitResolver.BodyAnchorUpMeters));
        }
    }
}
