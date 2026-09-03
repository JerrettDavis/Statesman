[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]] $Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function Invoke-PackProjects {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Configuration,

        [Parameter(Mandatory = $true)]
        [string] $OutputPath
    )

    Get-ChildItem -Path (Join-Path $root 'src') -Filter *.csproj -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            Invoke-Native dotnet pack $_.FullName --configuration $Configuration --no-build --output $OutputPath
        }
}

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Invoke-Native python eng/validate.py --report artifacts/static-validation.txt
    Invoke-Native dotnet restore Statesman.slnx
    Invoke-Native dotnet build Statesman.slnx --configuration $Configuration --no-restore
    Invoke-Native dotnet test Statesman.slnx --configuration $Configuration --no-build
    Invoke-PackProjects -Configuration $Configuration -OutputPath artifacts/packages
}
finally {
    Pop-Location
}
