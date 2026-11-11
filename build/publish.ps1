<#
Pubblica agente e CLI self-contained, file singolo, per ogni RID, e li impacchetta in artifacts\packages (+ SHA256SUMS).
  build\publish.ps1 -Version 0.2.0
  build\publish.ps1 -Only linux-x64,win-x64
Serve il .NET SDK 10 e la rete (runtime pack e ASP.NET Core pack si scaricano al primo publish). Richiede `tar` (Windows 10+).
#>
param(
  [string]$Version = "0.1.0",
  [string]$Out = "artifacts",
  [string[]]$Only = @()
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

# l'agente è leggero e gira ovunque; la CLI completa porta ASP.NET Core e SQLite nativo: niente linux-arm (32 bit)
$agentRids = "win-x64", "linux-x64", "linux-arm64", "linux-arm", "osx-arm64", "osx-x64"
$cliRids = "win-x64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64"

function Pack([string]$project, [string]$name, [string]$rid) {
  Write-Host "==> $name $rid"
  $stage = Join-Path $Out "stage/$name-$rid"
  $base = "$name-$Version-$rid"
  dotnet publish $project -c Release -r $rid "-p:Version=$Version" -o $stage
  if ($LASTEXITCODE -ne 0) { throw "publish fallito: $name $rid" }
  Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force
  if ($rid -like "win-*") {
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath (Join-Path $Out "packages/$base.zip")
  } else {
    # tar (non zip): conserva il bit di esecuzione per Linux e macOS
    tar -C $stage -czf (Join-Path $Out "packages/$base.tar.gz") .
    if ($LASTEXITCODE -ne 0) { throw "tar fallito: $name $rid" }
  }
}

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $Out "stage"), (Join-Path $Out "packages") | Out-Null

foreach ($rid in $agentRids) { if ($Only.Count -eq 0 -or $Only -contains $rid) { Pack "src/LoadTest.Agent/LoadTest.Agent.csproj" "loadtest-agent" $rid } }
foreach ($rid in $cliRids) { if ($Only.Count -eq 0 -or $Only -contains $rid) { Pack "src/LoadTest.Cli/LoadTest.Cli.csproj" "loadtest" $rid } }

Get-ChildItem (Join-Path $Out "packages") | Sort-Object Name | ForEach-Object {
  "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content (Join-Path $Out "SHA256SUMS") -Encoding ascii
Remove-Item (Join-Path $Out "stage") -Recurse -Force
Write-Host "Pacchetti in $Out/packages, somme di controllo in $Out/SHA256SUMS"
