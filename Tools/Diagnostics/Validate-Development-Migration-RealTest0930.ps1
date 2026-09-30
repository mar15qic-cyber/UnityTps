$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Add-Type -Path "$project/Builds/RealTest0930/Api/MySqlConnector.dll"
$secretsFile=Join-Path $env:APPDATA 'Microsoft/UserSecrets/7acf495b-5209-4b71-bf55-76ca1b684c55/secrets.json'
$taskSecrets=Get-Content -LiteralPath $secretsFile -Raw|ConvertFrom-Json -AsHashtable
$taskConnection=[MySqlConnector.MySqlConnection]::new($taskSecrets['ConnectionStrings:GameDb'])
$taskConnection.Open()
$checks=[ordered]@{}
function CountQuery([string]$sql){
 $command=$taskConnection.CreateCommand();$command.CommandText=$sql
 try{return [long]$command.ExecuteScalar()}finally{$command.Dispose()}
}
try{
 $checks.migration=CountQuery "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId='20260930060000_AddThrowableSlots'"
 $checks.nonThreeSlotLoadouts=CountQuery 'SELECT COUNT(*) FROM PlayerLoadout p LEFT JOIN (SELECT LoadoutId,COUNT(*) n FROM PlayerLoadoutThrowable GROUP BY LoadoutId) t ON t.LoadoutId=p.Id WHERE t.n IS NULL OR t.n<>3'
 $checks.fiveActiveThrowables=CountQuery "SELECT COUNT(*) FROM CatalogItem WHERE IsActive=1 AND ItemId IN ('throwable.frag','throwable.frag_02','throwable.frag_03','throwable.flash','throwable.smoke') AND AcquisitionSource='Shop' AND UnlockLevel=1"
 $checks.pistolMagazineShop=CountQuery "SELECT COUNT(*) FROM CatalogItem WHERE ItemId='attach.pistol.magazine' AND AcquisitionSource='Shop' AND PriceCoins=1500 AND UnlockLevel=1 AND IsActive=1"
 $checks.pistolOpticCombinations=CountQuery "SELECT COUNT(*) FROM AttachmentCompat WHERE IsImplemented=1 AND WeaponItemId IN ('weapon.service_pistol','weapon.handgun02','weapon.handgun03','weapon.handgun04') AND AttachmentItemId IN ('attach.rifle.optic','attach.lpfp.optic.02')"
 $checks.retiredSuppressorActive=CountQuery "SELECT COUNT(*) FROM CatalogItem WHERE ItemId='attach.rifle.muzzle' AND IsActive=1"
 $checks.retiredSuppressorEquipped=CountQuery "SELECT COUNT(*) FROM PlayerLoadoutAttachment WHERE AttachmentItemId='attach.rifle.muzzle'"
 $checks.retiredPresetActive=CountQuery "SELECT COUNT(*) FROM CatalogItem WHERE ItemId IN ('throwable.standard','throwable.frag_assault') AND IsActive=1"
 $checks.retiredSuppressorRewards=CountQuery "SELECT COUNT(*) FROM PassReward WHERE ItemId='attach.rifle.muzzle'"
 $checks.pistolMagazineReward=CountQuery "SELECT COUNT(*) FROM PassReward WHERE ItemId='attach.pistol.magazine'"
 $checks.persistedEmptySlots=CountQuery 'SELECT COUNT(*) FROM PlayerLoadoutThrowable WHERE ItemId IS NULL'
}finally{$taskConnection.Close();$taskConnection.Dispose();$taskSecrets.Clear()}
$passed=$checks.migration -eq 1 -and $checks.nonThreeSlotLoadouts -eq 0 -and $checks.fiveActiveThrowables -eq 5 -and $checks.pistolMagazineShop -eq 1 -and $checks.pistolOpticCombinations -eq 8 -and $checks.retiredSuppressorActive -eq 0 -and $checks.retiredSuppressorEquipped -eq 0 -and $checks.retiredPresetActive -eq 0 -and $checks.retiredSuppressorRewards -eq 0 -and $checks.pistolMagazineReward -ge 1
@{checks=$checks;passed=$passed;readOnly=$true;atUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 5|Set-Content "$project/Logs/RealTest0930/development-migration.json" -Encoding utf8
$checks|ConvertTo-Json -Depth 4
if(-not $passed){throw 'Development migration validation failed'}
