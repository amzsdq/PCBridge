param(
  [Parameter(Mandatory=$true)][string]$InputZip,
  [Parameter(Mandatory=$true)][string]$OutputZip,
  [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = [IO.Path]::GetFullPath($RepoRoot)
$input = [IO.Path]::GetFullPath($InputZip)
$output = [IO.Path]::GetFullPath($OutputZip)
if (-not (Test-Path -LiteralPath $input -PathType Leaf)) { throw "Input payload ZIP not found: $input" }
if ($input -eq $output) { throw 'InputZip and OutputZip must be different; the base payload is preserved by design.' }

$sourceRoot = Join-Path $repo 'provider\chatgpt-extension'
$files = @('manifest.json','background.js','content.js','chatgpt-dom.js')
foreach ($name in $files) {
  $path = Join-Path $sourceRoot $name
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Companion source missing: $path" }
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
Copy-Item -LiteralPath $input -Destination $output -Force

$zip = [IO.Compression.ZipFile]::Open($output,[IO.Compression.ZipArchiveMode]::Update)
try {
  $prefix = 'provider/chatgpt-extension/'
  @($zip.Entries | Where-Object { $_.FullName.StartsWith($prefix,[StringComparison]::Ordinal) }) |
    ForEach-Object { $_.Delete() }

  foreach ($name in $files) {
    $entry = $prefix + $name
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
      $zip,
      (Join-Path $sourceRoot $name),
      $entry,
      [IO.Compression.CompressionLevel]::Optimal
    ) | Out-Null
  }
}
finally {
  $zip.Dispose()
}

function Hash-Stream([IO.Stream]$Stream) {
  $sha = [Security.Cryptography.SHA256]::Create()
  try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-','') }
  finally { $sha.Dispose() }
}

$verify = [IO.Compression.ZipFile]::OpenRead($output)
try {
  $seen = @()
  foreach ($name in $files) {
    $entryName = 'provider/chatgpt-extension/' + $name
    $entry = $verify.GetEntry($entryName)
    if ($null -eq $entry) { throw "Companion entry missing after package: $entryName" }

    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $sourceRoot $name)).Hash
    $stream = $entry.Open()
    try { $entryHash = Hash-Stream $stream } finally { $stream.Dispose() }
    if ($sourceHash -ne $entryHash) { throw "Companion entry hash mismatch: $entryName" }
    $seen += $entryName
  }

  $extra = @($verify.Entries | Where-Object {
    $_.FullName.StartsWith('provider/chatgpt-extension/',[StringComparison]::Ordinal) -and
    $seen -notcontains $_.FullName
  })
  if ($extra.Count -ne 0) { throw 'Unexpected companion entries remained in packaged payload.' }
}
finally {
  $verify.Dispose()
}

[pscustomobject]@{
  ok = $true
  input_zip = $input
  output_zip = $output
  companion_entries = $files.Count
  output_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $output).Hash
} | ConvertTo-Json -Compress
