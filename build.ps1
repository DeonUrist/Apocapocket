param(
    [string]$GameDir = 'E:\SteamLibrary\steamapps\common\Apocalypter',
    [string]$MonoDir = 'C:\Program Files\Unity\Hub\Editor\2020.3.49f1\Editor\Data\MonoBleedingEdge',
    [string]$Output = 'Apocapocket.dll',
    [switch]$Deploy
)
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameDir 'Apocalypter_Data\Managed'
$core = Join-Path $GameDir 'BepInEx\core'
$compiler = Join-Path $MonoDir 'lib\mono\4.5\mcs.exe'
$runtime = Join-Path $MonoDir 'bin\mono.exe'
$arguments = @('-nostdlib', '-noconfig', '-target:library', '-langversion:7', '-optimize+', ('-out:' + $Output))
$names = @('mscorlib', 'System', 'System.Core', 'netstandard', 'UnityEngine', 'UnityEngine.CoreModule', 'UnityEngine.PhysicsModule', 'UnityEngine.AudioModule', 'UnityEngine.InputLegacyModule', 'Unity.InputSystem', 'UnityEngine.ImageConversionModule', 'UnityEngine.JSONSerializeModule', 'UnityEngine.UI', 'UnityEngine.UIModule', 'UnityEngine.TextRenderingModule', 'PlayMaker', 'Assembly-CSharp', 'Assembly-CSharp-firstpass')
foreach ($name in $names) { $arguments += '-r:' + (Join-Path $managed ($name + '.dll')) }
foreach ($name in @('BepInEx', '0Harmony')) { $arguments += '-r:' + (Join-Path $core ($name + '.dll')) }
$arguments += @('Plugin.cs', 'Inventory.cs', 'InventoryWorld.cs', 'InputPatches.cs', 'Persistence.cs', 'Icons.cs', 'ExtraSlots.cs', 'ExtendedCardGraphic.cs', 'Keybinds.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $runtime $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "mcs failed ($LASTEXITCODE)" }
if ($Deploy) {
    $destination = Join-Path $GameDir 'BepInEx\plugins\Apocapocket\Apocapocket.dll'
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $Output -Destination $destination -Force
    Write-Output "Deployed $destination"
}
