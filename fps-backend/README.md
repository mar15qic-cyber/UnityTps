# UnityFps Backend

ASP.NET Core / .NET 8 API，负责账户、钱包与目录、三背包与配件、房间、DS 实例池和一次性票据、服务器权威结算、好友、社交与设置。

## 开发入口

从仓库根目录运行；先准备 MySQL 连接、JWT 签名密钥和 DS 控制面配置：

```powershell
dotnet restore fps-backend/UnityFps.Backend.sln
dotnet build fps-backend/UnityFps.Backend.sln --no-restore
dotnet run --project fps-backend/src/UnityFps.Api --launch-profile http
```

配置占位示例、命名 InMemory 的实际条件见 [环境搭建](../Docs/Development/Setup.md)。默认本地 API 为 127.0.0.1:5080；Development 提供 /swagger，/health 检查数据库可达。关系库启动执行迁移和种子。

## 当前契约与结构

- 玩家使用 Bearer JWT，登录 TokenVersion 实现单活；服务器使用 X-Server-Key。
- 每用户三个背包、每包三投掷槽，可重复和留空，expectedVersion 防止覆盖并发保存。
- 商城购买永久解锁，idempotencyKey 防止重复扣款。
- 创建等待房间与开始租用 DS 分开，按地图和协议签发一次性入场票据。
- 比赛由 DS 上报并按 matchId 幂等结算；玩家 POST /api/matches 已退役为 410。
- Controllers 定义 HTTP，Services 实现业务，Features/Contracts.cs 定义 DTO，Data 管理 EF Core 实体与迁移。

完整接口、数据模型和迁移说明见 [Backend 开发文档](../Docs/Development/Backend.md)。[系统架构](../Docs/Architecture/System.md)、[测试方法](../Docs/Development/Testing.md)、[构建与部署](../Docs/Operations/BuildAndRelease.md) 提供联机上下文。

```powershell
dotnet test fps-backend/UnityFps.Backend.sln --no-restore
```

InMemory 测试不等于 MySQL 迁移验证；关系库探针使用独立测试库。实际秘密只在本机环境 / 秘密配置保存，不进 Git、不放进玩家包。
