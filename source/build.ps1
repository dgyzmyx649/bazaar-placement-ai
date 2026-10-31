param([Parameter(Mandatory=$true)][string]$GameDir)
$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'BazaarPlacementAI.csproj') -c Release --nologo ("-p:GameDir=" + $GameDir)
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output 'Managed plugin built under bin/Release/net472. Package only Windows x64 ONNX Runtime native files and the separately licensed model files.'
