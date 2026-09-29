param([string]$ReleaseId='private-baseline-20260924-01')
$ErrorActionPreference='Stop'
if($ReleaseId -notmatch '^[a-zA-Z0-9_-]{1,80}$'){throw 'Invalid release ID.'}
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$release=Join-Path $project ('Builds/PrivateInvitations/'+$ReleaseId)
$source=Join-Path $release 'Client'
$manifest=Get-Content (Join-Path $release 'release-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$dest=Join-Path $project ('Builds/PrivateInvitationExePackages/'+$ReleaseId)
if(Test-Path -LiteralPath $dest){throw 'Output exists; keep distributed packages immutable.'}
$clientFiles=@($manifest.files | Where-Object {$_.path.StartsWith('Client/')})
foreach($f in $clientFiles){
    if((Get-FileHash -LiteralPath (Join-Path $release $f.path)).Hash -ne $f.sha256){throw ('Baseline hash mismatch: '+$f.path)}
}
# Use the Unity build file list to omit the launcher's .NET runtime from the direct package.
# Always copy the sealed release bytes, never unsealed build output bytes.
$unityBuild=Join-Path $project 'Builds/PrivateInvitationClient'
$unityManifest=Get-Content (Join-Path $unityBuild 'build-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($unityManifest.inputDigest -ne $manifest.inputDigest){throw 'Unity file list belongs to another build.'}
New-Item -ItemType Directory -Path $dest | Out-Null
foreach($variant in @('DirectClient','ExeLauncher')){
    $stage=Join-Path $dest $variant
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach($f in $clientFiles){
        $relative=$f.path.Substring(7)
        if($relative -match '(?i)\.cmd$'){continue}
        if($variant -eq 'DirectClient' -and -not(Test-Path -LiteralPath (Join-Path $unityBuild $relative) -PathType Leaf)){continue}
        $target=Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $relative) -Destination $target
    }
    $entry=if($variant -eq 'DirectClient'){'UnityFpsClient.exe'}elseif(Test-Path (Join-Path $stage 'Launcher/StartGame.exe')){'Launcher/StartGame.exe'}else{'StartGame.exe'}
    $readme=if($variant -eq 'DirectClient'){
        '完整解压后双击 UnityFpsClient.exe。不要单独移动 EXE，必须保留 UnityFpsClient_Data、UnityPlayer.dll 及其余附带文件。此入口不检查或申请加入 ZeroTier；请先安装 ZeroTier，加入网络 743993800fd77ce1 并由主机批准设备，再启动游戏。主机需要开启对应版本服务器。在游戏登录页点击“创建账号”，填写用户名和至少 8 位密码，再点击“注册并继续”。'
    }else{
        ('完整解压后运行 '+$entry+'，再点击窗口中的“开始游戏”。未安装 ZeroTier 时按提示安装，首次加入允许管理员请求，将设备编号发给主机批准，然后重试。主机需要开启对应版本服务器。在游戏登录页点击“创建账号”，填写用户名和至少 8 位密码，再点击“注册并继续”。连接诊断和导出测试记录均在助手中。不要单独发送或移动 EXE，保留整个文件夹。')
    }
    Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Value $readme -Encoding UTF8
    $hashes=@(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        @{path=$_.FullName.Substring($stage.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    })
    @{releaseId=$ReleaseId;protocolId=$manifest.protocolId;entryPoint=$entry;files=$hashes} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $stage 'package-manifest.json') -Encoding UTF8
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $dest ($variant+'.zip'))
}
Get-ChildItem -LiteralPath $dest -Filter '*.zip' | ForEach-Object { [pscustomobject]@{path=$_.FullName;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} } | ConvertTo-Json | Set-Content (Join-Path $dest 'zip-hashes.json') -Encoding UTF8
Write-Output ('EXE_PACKAGES_READY '+$dest)
