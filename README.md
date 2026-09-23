# UnityFpsLowPoly

一个使用 Unity 6 制作的多人 FPS 项目，包含游戏客户端、专用服务器逻辑和 ASP.NET Core 后端。当前代码以开发和测试为主，具体功能状态请以 `Docs/` 中的实施记录为准。

## 环境

- Unity **6000.0.76f1**（见 `ProjectSettings/ProjectVersion.txt`）
- .NET **8** SDK，用于 `fps-backend/` 的构建和测试
- 项目使用 URP、Input System、FishNet 等依赖；Unity 包版本见 `Packages/manifest.json`
- `Packages/manifest.json` 还引用本机 `.codely-cli` 和 `.codely.packages` 路径；在新机器上打开项目之前，需要安装或调整这两个本地包依赖

## 快速开始

1. 用上述版本的 Unity Hub 打开仓库根目录，等待资源导入完成。
2. 在 Unity 中打开 `Assets/_Project/Scenes/Boot.unity`。其他主要场景包括 `Lobby.unity`、`Arena.unity` 和 `Map_*.unity`。
3. 如需启动后端，在 `fps-backend/` 下运行：

   ```powershell
   dotnet run --project .\src\UnityFps.Api --launch-profile http
   ```

   后端的本地配置和接口说明见 [`fps-backend/README.md`](fps-backend/README.md)。连接串和签名密钥应通过环境变量或 User Secrets 提供。

## 目录

| 路径 | 内容 |
| --- | --- |
| `Assets/_Project/` | 游戏场景、脚本、预制体、资源和 Unity 测试 |
| `fps-backend/` | ASP.NET Core API、数据库迁移和后端测试 |
| `Packages/`、`ProjectSettings/` | Unity 包与项目设置 |
| `Docs/` | 架构、开发计划、实施与验收记录 |
| `Tools/` | 构建、部署、诊断及本地测试脚本 |

## 验证

Unity 测试位于 `Assets/_Project/Tests/`，可通过 Unity Test Runner 运行 EditMode 测试。后端可在 `fps-backend/` 下运行：

```powershell
dotnet build .\UnityFps.Backend.sln
dotnet test .\UnityFps.Backend.sln
```

项目总览从 [`Docs/00-总览与导航.md`](Docs/00-总览与导航.md) 开始阅读。
