param(
  [Parameter(Mandatory=$true)][string]$Root,
  [ValidateSet('ensure','status')][string]$Action = 'ensure'
)

$ErrorActionPreference = 'Stop'
Import-Module BitsTransfer
Add-Type -AssemblyName System.IO.Compression.FileSystem

$rootPath = [IO.Path]::GetFullPath($Root)
$runtimeRoot = Join-Path $rootPath 'browser-runtime'
$finalRoot = Join-Path $runtimeRoot 'chrome-for-testing'
$chrome = Join-Path $finalRoot 'chrome-win64\chrome.exe'
$metaPath = Join-Path $runtimeRoot 'stable-source.json'
$zipPath = Join-Path $runtimeRoot 'chrome-win64.zip'
New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null

function Result([string]$state,[object]$extra) {
  $base = [ordered]@{
    ok = $state -ne 'error'
    state = $state
    root = $finalRoot
    chrome = $chrome
  }
  if ($null -ne $extra) {
    foreach ($p in $extra.PSObject.Properties) { $base[$p.Name] = $p.Value }
  }
  [pscustomobject]$base | ConvertTo-Json -Compress
}

function Chrome-Version([string]$path) {
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return '' }
  return (Get-Item -LiteralPath $path).VersionInfo.ProductVersion
}

if (Test-Path -LiteralPath $chrome -PathType Leaf) {
  Result 'ready' ([pscustomobject]@{
    version = Chrome-Version $chrome
    sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $chrome).Hash
  })
  exit 0
}

$meta = Invoke-RestMethod -Uri 'https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions-with-downloads.json' -UseBasicParsing
$stable = $meta.channels.Stable
$item = $stable.downloads.chrome | Where-Object { $_.platform -eq 'win64' } | Select-Object -First 1
if ($null -eq $item) { throw 'Stable win64 Chrome for Testing URL missing.' }

[pscustomobject]@{
  version = $stable.version
  revision = $stable.revision
  url = $item.url
  utc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json -Compress | Set-Content -LiteralPath $metaPath -Encoding UTF8

$jobName = 'PCBridge-M1-CFT-' + $stable.version
$jobs = @(Get-BitsTransfer -ErrorAction SilentlyContinue | Where-Object {
  $_.DisplayName -like 'PCBridge-M1-CFT-*' -and
  $_.JobState.ToString() -notin @('Cancelled','Acknowledged')
})
$job = $jobs | Where-Object { $_.DisplayName -eq $jobName } | Select-Object -First 1

if ($null -eq $job -and $Action -eq 'ensure') {
  $bitsArgs = @{
    Source = $item.url
    Destination = $zipPath
    DisplayName = $jobName
    Description = 'PCBridge M1 isolated Chrome for Testing runtime'
    Priority = 'Low'
    Asynchronous = $true
  }
  $job = Start-BitsTransfer @bitsArgs
}

if ($null -eq $job) {
  Result 'not_started' ([pscustomobject]@{version=$stable.version;url=$item.url})
  exit 0
}

$state = $job.JobState.ToString()
if ($state -eq 'Transferred') {
  Complete-BitsTransfer -BitsJob $job
  if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { throw 'BITS completed but Chrome ZIP is missing.' }

  $zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash
  $extract = Join-Path $runtimeRoot ('.extract-' + [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path $extract | Out-Null

  [IO.Compression.ZipFile]::ExtractToDirectory($zipPath,$extract)
  $candidate = Join-Path $extract 'chrome-win64\chrome.exe'
  if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
    throw "Chrome ZIP did not contain chrome-win64\chrome.exe. Staging retained at: $extract"
  }

  $version = Chrome-Version $candidate
  if (-not $version.StartsWith($stable.version,[StringComparison]::OrdinalIgnoreCase)) {
    throw "Extracted Chrome version mismatch. expected=$($stable.version) actual=$version staging=$extract"
  }
  if (Test-Path -LiteralPath $finalRoot) {
    throw "Existing browser runtime appeared during extraction; refusing overwrite. Staging retained at: $extract"
  }

  Move-Item -LiteralPath $extract -Destination $finalRoot
  Result 'ready' ([pscustomobject]@{
    version = Chrome-Version $chrome
    zip_sha256 = $zipHash
    chrome_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $chrome).Hash
  })
  exit 0
}

$bytesTotal = [Int64]$job.BytesTotal
$bytesDone = [Int64]$job.BytesTransferred
$percent = if ($bytesTotal -gt 0) { [Math]::Round(($bytesDone * 100.0) / $bytesTotal,2) } else { 0.0 }
$errorText = [string]$job.ErrorDescription

if ($state -eq 'TransientError' -and $errorText -match '게임 모드|game mode') {
  $normalized = 'paused_game_mode'
} elseif ($state -eq 'TransientError') {
  $normalized = 'paused_transient'
} elseif ($state -eq 'Error') {
  $normalized = 'error'
} else {
  $normalized = $state.ToLowerInvariant()
}

Result $normalized ([pscustomobject]@{
  version = $stable.version
  bits_state = $state
  priority = $job.Priority.ToString()
  bytes_transferred = $bytesDone
  bytes_total = $bytesTotal
  percent = $percent
  error = $errorText
  url = $item.url
})
if ($normalized -eq 'error') { exit 2 }
