# Networking

## 权威、预测与协议

Game.Gameplay 直接引用 FishNet.Runtime。PlayerNetworkAdapter 负责网络玩家连接和数据交付，NetworkCombatAuthority 处理服务器战斗，WeaponController / WeaponRuntime 驱动 Owner 预测与本地玩法，NetworkWeaponState 与 AuthoritativeAmmoSnapshot 同步权威结果。

Owner 可以即时呈现开火 / 换弹，但伤害、接受的开火、弹药和配装切换由 DS 决定。观察者使用同步状态驱动 TP 动作，不能从动画事件推导弹药事实。

当前协议为 `fps-net-v25`，定义在 GameProtocolIdentity。创建房间的 ClientProtocolId 冻结到房间，入房及 DS 租用校验兼容；票据消费再次验证。不要修改 DTO / RPC 后继续沿用同一协议或只替换客户端。协议变化需要双端和热更地图一起构建并验证。

## 票据与生命周期

玩家用 JWT 调用 API，服务器用 X-Server-Key 调用控制面。创建等待房间不等于马上连接 DS；开始阶段租实例并签票。DS 消费一次性票据得到身份、队伍和配装，不信任客户端自行提交的持有物。

输入和快照携带序号 / 生命代际等约束，防止旧生命输入在复活后生效。死亡、切枪、退出和复活要清理未确认动作；Owner 补弹同时更新预测运行时。终局由 DS 上报，API 以 matchId 幂等保存，结算失败的补偿记录按实例分开。

## 背包同步与资格

Owner 完成网络生成后交付三背包清单，BackpackDisplayBridge 的到达事件刷新已打开 UI。显示索引为 1–3，内部索引 0–2，入场默认背包 1。

| 模式 | 区域 | 本生命规则 |
|---|---|---|
| TDM | 本队出生簇中心，XZ 半径 15 米 | 必须在区；同时离开过且实际开火 / 投掷过则锁定，复活恢复 |
| KillRace | 本人最近出生点，XZ 半径 8 米 | 离开过或实际开火 / 投掷过即锁定，返回不恢复，复活恢复 |

资格由服务器维护。BackpackSwitchPolicy.EvaluateAvailability 先判断区域和生命锁存，随后才考虑 busy；PendingShots 仅限制仍有资格的玩家。BackpackSwitchHudView 在失去资格时隐藏图标，不能因换弹再次显示；不可换时 B 只给短暂提示。存活、比赛状态、配装数据和当前背包等闸门仍由权威端重验。

## 换弹快照

逐发换弹快照包含阶段、进度和 Generation，客户端预测与服务器使用同一状态模型。装填完成立即转移一发；火力中断请求、关仓和延迟确认均可能跨多帧，不能用固定总时长替代每阶段状态。

修改网络代码后检查乱序、重复、延迟确认、切枪 / 死亡中断和复活恢复；调试记录同时标识账号、生命、请求序号、代际及双端构建身份。测试方法见 [Testing](Testing.md)，部署身份见 [Build and Release](../Operations/BuildAndRelease.md)。
