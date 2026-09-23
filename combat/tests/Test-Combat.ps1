param([string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference = 'Stop'
$combatRoot = Split-Path $PSScriptRoot -Parent
$bridge = Join-Path $combatRoot 'out\BannerlordCombatBridge\bin\Win64_Shipping_Client\BannerlordCombatBridge.dll'
if (!(Test-Path -LiteralPath $bridge)) { throw 'Run combat/build.ps1 first.' }
$framework = Join-Path $GameRoot 'bin\Win64_Shipping_Client\mono\lib\mono\4.7.1-api'
$testOutput = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
Copy-Item -LiteralPath $bridge -Destination $testOutput -Force
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xml.dll',
    'System.Runtime.Serialization.dll', 'Facades\netstandard.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += '/reference:' + $bridge
foreach ($test in @('ProtocolTests', 'InputArgumentsTests', 'FormationArgumentsTests')) {
    $exe = Join-Path $testOutput ($test + '.exe')
    $compilerArgs = @('/nologo', '/noconfig', '/nostdlib+', '/target:exe', ('/out:' + $exe)) + $references + (Join-Path $PSScriptRoot ($test + '.cs'))
    & 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw "$test compilation failed." }
    & $exe $GameRoot
    if ($LASTEXITCODE -ne 0) { throw "$test failed." }
}
& (Join-Path $PSScriptRoot 'Test-NativeContract.ps1') -GameRoot $GameRoot -BridgePath $bridge
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'test_client.py')) {
    & python -m unittest discover -s $PSScriptRoot -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) { throw 'Combat client tests failed.' }
}
Write-Output 'PASS: combat offline suites. No game state or live installation was changed.'
