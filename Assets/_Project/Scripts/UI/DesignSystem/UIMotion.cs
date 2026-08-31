using System;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Self-contained tween facade for the UI design system. DOTween lives in Assembly-CSharp
    /// (Demigiant ships without asmdefs) and is unreachable from Game.UI, so motion is driven by
    /// a tiny internal runner instead (see UIMotionDriver). All methods are null-safe and
    /// complete instantly in EditMode tests where no runner exists unless requested.
    /// </summary>
    public static class UIMotion
    {
        public enum Ease { OutQuad, OutBack, Linear }

        public sealed class Handle
        {
            public Func<float, float> EaseFunc;
            public float Duration;
            public float Elapsed;
            public Action<float> OnUpdate;
            public Action OnComplete;
            public bool Done;
        }

        /// <summary>All live handles; drained by UIMotionDriver each frame. Exposed for tests.</summary>
        public static readonly System.Collections.Generic.List<Handle> Pending = new();

        public static Handle Tween(float duration, Ease ease, Action<float> onUpdate, Action onComplete = null)
        {
            var handle = new Handle
            {
                Duration = Mathf.Max(0.0001f, duration),
                EaseFunc = Resolve(ease),
                OnUpdate = onUpdate,
                OnComplete = onComplete,
            };
            Pending.Add(handle);
            UIMotionDriver.EnsureExists();
            if (UIMotionDriver.Instance == null)
            {
                // EditMode without a runner: complete synchronously so tests observe the final state.
                handle.OnUpdate?.Invoke(1f);
                handle.OnComplete?.Invoke();
                handle.Done = true;
                Pending.Remove(handle);
            }
            return handle;
        }

        /// <summary>Page enter: fade CanvasGroup 0→1 and slide content up by PageSlidePixels.</summary>
        public static void FadeSlideIn(CanvasGroup group, RectTransform content)
        {
            if (group == null) return;
            var startY = content != null ? content.anchoredPosition.y : 0f;
            group.alpha = 0f;
            if (content != null) content.anchoredPosition = new Vector2(content.anchoredPosition.x, startY - UITheme.PageSlidePixels);
            Tween(UITheme.PageFadeSeconds, Ease.OutQuad, t =>
            {
                group.alpha = t;
                if (content != null)
                    content.anchoredPosition = new Vector2(content.anchoredPosition.x, Mathf.Lerp(startY - UITheme.PageSlidePixels, startY, t));
            });
        }

        /// <summary>Button press feedback: scale to PressScale and back.</summary>
        public static void PressPunch(RectTransform target)
        {
            if (target == null) return;
            Tween(UITheme.PressSeconds, Ease.OutQuad, t =>
            {
                if (target != null) target.localScale = Vector3.one * Mathf.Lerp(1f, UITheme.PressScale, t);
            }, () =>
            {
                if (target == null) return;
                Tween(UITheme.PressSeconds, Ease.OutBack, t =>
                {
                    if (target != null) target.localScale = Vector3.one * Mathf.Lerp(UITheme.PressScale, 1f, t);
                });
            });
        }

        /// <summary>Hover lift: translate up by HoverLiftPixels (reversible).</summary>
        public static void HoverLift(RectTransform target, bool enter)
        {
            if (target == null) return;
            var startY = target.anchoredPosition.y;
            var endY = enter ? startY + UITheme.HoverLiftPixels : startY - UITheme.HoverLiftPixels;
            Tween(UITheme.HoverSeconds, Ease.OutQuad, t =>
            {
                if (target != null)
                    target.anchoredPosition = new Vector2(target.anchoredPosition.x, Mathf.Lerp(startY, endY, t));
            });
        }

        /// <summary>Error feedback: horizontal shake.</summary>
        public static void Shake(RectTransform target, float amplitude = 8f, float duration = 0.3f)
        {
            if (target == null) return;
            var origin = target.anchoredPosition;
            Tween(duration, Ease.Linear, t =>
            {
                if (target == null) return;
                var offset = Mathf.Sin(t * Mathf.PI * 6f) * amplitude * (1f - t);
                target.anchoredPosition = origin + new Vector2(offset, 0f);
            }, () =>
            {
                if (target != null) target.anchoredPosition = origin;
            });
        }

        /// <summary>Roll a displayed number (coins/XP) between values.</summary>
        public static void RollNumber(int from, int to, float duration, Action<int> onValue)
        {
            if (onValue == null) return;
            Tween(duration, Ease.OutQuad, t => onValue(Mathf.RoundToInt(Mathf.Lerp(from, to, t))));
        }

        public static Func<float, float> Resolve(Ease ease)
        {
            switch (ease)
            {
                case Ease.OutQuad: return t => 1f - (1f - t) * (1f - t);
                case Ease.OutBack:
                    return t =>
                    {
                        const float c1 = 1.70158f;
                        const float c3 = c1 + 1f;
                        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
                    };
                default: return t => t;
            }
        }

        /// <summary>Advances all pending handles; called by UIMotionDriver.Update. Exposed for EditMode tests.</summary>
        public static void Tick(float deltaTime)
        {
            for (var i = Pending.Count - 1; i >= 0; i--)
            {
                var handle = Pending[i];
                if (handle == null || handle.Done)
                {
                    Pending.RemoveAt(i);
                    continue;
                }
                handle.Elapsed += deltaTime;
                var t = Mathf.Clamp01(handle.Elapsed / handle.Duration);
                try
                {
                    handle.OnUpdate?.Invoke(handle.EaseFunc != null ? handle.EaseFunc(t) : t);
                    if (t >= 1f)
                    {
                        handle.Done = true;
                        handle.OnComplete?.Invoke();
                        Pending.RemoveAt(i);
                    }
                }
                catch (Exception)
                {
                    handle.Done = true;
                    Pending.RemoveAt(i);
                    throw;
                }
            }
        }
    }
}
