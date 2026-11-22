param(
    [Parameter(Mandatory)]
    [ValidatePattern('\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z')]
    [string]$Repository,
    [Parameter(Mandatory)]
    [ValidatePattern('\A\d+\.\d+\.\d+(?:\.\d+)?\z')]
    [string]$Version,
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',
    [string]$InnoCompiler = '',
    [ValidatePattern('\Ahttps://[^\s]+\z')]
    [string]$ReportEndpoint,
    [ValidatePattern('\A[A-Za-z0-9_]{5,32}\z')]
    [string]$ReportBotUsername,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'out')
)

$ErrorActionPreference = 'Stop'
if (!$InnoCompiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InnoCompiler = $command.Source }
    else {
        $InnoCompiler = @(
            (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 7/ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe')
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}

if (!$InnoCompiler -or !(Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'ISCC.exe was not found. Install Inno Setup or pass its path in -InnoCompiler.'
}

$project = Join-Path $PSScriptRoot '../RadialMenu1.csproj'
$publishRoot = New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot "../obj/release-$Version-win-$Architecture")
$output = New-Item -ItemType Directory -Force -Path $OutputDirectory
$publish = Join-Path $publishRoot.FullName ([Guid]::NewGuid().ToString('N'))

$publishArgs = @('publish', $project, '-c', 'Release', '-r', "win-$Architecture", '--self-contained',
    'true', "-p:Version=$Version", "-p:UpdateRepository=$Repository", '-p:DebugType=embedded', '-o', $publish)
if ($ReportEndpoint) { $publishArgs += "-p:ReportEndpoint=$ReportEndpoint" }
if ($ReportBotUsername) { $publishArgs += "-p:ReportBotUsername=$ReportBotUsername" }
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish exited with $LASTEXITCODE." }

$updateHostProject = Join-Path $PSScriptRoot 'UpdateHost/UpdateHost.csproj'
$hostOutput = "${publish}-update-host"
dotnet build $updateHostProject -c Release -o $hostOutput
if ($LASTEXITCODE -ne 0) { throw "Update host build exited with $LASTEXITCODE." }
Copy-Item -LiteralPath (Join-Path $hostOutput 'RadialMenu.UpdateHost.exe'),
    (Join-Path $hostOutput 'RadialMenu.UpdateHost.exe.config') -Destination $publish

$installerArgs = @("-dAppVersion=$Version", "-dBuildArch=$Architecture", "-dPublishDir=$publish",
    "-o$($output.FullName)", (Join-Path $PSScriptRoot 'RadialMenu.iss'))
& $InnoCompiler @installerArgs
if ($LASTEXITCODE -ne 0) { throw "ISCC exited with $LASTEXITCODE." }

Get-Item -LiteralPath (Join-Path $output.FullName "RadialMenu-Setup-win-$Architecture.exe")
