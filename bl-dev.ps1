param(
    [Parameter(Position=0)]
    [ValidateSet('status','sync','build','deploy','logs','command','push')]
    [string]$Action = 'status',
    [Parameter(Position=1, ValueFromRemainingArguments=$true)]
    [string[]]$Args
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$BridgeRoot = 'C:\Users\Public\BannerlordBridge'
$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord'
$InstalledDll = Join-Path $GameRoot 'Modules\BannerlordStrategicBridge\bin\Win64_Shipping_Client\BannerlordStrategicBridge.dll'
$BuiltDll = Join-Path $Root 'BannerlordStrategicBridge.mono20.dll'
$Rsp = Join-Path $Root 'compile_mono20.rsp'
$Blctl = Join-Path $Root 'blctl.py'
$Git = 'C:\Program Files\Git\cmd\git.exe'
$Csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

function Assert-Tool([string]$Path, [string]$Name) {
    if (!(Test-Path $Path)) { throw "$Name not found at $Path" }
}

function Invoke-Git {
    Assert-Tool $Git 'Git'
    & $Git -C $Root @args
    if ($LASTEXITCODE -ne 0) { throw "git failed with exit code $LASTEXITCODE" }
}

switch ($Action) {
    'status' {
        Write-Host '=== Git ==='
        if (Test-Path (Join-Path $Root '.git')) { & $Git -C $Root status --short --branch }
        else { Write-Host 'Repository not initialized.' }

        Write-Host '=== Bannerlord ==='
        $game = Get-Process Bannerlord, Bannerlord.Native -ErrorAction SilentlyContinue
        if ($game) { $game | Select-Object ProcessName, Id | Format-Table -AutoSize }
        else { Write-Host 'Not running.' }

        Write-Host '=== Bridge State ==='
        $state = Join-Path $BridgeRoot 'state.json'
        if (Test-Path $state) { Get-Content $state -Raw }
        else { Write-Host 'state.json not present.' }
    }

    'sync' {
        Invoke-Git pull --ff-only
    }

    'build' {
        Assert-Tool $Csc 'C# compiler'
        Assert-Tool $Rsp 'compile response file'
        Push-Location $Root
        try {
            & $Csc /noconfig "@$Rsp"
            if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
        }
        finally {
            Pop-Location
        }
        if (!(Test-Path $BuiltDll)) { throw 'Compiler returned success but output DLL is missing.' }
        Write-Host "Built $BuiltDll"
    }

    'deploy' {
        $game = Get-Process Bannerlord, Bannerlord.Native -ErrorAction SilentlyContinue
        if ($game) { throw 'Bannerlord is running. Exit the game before deploying a new bridge DLL.' }
        if (!(Test-Path $BuiltDll)) { throw "Build output missing: $BuiltDll" }
        $destDir = Split-Path $InstalledDll -Parent
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        if (Test-Path $InstalledDll) {
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            Copy-Item $InstalledDll "$InstalledDll.$stamp.bak" -Force
        }
        Copy-Item $BuiltDll $InstalledDll -Force
        Write-Host "Deployed $InstalledDll"
    }

    'logs' {
        $log = Join-Path $BridgeRoot 'bridge.log'
        if (!(Test-Path $log)) { throw "Bridge log not found: $log" }
        Get-Content $log -Tail 120
    }

    'command' {
        if (!$Args -or $Args.Count -lt 1) { throw 'Usage: bl-dev.ps1 command <verb> [argument]' }
        $verb = $Args[0]
        $rest = if ($Args.Count -gt 1) { $Args[1..($Args.Count-1)] } else { @() }
        Assert-Tool $Blctl 'blctl.py'
        & python $Blctl $verb @rest
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    'push' {
        if (!(Test-Path (Join-Path $Root '.git'))) { throw 'Repository is not initialized.' }
        $message = if ($Args -and $Args.Count -gt 0) { $Args -join ' ' } else { 'Update Bannerlord strategic bridge' }

        & $Git -C $Root add -A
        if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

        & $Git -C $Root diff --cached --quiet
        if ($LASTEXITCODE -eq 1) {
            & $Git -C $Root commit -m $message
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
        } elseif ($LASTEXITCODE -ne 0) {
            throw 'git diff failed.'
        } else {
            Write-Host 'Nothing new to commit.'
        }

        $branch = (& $Git -C $Root rev-parse --abbrev-ref HEAD).Trim()
        if ([string]::IsNullOrWhiteSpace($branch) -or $branch -eq 'HEAD') { throw 'Cannot push detached HEAD.' }
        & $Git -C $Root push -u origin $branch
        if ($LASTEXITCODE -ne 0) { throw 'git push failed.' }
    }
}
