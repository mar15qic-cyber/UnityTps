# Lua 热更页开发规范（UnityFps 热更试点）

> 适用：所有经热更通道上线的大厅页面/玩法逻辑。原则：**Lua 是产品面，C# 是地基**；
> 地基变更走整包，产品面变更走热更包（用户或 CI 执行 `Tools/HotUpdate/Publish-HotUpdate.ps1`）。

## 1. 文件与注册
- 源文件：`Assets/Resources/Lua/<page>_page.lua.txt`（`.lua.txt` = 内置兜底 + 热更包双源同文件）。
- 注册链：`hot_bootstrap.lua` 里 `require '<page>_page'`（新增页面必须挂进 bootstrap）。
- 页面注册：`CS.Game.UI.HotLuaFacade.RegisterPage(id, label, render)`，id 全局唯一（同 id 重注册=覆盖）。
- 渲染签名：`function(root)`，root 为页面容器 `RectTransform`（已注册进 bodyObjects 清理链，无需自删）。

## 2. UI 构建（只用 DesignSystem，禁硬编码）
- 只允许：`CS.Game.UI.UITypography / UIComponents / UITheme / UIArt / UISprites`（静态方法+UITheme 令牌）。
- 锚点用 0-1 归一化（`Vector2(minX,minY)~(maxX,maxY)`），与既有页面一致；自适应宽度用 `math.min` 收窄（参照 ShellPages 地图按钮）。
- **禁**：`new GameObject` 裸建、硬编码色值/字号、直接改 canvas。

## 3. 数据通道
- 走 `HotLuaFacade.GetXxx`（C# 侧 Task→主线程回调，数据以 LuaTable 交付，1-based `ipairs` 遍历）。
- 每个 facade 方法在源码注释里有 LuaTable 契约（字段名即契约）——**C# 侧只增字段不改名**。
- 请求中的翻页/重试：局部函数 `show(root, page)` 模式（参照 career_page），避免整页重注册。

## 4. 红线（违者热更包拒绝发布）
- ❌ 禁触碰：`CS.Game.Gameplay.*`（协议/FishNet/SyncVar/RPC）、`GameProtocolIdentity`、任何 DS 侧逻辑。
- ❌ 禁 `CS.Game.UI.AppRoot` 内部状态写操作（只读 `ApiClient`/`Session` 引用级访问需走 facade）。
- ❌ 禁死循环/无超时等待（Lua 卡死=大厅卡死）；异步必须回调式。
- ❌ 禁在渲染外持有 `RectTransform` 引用（页面切换即销毁，持有=悬空）。

## 5. 质量门（每个 Lua 页两项强制）
1. **EditMode 冒烟**（`HotPageSmokeTests` 加用例）：stub LuaTable 数据 → `CareerPageRender(root, dto, nil)` 直调渲染 → 断言文本/子节点（数据分支+空态+错误态三分支）。Lua 无编译期检查，这是唯一防线。
2. **热更包演练**：`Publish-HotUpdate.ps1`（不带 -Deploy）确认包内容清单，再由发布人部署。

## 6. 发布与回滚
- 发布：`powershell -ExecutionPolicy Bypass -File Tools\HotUpdate\Publish-HotUpdate.ps1 [-Deploy] [-Version N]`。
- 回滚：`copy /Y Logs\HotUpdate\releases\<旧>\manifest.json fps-backend\src\UnityFps.Api\hotupdate\manifest.json`。
- 已装客户端不受回滚影响（DowngradeRejected）；只影响新启动客户端。
