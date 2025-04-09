#Requires -Version 5.0

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

  try {
    Push-Location $workingDir
    Remove-Item $outputDir -Force -Recurse -ErrorAction SilentlyContinue

    Exec { & dotnet clean -c Release }

    Exec { & dotnet build -c Release }

    # https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-integration-dotnet-test
    # https://github.com/microsoft/testfx/issues/4396
    Exec { & dotnet test  -c Release --no-build --no-restore -p:TestingPlatformShowTestsFailure=true -p:TestingPlatformCaptureOutput=false -tl:false }

    Exec { & dotnet pack  -c Release --no-build --output "$outputDir" }

    if (($null -ne $env:NUGET_SOURCE ) -and ($null -ne $env:NUGET_API_KEY)) {
      Exec { & dotnet nuget push "$nupkgsPath" -s $env:NUGET_SOURCE -k $env:NUGET_API_KEY --skip-duplicate }
    }
  }
  finally {
    Pop-Location
  }
}
