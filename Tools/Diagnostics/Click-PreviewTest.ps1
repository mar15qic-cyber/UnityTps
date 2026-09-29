param([double]$X,[double]$Y)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$run=Join-Path $project 'Logs/PreviewV1-r2/runtime-round1'
$r=Get-Content "$run/processes.json" -Raw | ConvertFrom-Json | Where-Object role -eq 'alpha'
$p=Get-Process -Id $r.pid
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PreviewTestWindow {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left,top,right,bottom; }
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int x,y; }
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out Rect r);
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref Point p);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
}
'@
$h=$p.MainWindowHandle
if($h -eq 0){throw 'Test window unavailable.'}
[PreviewTestWindow]::ShowWindow($h,9) | Out-Null
[PreviewTestWindow]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 250
$rect=[PreviewTestWindow+Rect]::new();[PreviewTestWindow]::GetClientRect($h,[ref]$rect) | Out-Null
$point=[PreviewTestWindow+Point]::new();$point.x=[int]($X*($rect.right-$rect.left));$point.y=[int]($Y*($rect.bottom-$rect.top))
[PreviewTestWindow]::ClientToScreen($h,[ref]$point) | Out-Null
[PreviewTestWindow]::SetCursorPos($point.x,$point.y) | Out-Null
[PreviewTestWindow]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 100
[PreviewTestWindow]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 600
$s=Get-Content "$run/alpha.state.json" -Raw | ConvertFrom-Json
@{seq=([int]$s.seq+1);op='screenshot';name='ui-current'} | ConvertTo-Json -Compress | Set-Content "$run/alpha.command.json" -Encoding utf8
