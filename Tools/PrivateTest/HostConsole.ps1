param([string]$ConfigPath=(Join-Path $PSScriptRoot '.runtime/host.json'),[switch]$ValidateUi)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$created=$false
$mutexName='Local\UnityFpsPrivateHostConsole';if($ValidateUi){$mutexName+='Validation'+[guid]::NewGuid().ToString('N')}
$consoleMutex=New-Object Threading.Mutex($true,$mutexName,[ref]$created)
if(-not $created){[Windows.Forms.MessageBox]::Show('邀请测试控制台已经打开，请切换到已有窗口。')|Out-Null;$consoleMutex.Dispose();return}
. "$PSScriptRoot/Private.Common.ps1"
$form=New-Object Windows.Forms.Form
$form.Text='本机邀请测试控制台'; $form.Size=New-Object Drawing.Size(1000,820)
$form.Font=New-Object Drawing.Font('Microsoft YaHei UI',10)
$output=New-Object Windows.Forms.TextBox
$output.Multiline=$true; $output.ReadOnly=$true; $output.ScrollBars='Vertical'; $output.SetBounds(20,200,940,560)
$form.Controls.Add($output)
$script:worker=$null; $script:attempts=0; $script:supervise=$false
function Invoke-HostTask([string]$Script,[string[]]$Arguments=@()) {
    if ($script:worker -and -not $script:worker.HasExited) { $output.Text='操作进行中，请等待。'; return }
        $log=Join-Path $PSScriptRoot ('.runtime/console/'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-'+[guid]::NewGuid().ToString('N')+'.log')
    $script:actionLog=$log
    $script:actionName=[IO.Path]::GetFileNameWithoutExtension($Script)
    $script:actionStarted=Get-Date
    New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
    $args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$Script+'"'),'-ConfigPath',('"'+$ConfigPath+'"'))+$Arguments
    $script:worker=Start-Process powershell.exe -ArgumentList $args -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError ($log+'.error')
}
function Add-Button([string]$Text,[int]$X,[int]$Y,[scriptblock]$Click) {
    $b=New-Object Windows.Forms.Button; $b.Text=$Text; $b.SetBounds($X,$Y,175,45); $b.Add_Click($Click); $form.Controls.Add($b)
}
Add-Button '配置 / 切换版本' 20 20 {
    $setup=New-Object Windows.Forms.Form;$setup.Text='主机配置（留空保留已存数据库连接）';$setup.Size=New-Object Drawing.Size(950,370)
    $fields=@{}
    $values=@{NetworkId='743993800fd77ce1';HostAddress='10.16.41.231';Subnet='10.16.41.0/24';ReleaseRoot=([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Builds/PrivateInvitations/private-test-01')));GameDb=''}
    if(Test-Path -LiteralPath $ConfigPath){
        try{
            $saved=Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $values.NetworkId=$saved.networkId;$values.HostAddress=$saved.publicAddress;$values.Subnet=$saved.overlaySubnet;$values.ReleaseRoot=$saved.releaseRoot
        }catch{[Windows.Forms.MessageBox]::Show('已保存配置无法读取，请检查 host.json。')|Out-Null;return}
    }
    $releases=@(Get-ChildItem (Join-Path $PSScriptRoot '../../Builds/PrivateInvitations') -Directory -ErrorAction SilentlyContinue | Where-Object {
        (Test-Path (Join-Path $_.FullName 'release-manifest.json')) -and (Test-Path (Join-Path $_.FullName 'Api/hotupdate/maps-manifest.json')) -and (Test-Path (Join-Path $_.FullName 'Server/UnityFpsDedicatedServer.exe'))
    } | Sort-Object LastWriteTime -Descending)
    $labels=@{NetworkId='网络编号';HostAddress='本机组网地址';Subnet='组网网段';ReleaseRoot='发布目录';GameDb='MySQL 连接字符串'}
    $y=15
    foreach($key in @('NetworkId','HostAddress','Subnet','ReleaseRoot','GameDb')){
        $label=New-Object Windows.Forms.Label;$label.Text=$labels[$key];$label.SetBounds(15,$y,150,25);$setup.Controls.Add($label)
        if($key -eq 'ReleaseRoot'){
            $field=New-Object Windows.Forms.ComboBox;$field.DropDownStyle='DropDownList';$field.DropDownWidth=1000
            foreach($release in $releases){[void]$field.Items.Add($release.FullName)}
            $field.SelectedItem=$values.ReleaseRoot
            if($field.SelectedIndex -lt 0 -and $field.Items.Count){$field.SelectedIndex=0}
        }else{$field=New-Object Windows.Forms.TextBox;$field.Text=$values[$key]}$field.SetBounds(170,$y,740,25);if($key -eq 'GameDb'){$field.UseSystemPasswordChar=$true};$fields[$key]=$field;$setup.Controls.Add($field);$y+=42
    }
    $hint=New-Object Windows.Forms.Label;$hint.Text='连接字符串留空：沿用已保存值；仅首次配置或更换数据库时填写。切换版本请先正常关服。';$hint.SetBounds(15,230,910,25);$setup.Controls.Add($hint)
    $save=New-Object Windows.Forms.Button;$save.Text='保存配置';$save.SetBounds(740,270,170,40);$setup.Controls.Add($save)
    $save.Add_Click({
        try{
            & "$PSScriptRoot/Initialize-PrivateHost.ps1" -ReleaseRoot $fields.ReleaseRoot.Text -GameDb $fields.GameDb.Text -NetworkId $fields.NetworkId.Text -HostAddress $fields.HostAddress.Text -Subnet $fields.Subnet.Text | Out-Null
            $script:ConfigPath=Join-Path $PSScriptRoot '.runtime/host.json';$output.Text='配置已保存。请设置组网防火墙，再点击开始测试。';$setup.Close()
        }catch{[Windows.Forms.MessageBox]::Show(('配置保存失败：'+$_.Exception.Message))|Out-Null}
    });$setup.ShowDialog()|Out-Null
}
Add-Button '开始测试' 210 20 { $script:supervise=$true; $script:attempts=0; Invoke-HostTask "$PSScriptRoot/Start-PrivateHost.ps1" }
Add-Button '停止新入场并关服' 400 20 { $script:supervise=$false; Invoke-HostTask "$PSScriptRoot/Stop-PrivateHost.ps1" }
Add-Button '连接检查' 590 20 { Invoke-HostTask "$PSScriptRoot/Start-PrivateHost.ps1" @('-CheckOnly') }
Add-Button '设置组网防火墙' 20 80 {
    try { Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSScriptRoot+'/Set-PrivateFirewall.ps1"'),'-ConfigPath',('"'+$ConfigPath+'"')) }
    catch { $output.Text='已取消管理员操作。' }
}
Add-Button '打开设备批准页面' 210 80 { $c=Read-PrivateConfig $ConfigPath; Start-Process ('https://central.zerotier.com/network/'+$c.networkId) }
Add-Button '账号管理' 400 80 {
    $account=New-Object Windows.Forms.Form; $account.Text='测试账号'; $account.Size=New-Object Drawing.Size(420,200)
    $name=New-Object Windows.Forms.TextBox; $name.SetBounds(20,20,350,30); $account.Controls.Add($name)
    foreach ($action in @('create','disable')) {
        $button=New-Object Windows.Forms.Button; $button.Text=$action; $button.Tag=$action; $button.SetBounds($(if($action -eq 'create'){20}else{210}),70,160,40)
        $button.Add_Click({ if ($name.Text -notmatch '^[a-zA-Z0-9_-]{3,32}$') { return }; Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSScriptRoot+'/../Cloud/Set-InviteAccount.ps1"'),'-ConfigPath',('"'+$ConfigPath+'"'),'-Action',$this.Tag,'-Username',$name.Text); $account.Close() })
        $account.Controls.Add($button)
    }
    $account.ShowDialog() | Out-Null
}
$script:actionLog=$null;$script:actionName='本窗口尚未执行操作';$script:actionStarted=$null
$script:diagnostic=$null;$script:statusReport='正在检查本机进程和端口…'
$script:diagnosticPath=Join-Path $PSScriptRoot ('.runtime/status-'+[guid]::NewGuid().ToString('N')+'.log')
function Request-Status {
    if($script:diagnostic -and -not $script:diagnostic.HasExited){return}
    if($script:diagnostic){
        if(Test-Path $script:diagnosticPath){$script:statusReport=Get-Content $script:diagnosticPath -Raw -Encoding UTF8}
        if($script:diagnostic.ExitCode -ne 0){$script:statusReport+='状态检查失败，请检查配置；不会复用旧启动日志。'}
    }
    New-Item -ItemType Directory (Split-Path $script:diagnosticPath) -Force|Out-Null
    $script:diagnostic=Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSScriptRoot+'/Get-HostStatus.ps1"'),'-ConfigPath',('"'+$ConfigPath+'"')) -RedirectStandardOutput $script:diagnosticPath -RedirectStandardError ($script:diagnosticPath+'.error')
}
Add-Button '刷新进程 / 端口' 20 140 {Request-Status}
Add-Button '恢复遗留进程记录' 590 140 {Invoke-HostTask "$PSScriptRoot/Recover-HostProcesses.ps1"}
Add-Button '打开本次日志' 210 140 {if($script:actionLog -and (Test-Path $script:actionLog)){Start-Process notepad.exe -ArgumentList ('"'+$script:actionLog+'"')}else{[Windows.Forms.MessageBox]::Show('本窗口尚未执行开服或关服，没有本次日志。')|Out-Null}}
Add-Button '打开服务日志目录' 400 140 {Start-Process explorer.exe -ArgumentList ('"'+(Join-Path $PSScriptRoot '.runtime/logs')+'"')}
$timer=New-Object Windows.Forms.Timer;$timer.Interval=5000
$timer.Add_Tick({
    Request-Status
    $output.Text=$script:statusReport+"`r`n———————— 本次操作 ————————`r`n"+$script:actionName
    if($script:actionStarted){$output.AppendText('  '+$script:actionStarted.ToString('HH:mm:ss'))}
    if($script:worker){
        $script:worker.Refresh()
        if(-not $script:worker.HasExited){$output.AppendText('：执行中')}else{$output.AppendText('：已结束，退出码 '+$script:worker.ExitCode)}
        foreach($file in @($script:actionLog,($script:actionLog+'.error'))){if(Test-Path $file){$output.AppendText("`r`n"+(Get-Content $file -Tail 14 | Out-String))}}
    }
})
if($ValidateUi){$form.CreateControl();Write-Output ('HOST_UI_CONSTRUCTED controls='+$form.Controls.Count);$form.Dispose();$consoleMutex.ReleaseMutex();$consoleMutex.Dispose();return}
Request-Status
$timer.Start();$form.Add_FormClosed({$timer.Stop();$timer.Dispose();$consoleMutex.ReleaseMutex();$consoleMutex.Dispose()});[void]$form.ShowDialog()
