using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using Game.Gameplay.Menu;

namespace Game.UI.Menu
{
    /// <summary>
    /// 退出对局事务协调器（Gate A-3，2026-09-08 P0 追加复审 §1.3）：
    /// 把 GameplayMenuView 的 async void 外围收敛为薄 UI 入口，可等待的事务主体提取为依赖
    /// 注入式协调器——Begin（幂等闸/关菜单/离线就地回大厅）、后端 leave（有界超时）、
    /// 清本地 Room、Complete（服务器权威离开→停连接→清 NetworkLaunchContext→回大厅）四步
    /// 全部经委托注入，EditMode 无需网络/后端即可驱动成功 / HTTP 失败 / 超时 / 异常 /
    /// 重复点击各路径。
    /// 幂等语义：一次协调器实例只承载一个事务——进行中或已完成的 RunAsync 一律拒绝重入
    ///（生产中每次点击新建实例，实例生命周期=单次事务）。
    /// 「不得误清/误断已被新会话替代的状态」（Day4 残余审计 P1-1）：以注入的连接/房间会话
    /// generation（AccountSession.ConnectionGeneration，仅真正 create/join/reconnect 递增）
    /// 为代际身份——代际未变才执行 ClearRoom 与 Complete（生产 complete=停连接→清
    /// NetworkLaunchContext→回大厅）；代际已推进=superseded，旧事务只标记并结束。
    /// </summary>
    public sealed class LeaveTransactionCoordinator
    {
        /// <summary>后端 leave 有界超时默认值（§6 三.2：超时后本地照常返回——DS 掉线端点兜底收敛）。</summary>
        public const float DefaultTimeoutSeconds = 5f;

        private readonly Func<GameplayMenuController.LeaveTransactionOutcome> begin;
        private readonly Func<CancellationToken, Task<ApiResult<object>>> leaveApi;
        private readonly Func<long> generationProvider;
        private readonly Action clearRoom;
        private readonly Action complete;
        private readonly TimeSpan timeout;
        private readonly Action<string> warn;

        public LeaveTransactionCoordinator(
            Func<GameplayMenuController.LeaveTransactionOutcome> begin,
            Func<CancellationToken, Task<ApiResult<object>>> leaveApi,
            Func<long> generationProvider,
            Action clearRoom,
            Action complete,
            TimeSpan timeout,
            Action<string> warn)
        {
            this.begin = begin ?? throw new ArgumentNullException(nameof(begin));
            this.leaveApi = leaveApi;
            this.generationProvider = generationProvider;
            this.clearRoom = clearRoom;
            this.complete = complete ?? throw new ArgumentNullException(nameof(complete));
            this.timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(DefaultTimeoutSeconds);
            this.warn = warn;
        }

        /// <summary>事务进行中（重复点击闸：真实网络下 leave 未返回期间的第二次点击）。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>事务已终结（本协调器实例一次性；场景切换后由新视图新建实例）。</summary>
        public bool HasCompleted { get; private set; }

        /// <summary>
        /// 事务被新会话代际替代（连接/房间会话 generation 已推进）：本地清理、停连接、
        /// 清 NetworkLaunchContext 与导航全部跳过——生产注入的 complete 恰好承载这四项，
        /// 跳过 complete 即全部跳过，旧事务绝不触碰新会话的连接与上下文。
        /// （D4 残余审计 P1-1：generation 由 AccountSession.ConnectionGeneration 单调推进，
        /// 仅真正 create/join/reconnect 递增；同房间快照刷新不推进、退出照常完成。）
        /// </summary>
        public bool Superseded { get; private set; }

        /// <summary>
        /// 运行退出事务。返回 false = 未产生任何副作用（重复点击 / Begin 闸拒绝）。
        /// 代际未变：清本地 Room + Complete（停连接→清 NetworkLaunchContext→回大厅由注入方负责）。
        /// 代际已推进（新会话）：标记 Superseded 并结束，ClearRoom/Complete 均不调用。
        /// </summary>
        public async Task<bool> RunAsync()
        {
            if (IsRunning || HasCompleted) return false;
            var generationAtBegin = generationProvider?.Invoke() ?? 0L;
            var step = begin();
            if (step == GameplayMenuController.LeaveTransactionOutcome.Rejected) return false;
            if (step == GameplayMenuController.LeaveTransactionOutcome.ReturnNow)
            {
                HasCompleted = true; // 离线/本地服：begin 内部已就地回大厅并清上下文
                return true;
            }

            IsRunning = true;
            try
            {
                if (leaveApi != null)
                {
                    using var cts = new CancellationTokenSource(timeout);
                    try
                    {
                        var request = leaveApi(cts.Token);
                        // A transport which ignores cancellation must not strand the exit flow.
                        if (await Task.WhenAny(request, Task.Delay(timeout)).ConfigureAwait(true) != request)
                        {
                            _ = request.ContinueWith(t => { _ = t.Exception; },
                                TaskContinuationOptions.OnlyOnFaulted);
                            throw new OperationCanceledException();
                        }
                        var result = await request.ConfigureAwait(true);
                        if (result == null || !result.Success)
                            warn?.Invoke($"[LeaveTransaction] 后端 leave 未成功 code={(result != null ? result.Code : "null")}（本地照常返回；DS 掉线端点兜底）");
                    }
                    catch (OperationCanceledException)
                    {
                        warn?.Invoke($"[LeaveTransaction] 后端 leave 有界超时 {timeout.TotalSeconds:0}s（本地照常返回；DS 掉线端点兜底）");
                    }
                    catch (Exception exception)
                    {
                        warn?.Invoke("[LeaveTransaction] 后端 leave 异常（本地照常返回；DS 掉线端点兜底）: " + exception.Message);
                    }
                }
            }
            finally
            {
                var generationNow = generationProvider?.Invoke() ?? 0L;
                if (generationNow != generationAtBegin)
                {
                    // D4 残余审计 P1-1：会话代际已推进（真正的新 create/join/reconnect）——旧事务
                    // 只标记并结束。complete 承载 StopConnection/Clear(NetworkLaunchContext)/导航，
                    // 跳过它即不触碰新会话的连接与上下文。
                    Superseded = true;
                    warn?.Invoke("[LeaveTransaction] 退出事务 superseded（会话代际已推进）——跳过本地清理、停连接与导航");
                }
                else
                {
                    clearRoom?.Invoke();
                    complete();
                }
                IsRunning = false;
                HasCompleted = true;
            }
            return true;
        }
    }
}
