param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$gameBin = Join-Path $GameRoot 'bin\Win64_Shipping_Client'
$referenceRoot = Join-Path $gameBin 'mono\lib\mono\4.7.1-api'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $PSScriptRoot 'PartyEconomyRulesTests.exe'
$compilerArgs = @('/nologo', '/noconfig', '/nostdlib+', '/target:exe', "/out:$testExe",
    "/reference:$(Join-Path $referenceRoot 'mscorlib.dll')",
    "/reference:$(Join-Path $referenceRoot 'System.dll')",
    "/reference:$(Join-Path $referenceRoot 'System.Core.dll')",
    "/reference:$(Join-Path $referenceRoot 'Facades\netstandard.dll')",
    (Join-Path $repoRoot 'PartyEconomyRules.cs'), (Join-Path $repoRoot 'JsonText.cs'),
    (Join-Path $PSScriptRoot 'PartyEconomyRulesTests.cs'))
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Offline test compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Offline regression tests failed.' }

# Read metadata only. This does not load or execute the game's campaign engine.
$cecilPath = Join-Path $gameBin 'mono\lib\mono\msbuild\15.0\bin\Sdks\ILLink.Tasks\tools\net472\Mono.Cecil.dll'
[Reflection.Assembly]::LoadFrom($cecilPath) | Out-Null
$campaign = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameBin 'TaleWorlds.CampaignSystem.dll'))
try {
    $behavior = $campaign.MainModule.Types | Where-Object FullName -eq 'TaleWorlds.CampaignSystem.CampaignBehaviors.RecruitmentCampaignBehavior'
    $method = @($behavior.Methods | Where-Object {
        $_.Name -eq 'GetRecruitVolunteerFromIndividual' -and !$_.IsStatic -and !$_.IsPublic -and
        (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq
        'TaleWorlds.CampaignSystem.Party.MobileParty,TaleWorlds.CampaignSystem.CharacterObject,TaleWorlds.CampaignSystem.Hero,System.Int32'
    })
    if ($method.Count -ne 1) { throw 'The private recruitment API contract changed.' }
    $logic = $campaign.MainModule.Types | Where-Object FullName -eq 'TaleWorlds.CampaignSystem.Inventory.InventoryLogic'
    $price = $logic.Methods | Where-Object { $_.Name -eq 'GetItemPrice' -and $_.Parameters.Count -eq 2 }
    if ($null -eq $price -or !$price.HasBody) { throw 'Inventory price API unavailable.' }
    $instructions = @($price.Body.Instructions | ForEach-Object ToString)
    if ($instructions[0] -notmatch 'ldarg.2' -or $instructions[1] -notmatch 'ldc.i4.0' -or $instructions[2] -notmatch 'ceq') {
        throw 'Recheck the player buying versus market selling price convention.'
    }
    Write-Output 'PASS: installed private recruitment signature and market price direction match the adapter.'
}
finally { $campaign.Dispose() }
$bridgePath = Join-Path $repoRoot 'BannerlordStrategicBridge.mono20.dll'
if (!(Test-Path -LiteralPath $bridgePath)) { throw 'Run bl-dev.ps1 build before the contract checks.' }
$bridge = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($bridgePath)
try {
    $badReferences = @($bridge.MainModule.AssemblyReferences | Where-Object {
        $_.Name -eq 'System.Private.CoreLib' -or ($_.Name -eq 'System.Runtime' -and $_.Version.Major -gt 4)
    })
    if ($badReferences.Count) { throw 'Bridge references an incompatible modern .NET runtime.' }
    $framework = @($bridge.MainModule.AssemblyReferences | Where-Object { $_.Name -eq 'mscorlib' -and $_.Version.ToString() -eq '4.0.0.0' })
    if ($framework.Count -ne 1) {
        throw 'Expected the Mono-compatible mscorlib 4.0 assembly reference.'
    }
    # The compiler can resolve all forwarded types without emitting a reference
    # to the facade itself, so check the build input rather than requiring an
    # unused facade reference in the output DLL.
    $facade = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $referenceRoot 'Facades\netstandard.dll'))
    try {
        if ($facade.Name.Version.ToString() -ne '2.0.0.0') { throw 'Expected the bundled .NET Standard 2.0 facade.' }
    }
    finally { $facade.Dispose() }
    Write-Output 'PASS: bridge assembly targets the bundled Mono/.NET Standard 2.0 references.'
}
finally { $bridge.Dispose() }
