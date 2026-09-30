# 环境搭建

## 必备环境

Unity `6000.0.76f1` 与 Windows x64 / Dedicated Server 模块、.NET 8 SDK、MySQL、Windows PowerShell。数据库版本由 Pomelo 按连接探测；部署前在独立测试库验证迁移，不能把 InMemory 测试当成 MySQL 验收。

Package Manager 使用 URP `17.0.4`、Input System `1.19.0`、Cinemachine `3.1.7`。FishNet、Animancer、xLua 和模型 / 动画资源通过项目资产导入。

## 本地插件依赖

打开 Unity 前恢复 manifest 中两个 `file:` 路径：

```text
.codely-cli/extensions/TJGenerators/Packages/cn.tuanjie.ai.generators/
.codely.packages/cn.tuanjie.codely.bridge@1.0.85-47f31100/
```

目录由本机 Codely 工具提供，不在 Git；干净克隆并不能直接保证项目可打开。用原工具安装 / 恢复指定版本，确认含有效 package.json，再让 Unity 解析。缺少工具或包时先取得依赖；删包或换版本会改变 Final20261001 输入基线。

## 后端配置

根目录执行。以下为占位值，替换后仅存当前会话或本机秘密配置：

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__GameDb = 'Server=<MYSQL_HOST>;Port=3306;Database=<GAME_DB>;User=<DB_USER>;Password=<DB_PASSWORD>;'
$env:Jwt__SigningKey = '<AT_LEAST_32_BYTE_SIGNING_KEY>'
$env:ServerInstances__ServerKey = '<DS_CONTROL_PLANE_KEY>'
dotnet restore fps-backend/UnityFps.Backend.sln
dotnet run --project fps-backend/src/UnityFps.Api --launch-profile http
```

`/health` 返回连接状态，Development 环境提供 `/swagger`。启动时执行关系库迁移与种子，先确认连接和权限。

隔离演示或测试可以显式设置 `Database__InMemoryName`。当前 Development 并不自动建立可用 InMemory 库：DbContext 需要命名 InMemory 配置或有效连接串。`Database__AllowInMemoryFallback` 是外围启动许可，不能替代提供方选择。

Demo 账号需显式配置 `DemoSeed__Enabled` 与密码，行为见 DemoSeeder；正式、测试和朋友数据库分开管理。

## Unity 与联机入口

1. 打开项目，等待导入与编译，确认 Console 没有错误。
2. 打开 `Assets/_Project/Scenes/Boot.unity`，进入完整 Boot → 热更 → Lobby。
3. 本地 API 常用 `127.0.0.1:5080`，发布客户端使用外部 release environment，并校验发布身份和更新兼容。
4. 按 [构建与发布](../Operations/BuildAndRelease.md) 生成双端；双客户端使用独立账号和日志。

Library、Unity 生成工程、后端 bin/obj、.runtime、日志和构建输出均在本机生成。资源与 .meta 一起保留，避免重新生成 GUID 破坏引用。
