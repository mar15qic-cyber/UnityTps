param([string]$ConfigPath=(Join-Path $PSScriptRoot '.runtime/host.json'))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
. "$PSScriptRoot/Private.Common.ps1"
$form=New-Object Windows.Forms.Form
$form.Text='本机邀请测试控制台'; $form.Size=New-Object Drawing.Size(820,620)
$form.Font=New-Object Drawing.Font('Microsoft YaHei UI',10)
$output=New-Object Windows.Forms.TextBox
$output.Multiline=$true; $output.ReadOnly=$true; $output.ScrollBars='Vertical'; $output.SetBounds(20,170,760,390)
$form.Controls.Add($output)
$script:worker=$null; $script:attempts=0; $script:supervise=$false
function Invoke-HostTask([string]$Script,[string[]]$Arguments=@()) {
    if ($script:worker -and -not $script:worker.HasExited) { $output.Text='操作进行中，请等待。'; return }
    $log=Join-Path $PSScriptRoot '.runtime/console.log'
    New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
    $args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$Script+'"'),'-ConfigPath',('"'+$ConfigPath+'"'))+$Arguments
    $script:worker=Start-Process powershell.exe -ArgumentList $args -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError ($log+'.error')
}
function Add-Button([string]$Text,[int]$X,[int]$Y,[scriptblock]$Click) {
    $b=New-Object Windows.Forms.Button; $b.Text=$Text; $b.SetBounds($X,$Y,175,45); $b.Add_Click($Click); $form.Controls.Add($b)
}
Add-Button '首次配置' 20 20 {
    $setup=New-Object Windows.Forms.Form;$setup.Text='首次配置（仅主机）';$setup.Size=New-Object Drawing.Size(670,330)
    $fields=@{}
    $values=@{NetworkId='743993800fd77ce1';HostAddress='10.16.41.231';Subnet='10.16.41.0/24';ReleaseRoot=([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Builds/PrivateInvitations/private-test-01')));GameDb=''}
    $labels=@{NetworkId='网络编号';HostAddress='本机组网地址';Subnet='组网网段';ReleaseRoot='发布目录';GameDb='MySQL 连接字符串'}
    $y=15
    foreach($key in @('NetworkId','HostAddress','Subnet','ReleaseRoot','GameDb')){
        $label=New-Object Windows.Forms.Label;$label.Text=$labels[$key];$label.SetBounds(15,$y,150,25);$setup.Controls.Add($label)
        $field=New-Object Windows.Forms.TextBox;$field.Text=$values[$key];$field.SetBounds(170,$y,460,25);if($key -eq 'GameDb'){$field.UseSystemPasswordChar=$true};$fields[$key]=$field;$setup.Controls.Add($field);$y+=42
    }
    $save=New-Object Windows.Forms.Button;$save.Text='保存';$save.SetBounds(460,235,170,40);$setup.Controls.Add($save)
    $save.Add_Click({
        try{
            & "$PSScriptRoot/Initialize-PrivateHost.ps1" -ReleaseRoot $fields.ReleaseRoot.Text -GameDb $fields.GameDb.Text -NetworkId $fields.NetworkId.Text -HostAddress $fields.HostAddress.Text -Subnet $fields.Subnet.Text | Out-Null
            $script:ConfigPath=Join-Path $PSScriptRoot '.runtime/host.json';$output.Text='配置已保存。请设置组网防火墙，再点击开始测试。';$setup.Close()
        }catch{[Windows.Forms.MessageBox]::Show('配置保存失败，请检查输入及目录权限。')|Out-Null}
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
$timer=New-Object Windows.Forms.Timer; $timer.Interval=5000
$timer.Add_Tick({
    try {
        if ($script:worker -and -not $script:worker.HasExited) { $output.Text='正在启动或检查，请稍候…'; return }
        $c=Read-PrivateConfig $ConfigPath; $pool=Get-PrivatePool $c
        $output.Text=($pool.instances | Select-Object mapId,state,currentPlayers,fresh | Format-Table -AutoSize | Out-String)
        $state=Join-Path $c.stateRoot 'processes.json'
        if (Test-Path $state) {
            $records=@(Get-Content $state -Raw | ConvertFrom-Json)
            $alive=@($records | Where-Object { Test-ManagedProcess $_ })
            $memory=($alive | ForEach-Object { (Get-Process -Id $_.pid).WorkingSet64 } | Measure-Object -Sum).Sum
            $output.AppendText("`r`n服务内存："+[math]::Round($memory/1MB)+' MB；进程：'+$alive.Count)
            if ($script:supervise -and $alive.Count -lt $records.Count -and $script:attempts -lt 3) { $script:attempts++; Invoke-HostTask "$PSScriptRoot/Start-PrivateHost.ps1" }
        }
    } catch {
        $output.Text='服务尚未就绪。请检查配置及启动日志。'
        if ($script:supervise -and $script:attempts -lt 3) { $script:attempts++; Invoke-HostTask "$PSScriptRoot/Start-PrivateHost.ps1" }
        $log=Join-Path $PSScriptRoot '.runtime/console.log.error'
        if (Test-Path $log) { $output.AppendText("`r`n"+(Get-Content $log -Tail 8 | Out-String)) }
    }
})
$timer.Start(); $form.Add_FormClosed({$timer.Stop();$timer.Dispose()}); [void]$form.ShowDialog()
