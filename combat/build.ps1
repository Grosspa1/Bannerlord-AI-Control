param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord'
)
$ErrorActionPreference = 'Stop'
$gameBin = Join-Path $GameRoot 'bin\Win64_Shipping_Client'
$framework = Join-Path $gameBin 'mono\lib\mono\4.7.1-api'
$output = Join-Path $PSScriptRoot 'out\BannerlordCombatBridge'
$outputBin = Join-Path $output 'bin\Win64_Shipping_Client'
New-Item -ItemType Directory -Force -Path $outputBin | Out-Null
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xml.dll',
    'System.Runtime.Serialization.dll', 'Facades\netstandard.dll') | ForEach-Object { Join-Path $framework $_ }
$references += @('TaleWorlds.MountAndBlade.dll', 'TaleWorlds.Core.dll', 'TaleWorlds.Library.dll',
    'TaleWorlds.Engine.dll', 'TaleWorlds.DotNet.dll', 'TaleWorlds.ObjectSystem.dll',
    'TaleWorlds.InputSystem.dll', 'TaleWorlds.ScreenSystem.dll', 'TaleWorlds.Localization.dll',
    'TaleWorlds.MountAndBlade.ViewModelCollection.dll',
    'System.Numerics.Vectors.dll') | ForEach-Object { Join-Path $gameBin $_ }
$references += Join-Path $GameRoot 'Modules\Native\bin\Win64_Shipping_Client\TaleWorlds.MountAndBlade.View.dll'
foreach ($reference in $references) {
    if (!(Test-Path -LiteralPath $reference)) { throw "Missing installed reference: $reference" }
}
$compilerArgs = @('/nologo', '/noconfig', '/nostdlib+', '/target:library',
    ('/out:' + (Join-Path $outputBin 'BannerlordCombatBridge.dll')))
$compilerArgs += $references | ForEach-Object { '/reference:' + $_ }
$compilerArgs += @('CombatProtocol.cs', 'PlayerInput.cs', 'FormationOrders.cs', 'SubModule.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Combat module compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'module\SubModule.xml') -Destination $output -Force
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'combatctl.py') -Destination $output -Force
$archive = Join-Path $PSScriptRoot 'out\BannerlordCombatBridge-v0.1.0.zip'
Compress-Archive -LiteralPath $output -DestinationPath $archive -Force
Write-Output "Built package: $archive"
Get-FileHash -LiteralPath (Join-Path $outputBin 'BannerlordCombatBridge.dll') -Algorithm SHA256
