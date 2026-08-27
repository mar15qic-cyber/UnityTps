# Day5 后端与 Day6 Unity 大厅交接

## 边界

- 后端只负责账户、档案、配装、升级和比赛结算；不包含房间、FishNet、邮件、找回密码、角色权限或 Refresh Token。
- Unity 只在内存中保存 JWT；不写入 PlayerPrefs，不直接向 UI 暴露 UnityWebRequest。
- `Assets/_Project/Scenes/Arena.unity` 不属于本阶段修改范围。Boot/Lobby 为独立场景，Day7 才开放 Play/Room。
- Throwable 在本阶段固定为 `null`；升级属性只完成持久化和大厅反馈，实际武器属性应用留到 Day10。

## 本地启动

```powershell
dotnet run --project .\src\UnityFps.Api --launch-profile http
```

默认地址为 `http://127.0.0.1:5080`，Swagger 为 `/swagger`。连接串和 JWT 签名密钥只能通过 User Secrets 或环境变量提供：

```powershell
$env:ConnectionStrings__GameDb = "Server=127.0.0.1;Port=3306;Database=unity_fps;User=...;Password=...;"
$env:Jwt__SigningKey = "至少 32 字节的本地开发密钥"
```

未提供连接串时，Development 环境允许使用 InMemory 方便本地演示；非 Development 环境必须显式提供连接串（或明确打开 `Database__AllowInMemoryFallback`），不会静默降级。Demo 账号同样只在 Development 且显式设置 `DemoSeed__Enabled=true`、`DemoSeed__Password` 时创建，并获得 6 点技能点。

## API 契约

| 方法 | 路径 | 认证 | 说明 |
|---|---|---|---|
| POST | `/api/auth/register` | 否 | 创建账户、默认档案和默认配装，返回 12h JWT |
| POST | `/api/auth/login` | 否 | 返回 JWT、过期时间、档案和配装 |
| GET | `/api/profile` | Bearer | 读取档案 |
| PUT | `/api/profile/upgrades` | Bearer | 只升不降，目标等级 0–5，重复请求幂等 |
| GET/PUT | `/api/loadout` | Bearer | 白名单主副武器，Throwable 必须为 null |
| POST | `/api/matches` | Bearer | 事务写战绩、累计 XP、处理多级升级 |

错误统一为 ProblemDetails，并包含稳定的 `code` 字段；技能点不足、非法配装等业务冲突返回 409。

## 数据库探针状态

代码和手工 Migration 已按 Pomelo 8/MySQL 8 兼容路径落地，自动化测试使用 InMemory。当前机器虽安装 MySQL 9.5，但尚未提供可用凭据，因此没有把“9.5 探针通过”写入验收结论；拿到凭据后先针对 `unity_fps_test`（名称必须以 `_test` 结尾）执行 Migration 和增删查，失败立即切换受测 MySQL 8.4。

## 验证命令

```powershell
dotnet build .\UnityFps.Backend.sln --no-restore
dotnet test .\UnityFps.Backend.sln --no-restore
```

Day6 Unity EditMode 测试覆盖 URL 规范化、DTO 字段、会话状态和大厅状态枚举；PlayMode 端到端验收必须另行获得用户授权，只做正常 UI 操作和只读观察。
