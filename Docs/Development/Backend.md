# Backend：数据与 HTTP 契约

## 结构与认证

源码位于 fps-backend/src/UnityFps.Api。Controllers 负责 HTTP / 认证边界，Services 负责事务、业务校验与映射，AppDbContext 保存领域实体，Features/Contracts.cs 定义共享响应和请求。

注册 / 登录返回 AuthSessionDto：token、expiresAtUtc、profile、loadout、coins、backpacks。JWT 包含用户与 TokenVersion；再次登录顶替旧 token，旧会话返回 401。签名密钥、发行者和受众以配置为准。JWT 留在客户端会话内存，不把控制面密钥放进客户端。

玩家接口使用 `Authorization: Bearer <TOKEN>`；DS 接口使用 `X-Server-Key: <SERVER_KEY>`。错误通过 ProblemDetails 的 code 提供稳定业务原因。JWT 无效通常为 401，参数错误为 400，业务冲突为 409；具体状态以 Controller / Service 为准。

## 玩家接口导航

以下路径按当前 Controller 整理，Swagger 仅在 Development 开启。DTO 字段以 Contracts.cs 为准，不从历史文档复制旧请求。

| 方法 | 路径 | 用途 |
|---|---|---|
| POST | /api/auth/register、/api/auth/login | 匿名注册 / 登录 |
| GET | /api/profile | 档案 |
| GET | /api/inventory | 钱包与持有物 |
| GET | /api/shop/catalog | 目录、价格、开放等级与持有状态 |
| POST | /api/shop/purchases | 幂等购买 |
| GET | /api/loadout/backpacks | 三背包全集与 activeIndex |
| GET / PUT | /api/loadout?backpack=1 | 单背包武器及投掷槽 |
| GET / PUT | /api/loadout/attachments?backpack=1 | 单背包配件 |
| GET | /api/loadout/compatibility | 配件兼容矩阵 |
| GET / PUT | /api/settings | 玩家偏好 |
| GET | /api/maps | 地图目录 |
| GET | /api/pass、/api/achievements | 通行证及成就 |
| GET | /api/matches/history | 战绩 |
| POST | /api/matches | 旧玩家结算入口已退役，410 |

购买请求为 itemId、quantity（当前仅 1）、idempotencyKey（8–96 字符）。重复同一幂等请求不得再次扣款；型号为永久解锁，不按投掷使用次数购买。

配装查询 backpack 为 **1–3**，缺省 1；返回 backpackIndex 为 **0–2**。单包响应包含 primaryWeaponId、secondaryWeaponId、throwableId、throwableIds、version 和 attachments。新增三槽字段允许重复和 null；旧 throwableId 留作兼容。

```json
{
  "primaryWeaponId": "weapon.m4",
  "secondaryWeaponId": "weapon.service_pistol",
  "throwableIds": ["throwable.frag", "throwable.frag", null],
  "expectedVersion": 1
}
```

示例为 PUT 请求，expectedVersion 应取当前 GET 返回值。服务器要求三槽恒长 3、有效且已持有；未购买型号拒绝。修改武器清理该槽原配件。配件 PUT 使用 expectedVersion、weaponSlot、可选 weaponItemId 和 attachments 数组；数组元素为 attachmentSlot / attachmentItemId，服务器校验当前武器兼容。

## 房间与 DS 控制面

| 方法 | /api/rooms 下的路径 | 用途 |
|---|---|---|
| POST / GET | 根路径 | 创建 / 列表 |
| POST | join-by-code | 房间码加入 |
| GET | {roomId} | 房间快照 |
| POST | {roomId}/join、team、ready、settings | 加入、选边、准备、设置（每项都在 roomId 后） |
| POST | {roomId}/start | 房主开始、租 DS、签票 |
| POST | {roomId}/return | 返回确认 |
| GET | {roomId}/match-result | 权威结果 |
| GET / POST | {roomId}/chat | 房间聊天 |
| POST | heartbeat、leave | 成员心跳 / 离开 |

生命周期包含 Waiting、Starting、InMatch、Returning；当前浏览器支持符合条件的局中补人。协议、队伍容量、实例健康和票据期限由服务端验证，HTTP 创建参数中的旧 HostAddress / HostPort 不代表允许玩家指定权威实例。

`/api/server-instances` 整组由 RequireServerKey 保护：

| 方法 | 子路径 | 用途 |
|---|---|---|
| POST | register | 注册地图、地址、容量、协议 / 构建身份 |
| POST | {instanceId}/heartbeat | 状态和人数心跳 |
| POST | tickets/consume | 一次性票据消费与配装读取 |
| GET | pool | 实例池诊断 |
| POST | {instanceId}/maintenance | 禁止新租用 |
| POST | {instanceId}/players/disconnect | 权威掉线清理 |
| POST | {instanceId}/match-result | 终局报告与奖励结算 |

玩家 JWT 不能替代控制面密钥。终局按 matchId 幂等，房间结果、奖励资格与实例释放由服务器报告和 API 服务处理。

好友请求、好友删除、私聊、已读和邀请由 FriendsController / SocialController 提供 `/api/friends`、`/api/social` 路由。健康检查 `/health` 匿名；热更 `/hotupdate` 为登录前匿名只读静态资源。

## 数据模型、迁移与种子

主要持久化域：User / TokenVersion、Profile / Wallet、Inventory / Purchases、Loadouts / Attachments / Throwables、Rooms / Members、ServerInstances / JoinTickets、比赛奖励、Pass、Friends / Social 和 Settings。

`20260930020000_AddLoadoutBackpacks` 引入背包索引与唯一约束；`20260930060000_AddThrowableSlots` 增加按配装、槽位保存的投掷记录，并迁移旧预设。每用户逻辑上三个背包，缺失背包按 BackpackPolicy 合成默认，首次保存落库；入场 ActiveIndex 当前固定 0。

ThrowableSlotPolicy 保留三槽 NULL 行，明确区分“未迁移”与“用户选择空槽”。CatalogSeeder、AttachmentSystemSeeder、PassSeeder 维护商品、兼容、持有迁移与奖励；种子可重跑但不能破坏空槽、购买历史或把手枪弹匣恢复成仅奖励获取。

关系库启动时 MigrateAsync，然后依次执行种子；在备份和独立测试库确认后再更新实际库。本轮文档同步不执行数据库迁移。配置方法见 [Setup](Setup.md)，验证方法见 [Testing](Testing.md)。
