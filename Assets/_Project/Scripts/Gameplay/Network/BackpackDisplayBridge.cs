using System;
using System.Reflection;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// CF 三背包展示桥（2026-09-30 Phase D）：itemId → 展示名的跨程序集解析。
    /// asmdef 边界：WeaponAssetCatalog（itemId→definition→DisplayName）属 Game.UI——
    /// Game.Presentation 只引 Game.Gameplay，经本桥反射调用 Game.UI.NetworkPlayerLoadoutApplier
    /// .ResolveDisplayName（同 NetworkLoadoutPolicy 的反向依赖规避惯例）。
    /// 解析链路断裂（桥缺失/异常）时返回 itemId 原样——诚实呈现，绝不猜名字。
    /// </summary>
    public static class BackpackDisplayBridge
    {
        private const string ApplierTypeName = "Game.UI.NetworkPlayerLoadoutApplier, Game.UI";
        private const string MethodName = "ResolveDisplayName";
        private static MethodInfo _method;
        private static bool _resolved;

        public static string WeaponDisplayName(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return "—";
            var method = ResolveMethod();
            if (method == null) return itemId;
            try
            {
                return method.Invoke(null, new object[] { itemId }) as string ?? itemId;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BackpackDisplayBridge] 展示名解析异常：{exception.InnerException?.Message ?? exception.Message}");
                return itemId;
            }
        }

        private static MethodInfo ResolveMethod()
        {
            if (_method != null) return _method;
            if (!_resolved)
            {
                _resolved = true;
                var type = Type.GetType(ApplierTypeName);
                if (type == null)
                {
                    Debug.LogWarning($"[BackpackDisplayBridge] 找不到 {ApplierTypeName}——展示名桥未装配");
                    return null;
                }
                _method = type.GetMethod(MethodName, BindingFlags.Public | BindingFlags.Static);
                if (_method == null) Debug.LogWarning($"[BackpackDisplayBridge] {MethodName} 方法缺失");
            }
            return _method;
        }
    }
}
