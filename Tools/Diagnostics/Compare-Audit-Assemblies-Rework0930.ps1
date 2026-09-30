$ErrorActionPreference='Stop'
Add-Type -Path 'E:/Unity6/Editor/Data/Managed/Unity.Cecil.dll'
function DescribeType($type) {
 Write-Output ($type.FullName+'|'+$type.Attributes+'|'+$type.BaseType)
 foreach($field in $type.Fields){Write-Output ($field.FullName+'|'+$field.Attributes+'|'+$field.Constant)}
 foreach($method in $type.Methods){
  Write-Output ($method.FullName+'|'+$method.Attributes+'|'+$method.ImplAttributes)
  foreach($attribute in $method.CustomAttributes){Write-Output ($attribute.AttributeType.FullName+'|'+[string]::Join(',',@($attribute.ConstructorArguments|ForEach-Object {[string]$_.Value})))}
  if($method.HasBody){
   Write-Output ('maxstack='+$method.Body.MaxStackSize+'|init='+$method.Body.InitLocals)
   foreach($variable in $method.Body.Variables){Write-Output ([string]$variable.VariableType)}
   foreach($instruction in $method.Body.Instructions){Write-Output $instruction.ToString()}
   foreach($handler in $method.Body.ExceptionHandlers){Write-Output ([string]$handler.HandlerType+'|'+$handler.TryStart+'|'+$handler.TryEnd+'|'+$handler.HandlerStart+'|'+$handler.HandlerEnd+'|'+$handler.CatchType)}
  }
 }
 foreach($nested in $type.NestedTypes){DescribeType $nested}
}
$results=@()
foreach($role in @('Server','Client')){
 $app=if($role -eq 'Server'){'UnityFpsDedicatedServer'}else{'UnityFpsClient'}
 foreach($name in @('Game.Gameplay.dll','Game.Presentation.dll','Game.UI.dll','Game.Core.dll','Game.Account.dll')){
  $paths=@("Builds/RealTest0930-Rework-Final/$role/${app}_Data/Managed/$name","Builds/RealTest0930-Rework-Final/Audit/$role/${app}_Data/Managed/$name")
  $outputs=@()
  foreach($path in $paths){
   $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($path))
   try{$outputs+=,@($assembly.MainModule.Types|ForEach-Object {DescribeType $_})}finally{$assembly.Dispose()}
  }
  $differences=@(Compare-Object $outputs[0] $outputs[1])
  $results+=@{role=$role;assembly=$name;binaryIdentical=(Get-FileHash $paths[0]).Hash -eq (Get-FileHash $paths[1]).Hash;methodBodiesAndDefinitionsIdentical=$differences.Count -eq 0;differenceLines=$differences.Count}
  if($differences.Count){$differences|ConvertTo-Json -Depth 4|Set-Content "Logs/RealTest0930-Rework/$role-$name-diff.json" -Encoding utf8}
 }
}
$results|ConvertTo-Json -Depth 4|Set-Content 'Logs/RealTest0930-Rework/audit-assembly-equivalence.json' -Encoding utf8
if(@($results|Where-Object {-not $_.methodBodiesAndDefinitionsIdentical}).Count){throw 'Audit assembly contains changed method definitions.'}
Write-Output 'TEN_GAME_ASSEMBLIES_EQUIVALENT'
