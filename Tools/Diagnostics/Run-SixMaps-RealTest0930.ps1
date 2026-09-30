$ErrorActionPreference='Stop'
$checks=@(
 @{map='arena';scene='Arena';mode='TDM'},
 @{map='map_01';scene='Map_Stackyard';mode='TDM'},
 @{map='map_02';scene='Map_Depot55';mode='KillRace'},
 @{map='map_03';scene='Map_Ridgeline';mode='TDM'},
 @{map='map_04';scene='Map_TrainingYard';mode='TDM'},
 @{map='map_05';scene='Map_NightRelay';mode='KillRace'}
)
foreach($check in $checks){
 $runName='final-'+$check.map.Replace('_','-')
 try{
  & "$PSScriptRoot/Start-RealTest0930.ps1" -RunName $runName -UseBuiltAudit -MapId $check.map -Mode $check.mode
  & python "$PSScriptRoot/Run-Map-RealTest0930.py" "Logs/RealTest0930/$runName" $check.scene
  $checkExit=$LASTEXITCODE
  Write-Output "MAP_RESULT $runName exit=$checkExit"
 }finally{& "$PSScriptRoot/Stop-RealTest0930.ps1" -RunName $runName}
}
