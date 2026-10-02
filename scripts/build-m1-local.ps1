param(
  [Parameter(Mandatory=$true)][string]$BundleRoot,
  [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
  [string]$OutputExe = ''
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath($RepoRoot)
$bundle = [IO.Path]::GetFullPath($BundleRoot)
if (-not (Test-Path -LiteralPath $bundle -PathType Container)) { throw "BundleRoot not found: $bundle" }

$basePayload = Join-Path $bundle 'payload.zip'
$readme = Join-Path $bundle 'README.txt'
if (-not (Test-Path -LiteralPath $basePayload -PathType Leaf)) { throw "Base payload missing: $basePayload" }
if (-not (Test-Path -LiteralPath $readme -PathType Leaf)) { throw "README resource missing: $readme" }

if ([String]::IsNullOrWhiteSpace($OutputExe)) {
  $OutputExe = Join-Path $bundle 'PCBridge-M1-Integration-Test.exe'
}
$output = [IO.Path]::GetFullPath($OutputExe)
$payload = Join-Path $bundle 'payload-m1.zip'

$packageResult = & (Join-Path $repo 'scripts\package-m1-companion.ps1') -InputZip $basePayload -OutputZip $payload -RepoRoot $repo
if (-not $packageResult) { throw 'M1 companion packaging produced no verification result.' }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw "Framework64 csc.exe not found: $csc" }

$sources = @(
  'src\current\PCBridgePortable.cs',
  'src\current\ScopedBridge.cs',
  'src\current\ScopeTests.cs',
  'src\current\DesktopIntegration.cs',
  'src\current\IntegrationTests.cs',
  'src\current\M1Integration.cs',
  'src\vnext\M1Automation.cs',
  'src\vnext\M1SessionBinding.cs',
  'src\vnext\M1BrowserProfile.cs',
  'src\vnext\M1BrowserBroker.cs',
  'src\vnext\M1AutomationRuntime.cs'
) | ForEach-Object {
  $p = Join-Path $repo $_
  if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Build source missing: $p" }
  $p
}

$compilerArgs = @(
  '/nologo',
  '/target:winexe',
  '/platform:x64',
  '/optimize+',
  "/out:$output",
  '/reference:System.Windows.Forms.dll',
  '/reference:System.Drawing.dll',
  '/reference:System.Web.Extensions.dll',
  '/reference:System.Security.dll',
  '/reference:System.IO.Compression.dll',
  "/resource:$payload,PCBridge.Payload",
  "/resource:$readme,PCBridge.Readme"
) + $sources

& $csc @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "M1 integrated build compilation failed: $LASTEXITCODE" }

[pscustomobject]@{
  ok = $true
  exe = $output
  exe_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $output).Hash
  payload = $payload
  payload_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $payload).Hash
} | ConvertTo-Json -Compress
