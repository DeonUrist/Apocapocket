param([string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\2020.3.49f1\Editor\Unity.exe')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'verification\.unity-project'
New-Item -ItemType Directory -Path (Join-Path $project 'Assets\Editor'),(Join-Path $project 'Packages'),(Join-Path $project 'ProjectSettings') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ExtendedCardGraphic.cs') -Destination (Join-Path $project 'Assets\ExtendedCardGraphic.cs') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verification\UnityMeshVerifier.cs') -Destination (Join-Path $project 'Assets\Editor\UnityMeshVerifier.cs') -Force
[System.IO.File]::WriteAllText((Join-Path $project 'Packages\manifest.json'), '{"dependencies":{"com.unity.ugui":"1.0.0"}}')
[System.IO.File]::WriteAllText((Join-Path $project 'ProjectSettings\ProjectVersion.txt'), "m_EditorVersion: 2020.3.49f1`n")
$log = Join-Path $project 'verification.log'
$arguments = @('-batchmode', '-nographics', '-projectPath', ('"' + $project + '"'), '-executeMethod', 'UnityMeshVerifier.Run', '-logFile', ('"' + $log + '"'))
$process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or -not (Select-String -LiteralPath $log -SimpleMatch 'APOCAPOCKET_UI_PASS' -Quiet)) {
    throw "Native Unity UI verification did not complete. Check $log (an activated Unity editor license is required)."
}
Select-String -LiteralPath $log -Pattern 'Verified native UI mesh|APOCAPOCKET_UI_PASS' | ForEach-Object { $_.Line }
