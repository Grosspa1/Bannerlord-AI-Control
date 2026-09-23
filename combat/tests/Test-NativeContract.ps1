param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord',
    [string]$BridgePath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'out\BannerlordCombatBridge\bin\Win64_Shipping_Client\BannerlordCombatBridge.dll')
)
$ErrorActionPreference = 'Stop'
$gameBin = Join-Path $GameRoot 'bin\Win64_Shipping_Client'
$cecil = Join-Path $gameBin 'mono\lib\mono\msbuild\15.0\bin\Sdks\ILLink.Tasks\tools\net472\Mono.Cecil.dll'
[Reflection.Assembly]::LoadFrom($cecil) | Out-Null
$native = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameBin 'TaleWorlds.MountAndBlade.dll'))
$view = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameRoot 'Modules\Native\bin\Win64_Shipping_Client\TaleWorlds.MountAndBlade.View.dll'))
$bridge = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($BridgePath)
function Find-Call($method, [string]$pattern) {
    @($method.Body.Instructions | Where-Object {
        $_.OpCode.Code -in @('Call','Callvirt') -and $_.Operand.ToString() -match $pattern
    })
}
try {
    $mission = $native.MainModule.Types | Where-Object FullName -eq 'TaleWorlds.MountAndBlade.Mission'
    $after = $mission.Methods | Where-Object Name -eq 'AfterStart'
    $beforeHook = @(Find-Call $after 'MBSubModuleBase::OnBeforeMissionBehaviorInitialize\(')
    $behaviorInit = @(Find-Call $after 'MissionBehavior::OnBehaviorInitialize\(')
    $lateHook = @(Find-Call $after 'MBSubModuleBase::OnMissionBehaviorInitialize\(')
    if ($beforeHook.Count -ne 1 -or $behaviorInit.Count -ne 1 -or $lateHook.Count -ne 1 -or
        $beforeHook[0].Offset -ge $behaviorInit[0].Offset -or $behaviorInit[0].Offset -ge $lateHook[0].Offset) {
        throw 'Native behavior initialization order changed; re-audit the attachment hook.'
    }
    $pre = $mission.Methods | Where-Object Name -eq 'OnPreTick'
    $tick = @(Find-Call $pre 'MissionBehavior::OnPreMissionTick\(')
    if ($tick.Count -ne 1) { throw 'Native pre-tick dispatch changed.' }
    $instructions = @($pre.Body.Instructions)
    $tickIndex = [Array]::IndexOf($instructions, $tick[0])
    if ($instructions[$tickIndex+1].OpCode.Code -ne 'Ldloc_0' -or
        $instructions[$tickIndex+2].OpCode.Code -ne 'Ldc_I4_1' -or
        $instructions[$tickIndex+3].OpCode.Code -ne 'Sub' -or
        $instructions[$tickIndex+4].OpCode.Code -ne 'Stloc_0') {
        throw 'Native pre-tick loop no longer decrements its behavior index.'
    }
    $controller = $view.MainModule.Types | Where-Object Name -eq 'MissionMainAgentController'
    $inputTick = $controller.Methods | Where-Object Name -eq 'OnPreMissionTick'
    $disabled = @(Find-Call $inputTick '::get_IsDisabled\(')
    $controlCall = @(Find-Call $inputTick '::ControlTick\(')
    $lookCall = @(Find-Call $inputTick '::LookTick\(')
    if ($disabled.Count -ne 1 -or $controlCall.Count -ne 1 -or $lookCall.Count -ne 1 -or
        $disabled[0].Next.OpCode.Code -notin @('Brtrue','Brtrue_S') -or
        $disabled[0].Next.Operand.Offset -le $lookCall[0].Offset -or
        $disabled[0].Offset -ge $controlCall[0].Offset) {
        throw 'Native controller disabling no longer skips both movement and look.'
    }
    $screen = $view.MainModule.Types | Where-Object Name -eq 'MissionScreen'
    $camera = $screen.Methods | Where-Object Name -eq 'UpdateCamera'
    $cameraWrites = @(Find-Call $camera '::set_IsDisabled\(')
    if ($cameraWrites.Count -ne 2 -or
        @($cameraWrites | Where-Object { $_.Previous.OpCode.Code -eq 'Ldc_I4_0' }).Count -ne 1 -or
        @($cameraWrites | Where-Object { $_.Previous.OpCode.Code -eq 'Ldc_I4_1' }).Count -ne 1) {
        throw 'Native camera control reset changed; re-audit per-frame ownership.'
    }
    $submodule = $bridge.MainModule.Types | Where-Object FullName -eq 'BannerlordCombatBridge.SubModule'
    if (@($submodule.Methods | Where-Object Name -eq 'OnBeforeMissionBehaviorInitialize').Count -ne 1 -or
        @($submodule.Methods | Where-Object Name -eq 'OnMissionBehaviorInitialize').Count -ne 0) {
        throw 'The combat behavior must attach through the early hook.'
    }
    $adapter = $bridge.MainModule.Types | Where-Object FullName -eq 'BannerlordCombatBridge.PlayerInput'
    foreach ($method in @($adapter.Methods | Where-Object HasBody)) {
        if (@(Find-Call $method 'Agent::set_Controller\(').Count) { throw 'Player input must not take over the AI controller.' }
    }
    $adapterTick = $adapter.Methods | Where-Object Name -eq 'Tick'
    if (@(Find-Call $adapterTick '::set_IsDisabled\(').Count -ne 1) { throw 'Input must suppress the native controller each pre-tick.' }
    $badRefs = @($bridge.MainModule.AssemblyReferences | Where-Object {
        $_.Name -eq 'System.Private.CoreLib' -or ($_.Name -eq 'System.Runtime' -and $_.Version.Major -gt 4)
    })
    if ($badRefs.Count -or @($bridge.MainModule.AssemblyReferences | Where-Object {
        $_.Name -eq 'mscorlib' -and $_.Version.ToString() -eq '4.0.0.0'
    }).Count -ne 1) { throw 'The module no longer targets the bundled Mono-compatible runtime.' }
    $facade = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $gameBin 'mono\lib\mono\4.7.1-api\Facades\netstandard.dll'))
    try { if ($facade.Name.Version.ToString() -ne '2.0.0.0') { throw 'Expected bundled .NET Standard 2.0 facade.' } }
    finally { $facade.Dispose() }
    Write-Output 'PASS: installed initialization/pre-tick order, controller gating, camera reset, adapter ownership and runtime contracts.'
}
finally { $native.Dispose(); $view.Dispose(); $bridge.Dispose() }
