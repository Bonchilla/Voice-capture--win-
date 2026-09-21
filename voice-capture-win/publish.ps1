$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src/VoiceCapture.Windows/VoiceCapture.Windows.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$output = Join-Path $root ('artifacts/publish/' + $version + '/win-x64')
$archive = Join-Path $root ('artifacts/VoiceCapture-' + $version + '-win-x64.zip')
dotnet test (Join-Path $root 'tests/VoiceCapture.Tests') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item (Join-Path $root 'README.md') $output -Force
Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') $output -Force
$licenseOutput = Join-Path $output 'licenses'
New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
Get-ChildItem (Join-Path $root 'licenses') -File | Where-Object { $_.Name -notin @('Whisper.net.txt', 'whisper.cpp.txt') } | Copy-Item -Destination $licenseOutput -Force
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
Write-Output ('Application folder: ' + $output)
Write-Output ('Archive: ' + $archive)
