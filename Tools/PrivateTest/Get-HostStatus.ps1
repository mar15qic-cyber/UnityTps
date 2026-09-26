param([Parameter(Mandatory)][string]$ConfigPath)
. "$PSScriptRoot/Private.Common.ps1"
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$c=Read-PrivateConfig $ConfigPath
Write-Output ('检查时间：'+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Write-Output ('配置版本：'+$c.releaseId)
Write-Output ('主机目录：'+$c.releaseRoot)
$manifest=Join-Path $c.releaseRoot 'Api/hotupdate/maps-manifest.json'
Write-Output ('地图清单：'+$(if(Test-Path -LiteralPath $manifest){'存在'}else{'缺失，请重新选择完整主机发布目录'}))
try {
    $status=Invoke-RestMethod ('http://'+$c.publicAddress+':'+$c.playerTcpPort+'/api/test-status') -TimeoutSec 2
    Write-Output ('运行版本：'+$status.releaseId+'；地图就绪：'+$status.ready)
    if($status.releaseId -ne $c.releaseId){Write-Output '版本不一致：保存配置不会替换运行进程，需要先关服再开服。'}
}catch{Write-Output 'API 当前不可达（下方仍会检查本机进程和端口）。'}
$records=@();$state=Join-Path $c.stateRoot 'processes.json'
if(Test-Path $state){$records=@(Get-Content $state -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })}
$tcp=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue)
$udp=@(Get-NetUDPEndpoint -ErrorAction SilentlyContinue)
$ports=@([pscustomobject]@{name='API 内部';port=([uri]$c.apiListenUrl).Port;tcp=$true},[pscustomobject]@{name='玩家入口';port=$c.playerTcpPort;tcp=$true})
$ports+=@(Get-CloudMaps $c | ForEach-Object {[pscustomobject]@{name=$_.id;port=$_.port;tcp=$false}})
Write-Output "`r`n服务 / 协议端口 / PID / 归属（非托管不自动强杀）"
foreach($item in $ports){
    $listeners=@($(if($item.tcp){$tcp}else{$udp}) | Where-Object LocalPort -eq $item.port)
    $ids=@($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
    $protocol=if($item.tcp){'TCP'}else{'UDP'}
    if(-not $ids.Count){Write-Output ($item.name+' / '+$protocol+' '+$item.port+' / 空闲');continue}
    foreach($processId in $ids){
        $p=Get-Process -Id $processId -ErrorAction SilentlyContinue
        $owned=@($records | Where-Object {$_.pid -eq $processId -and (Test-ManagedProcess $_)})
        $kind=if($owned.Count){'托管'}else{'非托管 / 遗留或其他程序'}
        Write-Output ($item.name+' / '+$protocol+' '+$item.port+' / PID '+$processId+' / '+$kind)
        if($p){Write-Output ('    '+$p.ProcessName+' '+$p.Path)}
    }
}
Write-Output "`r`n记录的进程："
foreach($r in $records){Write-Output ($r.name+' PID '+$r.pid+'：'+$(if(Test-ManagedProcess $r){'运行中'}else{'已退出或 PID 已被其他进程复用'}))}
# Include same-project servers which no longer listen on a configured port.
$releaseBase=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Builds/PrivateInvitations'))+'\'
foreach($p in @(Get-Process -Name UnityFps.Api,UnityFpsDedicatedServer -ErrorAction SilentlyContinue)){
    if($p.Path -and $p.Path.StartsWith($releaseBase,[StringComparison]::OrdinalIgnoreCase) -and -not @($records | Where-Object {$_.pid -eq $p.Id -and (Test-ManagedProcess $_)}).Count){
        Write-Output ('未被当前记录管理的发布进程：PID '+$p.Id+' '+$p.Path)
    }
}
