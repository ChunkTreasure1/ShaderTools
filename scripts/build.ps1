[CmdletBinding()]
param(
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'Shader Tools for Visual Studio.sln'
$acquireServerPath = Join-Path $PSScriptRoot 'acquire-server.ps1'
$testProjectPath = Join-Path $repositoryRoot 'ShaderTools.Tests\ShaderTools.Tests.csproj'
$vsixPath = Join-Path $repositoryRoot 'ShaderTools.VisualStudio\bin\Release\ShaderTools.VisualStudio.vsix'
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Write-Host $Description
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution not found at '$solutionPath'."
}
if (-not (Test-Path -LiteralPath $acquireServerPath -PathType Leaf)) {
    throw "Server acquisition script not found at '$acquireServerPath'."
}
if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
    throw "vswhere was not found at '$vswherePath'. Install Visual Studio 2022 17.14 or newer with MSBuild."
}

$installations = @(& $vswherePath -latest -products '*' -version '[17.14,19.0)' -requires Microsoft.Component.MSBuild -property installationPath)
$discoveryExitCode = $LASTEXITCODE
$installationPath = [string]($installations | Select-Object -First 1)
$installationPath = $installationPath.Trim()
if ($discoveryExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($installationPath)) {
    throw 'Visual Studio 17.14 or newer with MSBuild was not found.'
}

$msbuildPath = Join-Path $installationPath 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuildPath -PathType Leaf)) {
    throw "MSBuild was not found at '$msbuildPath'."
}

# Developer PowerShell can belong to a different VS installation than vswhere's
# latest result. Its inherited VisualStudioVersion would then point NuGet imports
# at a missing version directory, silently dropping all PackageReference assemblies.
$visualStudioVersion = '{0}.0' -f [System.Diagnostics.FileVersionInfo]::GetVersionInfo($msbuildPath).FileMajorPart

$powerShellPath = (Get-Process -Id $PID).Path
Invoke-Checked -Executable $powerShellPath -Arguments @(
    '-NoLogo',
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', $acquireServerPath
) -Description 'Acquiring and verifying the pinned shader server bundle...'

Invoke-Checked -Executable $msbuildPath -Arguments @(
    $solutionPath,
    '/restore',
    # Build the solution in dependency order. Parallel top-level Bridge and VSIX builds can race
    # over the generated net472 app.config that the VSIX project consumes as project output.
    '/m:1',
    '/p:Configuration=Release',
    '/p:Platform=Any CPU',
    "/p:VisualStudioVersion=$visualStudioVersion",
    '/p:DeployExtension=false',
    # VSSDK's project-output collector otherwise records an intermediate net472 app.config
    # which the SDK IncrementalClean target removes before CreateVsixContainer consumes it.
    # The generated file only selects the CLR v4 runtime; the packaged bridge targets net472.
    '/p:AutoGenerateBindingRedirects=false',
    '/p:GenerateBindingRedirectsOutputType=false'
) -Description "Building the Release VSIX with $msbuildPath..."

if (-not $SkipTests) {
    $dotnet = @(Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
    if ($null -eq $dotnet) {
        throw 'dotnet was not found; it is required for ShaderTools.Tests. Use -SkipTests only when tests were already run separately.'
    }
    Invoke-Checked -Executable $dotnet.Source -Arguments @(
        'test',
        $testProjectPath,
        '--configuration', 'Release',
        '--nologo'
    ) -Description 'Running ShaderTools unit tests...'
}
else {
    Write-Host 'SKIP ShaderTools unit tests (-SkipTests was specified).'
}

if (-not (Test-Path -LiteralPath $vsixPath -PathType Leaf)) {
    throw "Build completed without producing the expected VSIX at '$vsixPath'."
}

$resolvedVsixPath = (Resolve-Path -LiteralPath $vsixPath).Path
$size = (Get-Item -LiteralPath $resolvedVsixPath).Length
Write-Host "VSIX: $resolvedVsixPath ($size bytes)"
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test-package.ps1') -VsixPath $resolvedVsixPath
}
