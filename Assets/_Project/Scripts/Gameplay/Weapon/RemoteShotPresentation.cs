using Game.Core;
using Game.Gameplay.Combat;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 逐发权威结果的线上表现载荷（ADS 审计 S2，2026-09-19）：把 WeaponShot 的表现必需字段
    /// 压平成 FishNet 可序列化的原始类型/数组——服务器用它做 ①ObserversShot 全端广播
    /// （旧 4 标量广播丢弃了散布/弹丸/最终点，OnRemoteShot 因此无人能正确消费）与
    /// ②Owner 定向确认（TargetShotConfirmed：接受→纠偏 / 拒绝→撤销预测表现）。
    /// 铁律：只传 Gameplay 语义的原始数据；Target 引用不进 wire（远端无对象映射）。
    /// </summary>
    [System.Serializable]
    public struct RemoteShotPresentation
    {
        /// <summary>服务器侧 shotRequestId（客户端请求路径）；0=Host/离线本地权威路径。</summary>
        public uint ShotRequestId;
        public string WeaponId;
        public bool IsSuppressed;
        public Vector3 Origin;          // AimOrigin（权威射线原点）
        public Vector3 FiredDirection;  // 散布后主弹道方向
        public Vector3 FinalPoint;      // 主 Result.Point（命中点或远点）
        public Vector3 FinalNormal;     // 主 Result.Normal
        public bool FinalHit;           // 主 Result.Hit
        public bool FinalHitCharacter;  // surface kind only; no client-side damage authority
        public int DamageAmount;         // server-confirmed HP loss across the entire shot
        public HitBodyRegion BodyRegion; // highest-priority damaged region in this shot
        public uint LifeEpoch;           // shooter's generation; stale confirmations are discarded
        /// <summary>弹丸数（1=单发）。霰弹 &gt;1 时 PelletPoints/PelletHits 逐弹丸对应。</summary>
        public int PelletCount;
        public Vector3[] PelletPoints;  // null=单发
        public bool[] PelletHits;       // 与 PelletPoints 等长；null=单发
        public Vector3[] PelletNormals;
        public bool[] PelletCharacters;
        public int[] PelletDamageAmounts;

        public static RemoteShotPresentation FromShot(in WeaponShot shot, uint shotRequestId)
        {
            var pellets = shot.Pellets;
            bool multi = pellets != null && pellets.Length > 1;
            var dto = new RemoteShotPresentation
            {
                ShotRequestId = shotRequestId,
                Origin = shot.Origin,
                FiredDirection = shot.FiredDirection,
                FinalPoint = shot.Result.Point,
                FinalNormal = shot.Result.Normal,
                FinalHit = shot.Result.Hit,
                FinalHitCharacter = shot.Result.Target != null,
                DamageAmount = shot.Result.DamageAmount,
                BodyRegion = shot.Result.BodyRegion,
                PelletCount = multi ? pellets.Length : 1,
                PelletPoints = null,
                PelletHits = null
            };
            if (multi)
            {
                dto.DamageAmount = 0;
                dto.BodyRegion = HitBodyRegion.Torso;
                dto.PelletPoints = new Vector3[pellets.Length];
                dto.PelletHits = new bool[pellets.Length];
                dto.PelletNormals = new Vector3[pellets.Length];
                dto.PelletCharacters = new bool[pellets.Length];
                dto.PelletDamageAmounts = new int[pellets.Length];
                for (int i = 0; i < pellets.Length; i++)
                {
                    dto.PelletPoints[i] = pellets[i].Point;
                    dto.PelletHits[i] = pellets[i].Hit;
                    dto.PelletNormals[i] = pellets[i].Normal;
                    dto.PelletCharacters[i] = pellets[i].Target != null;
                    dto.PelletDamageAmounts[i] = pellets[i].DamageAmount;
                    dto.DamageAmount += pellets[i].DamageAmount;
                    if (pellets[i].DamageAmount > 0 && pellets[i].BodyRegion == HitBodyRegion.Head)
                        dto.BodyRegion = HitBodyRegion.Head;
                }
            }
            return dto;
        }
    }
}
