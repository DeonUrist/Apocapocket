param(
    [string]$GameDir = 'E:\SteamLibrary\steamapps\common\Apocalypter',
    [string]$MonoDir = 'C:\Program Files\Unity\Hub\Editor\2020.3.49f1\Editor\Data\MonoBleedingEdge'
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project verification/Transactions.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Transaction/persistence verification failed' }
    & ./build.ps1 -GameDir $GameDir -MonoDir $MonoDir
    $managed = Join-Path $GameDir 'Apocalypter_Data\Managed'
    $core = Join-Path $GameDir 'BepInEx\core'
    $runtime = Join-Path $MonoDir 'bin\mono.exe'
    $compiler = Join-Path $MonoDir 'lib\mono\4.5\mcs.exe'
    $arguments = @('-nostdlib', '-noconfig', '-langversion:7', '-out:verification/HarmonySmoke.exe')
    foreach ($name in @('mscorlib', 'System', 'System.Core')) { $arguments += '-r:' + (Join-Path $managed ($name + '.dll')) }
    foreach ($name in @('BepInEx', '0Harmony')) { $arguments += '-r:' + (Join-Path $core ($name + '.dll')) }
    $arguments += 'verification/HarmonySmoke.cs'
    & $runtime $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Harmony verification build failed' }
    $previousMonoPath = $env:MONO_PATH
    try {
        $env:MONO_PATH = $managed + ';' + $core
        & $runtime verification/HarmonySmoke.exe Apocapocket.dll
        if ($LASTEXITCODE -ne 0) { throw 'Harmony verification failed' }
    }
    finally { $env:MONO_PATH = $previousMonoPath }
}
finally { Pop-Location }
