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
    Exec { & dotnet run   -c Release --no-build --no-restore --project "EphemeralMongo.Tests" }
    Exec { & dotnet run   -c Release --no-build --no-restore --project "EphemeralMongo.v2.Tests" --framework net9.0 }
    if ($IsWindows) {
      Exec { & dotnet run -c Release --no-build --no-restore --project "EphemeralMongo.v2.Tests" --framework net472 }
    }
    Exec { & dotnet pack  -c Release --no-build --output "$outputDir" }

    if (($null -ne $env:NUGET_SOURCE ) -and ($null -ne $env:NUGET_API_KEY)) {
      Exec { & dotnet nuget push "$nupkgsPath" -s $env:NUGET_SOURCE -k $env:NUGET_API_KEY --skip-duplicate }
    }
  }
  finally {
    Pop-Location
  }
}
