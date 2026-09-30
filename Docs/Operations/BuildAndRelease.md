# 双端构建、热更与发布身份

## 构建顺序

构建前确定源码基线、协议与目标版本；保持当前服务器和旧分发可恢复。Unity 编译完成后使用菜单：

1. `Tools/Dedicated Server/Build Windows Server (Release)`。
2. `Tools/Client/Build Windows Client (Release)`。
3. `Tools/HotUpdate/Build Map Bundles`。

默认双端安装目录为 Builds/Server 和 Builds/ReleaseClient。Editor 构建脚本和 BuildManifestWriter 生成 build-manifest.json，记录 Unity 版本、构建时间、协议、构建输入摘要与程序集信息。构建改变临时设置时应检查已恢复；同批双端需来自同一输入，不用“构建成功”代替身份核对。

后端单独构建 / 发布 .NET 8 项目。Unity DS 与 API 是两个进程，两者缺一不可；部署脚本会验证控制面和实例注册。

## 身份与 Git 字节

```powershell
powershell -ExecutionPolicy Bypass -File Tools/BuildStatus.ps1 -Gate
```

Gate 检查构建清单、当前输入以及运行栈身份。客户端与 DS buildId 可以不同，因为各自程序集编译不同；需要协议和输入基线对应，且运行日志与各自清单一致。

`.gitattributes` 对 Assets、Packages、ProjectSettings、后端与 Tools 保留字节，避免 autocrlf 改变源包匹配。Final20261001 原始输入摘要包含忽略的本机文件，受控输入摘要另行计算；不把干净克隆的全目录摘要与原机器摘要强行等同。规则和算法见 [最终版本记录](../Releases/Final20261001.md)。

当本地封存快照存在时，可运行只读比较：

```powershell
python Tools/Diagnostics/Verify-ReleaseSource.py --snapshot Logs/RealTest0930-Rework/ManualBuild/input-files-before.txt --revision Final20261001 --output Logs/source-verification.json
```

该快照是本地证据，不在 Git；干净克隆可使用版本记录的受控清单自行计算摘要。

## 热更发布

Lua 来源 Assets/Resources/Lua，地图 bundle 来源 Logs/HotUpdate/bundles。先构建地图，再准备发布：

```powershell
$nextVersion = 26 # 示例：实际取尚未发布、且高于当前版本的编号
powershell -ExecutionPolicy Bypass -File Tools/HotUpdate/Publish-HotUpdate.ps1 -Version $nextVersion
```

`-Deploy` 才将内容部署到 `fps-backend/src/UnityFps.Api/hotupdate`，替换当前指针。该动作影响客户端下载，先确认包、协议及部署目标。脚本支持 BundleSourcePath；旧内容回滚必须以更高版本重新发布，客户端拒绝版本降级。不同内容不得覆盖同一已发布版本目录。

Lua 主 manifest 和地图专用 maps-manifest 通道分开，地图绑定协议。不能把混合 Lua 清单直接当地图清单。客户端先下载、校验并组装完整版本，再提交安装根目录；出现下载 / 校验错误时检查日志和已安装目录，不手工把坏目录标为成功。

Final20261001 使用热更 `25`，本轮只同步代码和文档，没有再次部署或覆盖版本 25。

## 封存、恢复与日志

封存时保存双端清单、输入快照、源码提交、热更指针、包 SHA256 和验证范围。Builds / Logs 本地保留，不进 Git；Git tag 代表源码快照，不包含可执行 ZIP。

包校验失败时从封存目录恢复匹配客户端、DS 与 API，不混用新地图和旧协议。数据库迁移涉及单独备份，不能靠替换 exe 回滚数据。运行栈恢复用现有状态文件和明确实例身份，禁止按通用进程名批量终止。

每客户端用独立 -logFile；客户端日志在 Tools/Client/Logs，DS / API 启动器日志在 Tools/Server/Logs，最终包启动器还提供收集日志入口。导出时排除密码、JWT、入场票据、连接串与控制面密钥。保留故障前后的完整身份记录，避免拿历史 Player.log 判定当前包版本。
