param(
  [Parameter(Mandatory=$true)][string]$BasePayload,
  [Parameter(Mandatory=$true)][string]$RepositoryRoot,
  [Parameter(Mandatory=$true)][string]$OutputPayload
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$base = [IO.Path]::GetFullPath($BasePayload)
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$out = [IO.Path]::GetFullPath($OutputPayload)

if (-not (Test-Path -LiteralPath $base -PathType Leaf)) { throw "Base payload not found: $base" }
if ([StringComparer]::OrdinalIgnoreCase.Equals($base,$out)) { throw 'OutputPayload must differ from BasePayload.' }

$files = @(
  'provider/chatgpt-extension/manifest.json',
  'provider/chatgpt-extension/background.js',
  'provider/chatgpt-extension/content.js',
  'provider/chatgpt-extension/chatgpt-dom.js'
)

foreach ($relative in $files) {
  $source = Join-Path $repo ($relative -replace '/','\')
  if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required companion file missing: $relative" }
}

$outDir = Split-Path -Parent $out
if ($outDir) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
Copy-Item -LiteralPath $base -Destination $out -Force

$zip = [IO.Compression.ZipFile]::Open($out,[IO.Compression.ZipArchiveMode]::Update)
try {
  foreach ($relative in $files) {
    $existing = $zip.GetEntry($relative)
    if ($existing) { $existing.Delete() }
    $source = Join-Path $repo ($relative -replace '/','\')
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
      $zip,$source,$relative,[IO.Compression.CompressionLevel]::Optimal
    ) | Out-Null
  }
} finally {
  $zip.Dispose()
}

$check = [IO.Compression.ZipFile]::OpenRead($out)
try {
  $entries = @($check.Entries | ForEach-Object FullName)
  foreach ($relative in $files) {
    if ($entries -notcontains $relative) { throw "Payload verification failed: $relative" }
  }
  $manifestEntry = $check.GetEntry('provider/chatgpt-extension/manifest.json')
  $reader = New-Object IO.StreamReader($manifestEntry.Open(),[Text.Encoding]::UTF8)
  try { $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
  if ($manifest -notmatch '"PCBridge M1 ChatGPT Companion"' -or $manifest -notmatch '"manifest_version"\s*:\s*3') {
    throw 'Payload companion manifest verification failed.'
  }
} finally {
  $check.Dispose()
}

$result = [ordered]@{
  ok = $true
  output = $out
  bytes = (Get-Item -LiteralPath $out).Length
  sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $out).Hash
  added = $files
}
$result | ConvertTo-Json -Depth 4
