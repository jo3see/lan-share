[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'out'),
    [string]$PackagesDirectory = '',
    [ValidateSet('win-arm64','win-x64')][string]$RuntimeIdentifier = 'win-arm64',
    [switch]$SelfContained
)
$ErrorActionPreference='Stop'
$names=@('DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_GENERATE_ASPNET_CERTIFICATE')
$saved=@{}
foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    $env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot '.build'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
    if($PackagesDirectory){$env:NUGET_PACKAGES=[IO.Path]::GetFullPath($PackagesDirectory)}
    $contained=$SelfContained.IsPresent.ToString().ToLowerInvariant()
    & $DotnetPath publish (Join-Path $PSScriptRoot 'src\TetherNest.App\TetherNest.App.csproj') -c Release -r $RuntimeIdentifier --self-contained $contained -o $OutputDirectory --nologo
    if($LASTEXITCODE -ne 0){throw 'TetherNest build failed.'}
    & $DotnetPath build (Join-Path $PSScriptRoot 'tests\TetherNest.Tests\TetherNest.Tests.csproj') -c Release -r $RuntimeIdentifier --self-contained $contained --nologo
    if($LASTEXITCODE -ne 0){throw 'TetherNest test build failed.'}
} finally {
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}
}
