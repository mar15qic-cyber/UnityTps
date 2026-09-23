param([Parameter(Mandatory)][string]$ConfigPath,[Parameter(Mandatory)][ValidateSet('create','disable')][string]$Action,[Parameter(Mandatory)][string]$Username)
. "$PSScriptRoot/Cloud.Common.ps1"
$c=Read-CloudConfig $ConfigPath; $s=Read-CloudSecrets $c
$values=@{'ASPNETCORE_ENVIRONMENT'='Production';'Jwt__SigningKey'=$s.jwtSigningKey;
    'ServerInstances__ServerKey'=$s.serverKey;'ConnectionStrings__GameDb'=$s.gameDb;
    'InviteAdmin__Action'=$Action;'InviteAdmin__Username'=$Username;'InviteAdmin__Password'=$null}
$bstr=[IntPtr]::Zero
if ($Action -eq 'create') {
    $password=Read-Host 'New account password (12+ characters)' -AsSecureString
    $bstr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
    $values.InviteAdmin__Password=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
}
$previous=@{}
try {
    foreach ($k in $values.Keys) { $previous[$k]=[Environment]::GetEnvironmentVariable($k,'Process'); [Environment]::SetEnvironmentVariable($k,$values[$k],'Process') }
    Push-Location (Join-Path $c.releaseRoot 'Api')
    try { & (Join-Path $c.releaseRoot 'Api/UnityFps.Api.exe'); if ($LASTEXITCODE -ne 0) { throw 'Account command failed.' } }
    finally { Pop-Location }
} finally {
    foreach ($k in $previous.Keys) { [Environment]::SetEnvironmentVariable($k,$previous[$k],'Process') }
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
