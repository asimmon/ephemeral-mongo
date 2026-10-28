#Requires -Version 5.0

param(
  # The release workflow only runs once CI has already tested the same commit on every OS
  [switch]$SkipTests
)

Begin {
  $ErrorActionPreference = "stop"
}

Process {
  function Exec([scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
      throw ("An error occurred while executing command: {0}" -f $Command)
    }
  }

  $workingDir = Join-Path $PSScriptRoot "src"
  $outputDir = Join-Path $PSScriptRoot ".output"
  $nupkgsPath = Join-Path $outputDir "*.nupkg"
  $testResultsDir = Join-Path $outputDir "TestResults"

  try {
    Push-Location $workingDir
    Remove-Item $outputDir -Force -Recurse -ErrorAction SilentlyContinue

    Exec { & dotnet clean -c Release }

    Exec { & dotnet build -c Release }

    if (-not $SkipTests) {
      # https://aka.ms/dotnet-test
      # TRX reports and hang dumps end up in the test results directory, which CI uploads when tests fail
      $testArgs = @("--results-directory", $testResultsDir, "--report-xunit-trx", "--hangdump", "--hangdump-timeout", "15m")

      if ($IsWindows) {
        Exec { & dotnet test -c Release --no-build --no-progress --output Detailed @testArgs --filter-query "/[Category!=SlowOnWindows]" }
      } else {
        Exec { & dotnet test -c Release --no-build --no-progress --output Detailed @testArgs }
      }
    }

    Exec { & dotnet pack -c Release --no-build --output "$outputDir" }

    if (($null -ne $env:NUGET_SOURCE ) -and ($null -ne $env:NUGET_API_KEY)) {
      Exec { & dotnet nuget push "$nupkgsPath" -s $env:NUGET_SOURCE -k $env:NUGET_API_KEY --skip-duplicate }
    }
  }
  finally {
    Pop-Location
  }
}
