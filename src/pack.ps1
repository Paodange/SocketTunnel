param(
  [string[]]$Runtimes = @("win-x64","linux-x64","osx-x64")
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

dotnet --info | Out-Null
dotnet restore

$dist = Join-Path $Root "dist"
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Path $dist | Out-Null

foreach ($rid in $Runtimes) {
  Write-Host "Publishing $rid ..."
  $outDir = Join-Path $dist $rid
  dotnet publish -c Release -r $rid --self-contained true -o $outDir `
    /p:PublishSingleFile=true /p:PublishAot=true

  $zip = Join-Path $dist ("SocksTunnel-" + $rid + ".zip")
  if (Test-Path $zip) { Remove-Item $zip }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::CreateFromDirectory($outDir, $zip)
  Write-Host "Packed $zip"
}

Write-Host "Done. Zips are under dist/."