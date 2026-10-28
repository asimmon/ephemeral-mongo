#Requires -Version 7.0

<#
Lists the known vulnerabilities of the packages that packable projects depend on, directly or transitively,
and suggests the minimum version that fixes each of them. Advisories suppressed with NuGetAuditSuppress are reported as accepted.
#>

param(
  [string]$OutputPath = (Join-Path $PSScriptRoot ".output/vulnerability-report.md")
)

Begin {
  $ErrorActionPreference = "stop"
}

Process {
  function Exec([scriptblock]$Command) {
    $output = & $Command | Out-String
    if ($LASTEXITCODE -ne 0) {
      throw ("An error occurred while executing command: {0}`n{1}" -f $Command, $output)
    }
    return $output
  }

  function ConvertTo-NuGetVersion([string]$Version) {
    # Prerelease versions are never suggested
    $core = $Version.Split("+")[0]
    if ($core.Contains("-")) {
      return $null
    }

    $parts = @($core.Split("."))
    while ($parts.Count -lt 2) {
      $parts += "0"
    }

    $parsed = $null
    if ([version]::TryParse($parts -join ".", [ref]$parsed)) {
      return $parsed
    }

    return $null
  }

  # https://learn.microsoft.com/nuget/concepts/package-versioning#version-ranges
  function Test-VersionInRange([version]$Version, [string]$Range) {
    if (-not $Range.StartsWith("[") -and -not $Range.StartsWith("(")) {
      return $Version -ge (ConvertTo-NuGetVersion $Range)
    }

    $match = [regex]::Match($Range, '^([\[\(])\s*([^,\]\)]*?)\s*(?:(,)\s*([^\]\)]*?)\s*)?([\]\)])$')
    if (-not $match.Success) {
      return $false
    }

    $min = if ($match.Groups[2].Value) { ConvertTo-NuGetVersion $match.Groups[2].Value } else { $null }
    $max = if (-not $match.Groups[3].Success) { $min } elseif ($match.Groups[4].Value) { ConvertTo-NuGetVersion $match.Groups[4].Value } else { $null }

    $isMinInclusive = $match.Groups[1].Value -eq "["
    if ($min -and (($isMinInclusive -and $Version -lt $min) -or (-not $isMinInclusive -and $Version -le $min))) {
      return $false
    }

    $isMaxInclusive = $match.Groups[5].Value -eq "]"
    if ($max -and (($isMaxInclusive -and $Version -gt $max) -or (-not $isMaxInclusive -and $Version -ge $max))) {
      return $false
    }

    return $true
  }

  $versionsCache = @{}

  # nuget.org is the only package source in nuget.config
  function Get-NewerStableVersions([string]$PackageId, [string]$CurrentVersion, [string]$Range) {
    $key = $PackageId.ToLowerInvariant()
    if (-not $versionsCache.ContainsKey($key)) {
      $versionsCache[$key] = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$key/index.json").versions
    }

    $current = ConvertTo-NuGetVersion $CurrentVersion

    return $versionsCache[$key] |
      ForEach-Object { [pscustomobject]@{ Text = $_; Version = (ConvertTo-NuGetVersion $_) } } |
      Where-Object { $_.Version -and $_.Version -gt $current -and (-not $Range -or (Test-VersionInRange $_.Version $Range)) } |
      Sort-Object Version
  }

  function Get-VulnerablePackages([string]$ProjectPath) {
    $json = Exec { & dotnet list $ProjectPath package --vulnerable --include-transitive --format json }
    return ($json | ConvertFrom-Json).projects[0].frameworks
  }

  $advisoriesCache = @{}

  # Restores a throwaway project that only references the given package version, so that NuGet resolves its dependencies exactly like for a consumer
  function Test-VersionFixesAdvisory([string]$PackageId, [string]$Version, [string]$TargetFrameworks, [string]$AdvisoryUrl) {
    $key = "$PackageId|$Version|$TargetFrameworks"
    if (-not $advisoriesCache.ContainsKey($key)) {
      $projectDir = Join-Path $scanDir ("{0}.{1}.{2}" -f $PackageId, $Version, $advisoriesCache.Count)
      New-Item -ItemType Directory -Force $projectDir | Out-Null

      # These files stop MSBuild from importing the repository's props, which use central package management and coding standards
      Set-Content (Join-Path $projectDir "Directory.Build.props") "<Project />"
      Set-Content (Join-Path $projectDir "Directory.Packages.props") "<Project />"

      $projectPath = Join-Path $projectDir "Scan.csproj"
      Set-Content $projectPath @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>$TargetFrameworks</TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$PackageId" Version="[$Version]" />
  </ItemGroup>
</Project>
"@

      # A version that can't be restored, for instance because it doesn't support one of the target frameworks, is never suggested.
      # Vulnerabilities are listed afterwards, so the restore doesn't need to audit them too.
      $result = [pscustomobject]@{ IsRestored = $false; Advisories = @() }
      & dotnet restore $projectPath -p:NuGetAudit=false 2>&1 | Out-Null
      if ($LASTEXITCODE -eq 0) {
        $result.IsRestored = $true
        $result.Advisories = @(Get-VulnerablePackages $projectPath |
          Where-Object { $_ } |
          ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) } |
          Where-Object { $_ } |
          ForEach-Object { $_.vulnerabilities.advisoryurl })
      }

      $advisoriesCache[$key] = $result
    }

    $result = $advisoriesCache[$key]
    return $result.IsRestored -and $result.Advisories -notcontains $AdvisoryUrl
  }

  function Find-MinimumFixedVersion([string]$PackageId, [string]$CurrentVersion, [string]$Range, [string]$AdvisoryUrl, [string]$TargetFrameworks) {
    $candidates = @(Get-NewerStableVersions $PackageId $CurrentVersion $Range)

    # Each candidate requires a restore, so versions are assumed to stay fixed once a fix is released, which allows a binary search
    $fixedVersion = $null
    $low = 0
    $high = $candidates.Count - 1

    while ($low -le $high) {
      $middle = [math]::Floor(($low + $high) / 2)
      if (Test-VersionFixesAdvisory $PackageId $candidates[$middle].Text $TargetFrameworks $AdvisoryUrl) {
        $fixedVersion = $candidates[$middle].Text
        $high = $middle - 1
      }
      else {
        $low = $middle + 1
      }
    }

    return $fixedVersion
  }

  function Format-FixedVersion([string]$PackageId, [string]$CurrentVersion, [string]$FixedVersion) {
    $isNewMajor = (ConvertTo-NuGetVersion $FixedVersion).Major -ne (ConvertTo-NuGetVersion $CurrentVersion).Major
    return "``$PackageId`` $FixedVersion" + ($isNewMajor ? " (new major version)" : "")
  }

  # Direct dependencies of the project whose dependency graph contains the given transitive package
  function Get-TopLevelAncestors($Assets, [string]$TargetFramework, [string]$PackageId) {
    $packages = $Assets.targets[$TargetFramework]
    $resolvedKeys = @{}
    foreach ($key in $packages.Keys) {
      $resolvedKeys[$key.Split("/")[0]] = $key
    }

    $ancestors = @()
    $dependencies = $Assets.project.frameworks[$TargetFramework].dependencies

    foreach ($topLevelId in $dependencies.Keys) {
      if ($dependencies[$topLevelId].target -ne "Package" -or -not $resolvedKeys.ContainsKey($topLevelId)) {
        continue
      }

      $visited = @{}
      $stack = [System.Collections.Generic.Stack[string]]::new()
      $stack.Push($resolvedKeys[$topLevelId])

      while ($stack.Count -gt 0) {
        $key = $stack.Pop()
        if ($visited.ContainsKey($key)) {
          continue
        }

        $visited[$key] = $true

        if ($key.Split("/")[0] -eq $PackageId) {
          $ancestors += [pscustomobject]@{
            Id = $topLevelId
            Version = $resolvedKeys[$topLevelId].Split("/")[1]
            Range = $dependencies[$topLevelId].version
          }
          break
        }

        if ($packages[$key].dependencies) {
          foreach ($dependencyId in $packages[$key].dependencies.Keys) {
            if ($resolvedKeys.ContainsKey($dependencyId)) {
              $stack.Push($resolvedKeys[$dependencyId])
            }
          }
        }
      }
    }

    return $ancestors
  }

  $env:DOTNET_NOLOGO = "true"
  $scanDir = Join-Path $PSScriptRoot ".output/vulnerability-scan"
  Remove-Item $scanDir -Force -Recurse -ErrorAction SilentlyContinue

  $findings = @()
  $accepted = @()

  foreach ($projectFile in Get-ChildItem (Join-Path $PSScriptRoot "src") -Filter "*.csproj" -Recurse) {
    $evaluation = Exec { & dotnet msbuild $projectFile.FullName -getProperty:IsPackable -getProperty:TargetFrameworks -getProperty:ProjectAssetsFile -getItem:NuGetAuditSuppress } | ConvertFrom-Json
    if ($evaluation.Properties.IsPackable -ne "true") {
      continue
    }

    $projectName = $projectFile.BaseName
    $targetFrameworks = $evaluation.Properties.TargetFrameworks
    $suppressedAdvisories = @($evaluation.Items.NuGetAuditSuppress | ForEach-Object { $_.Identity })

    Write-Host "Scanning $projectName"
    Exec { & dotnet restore $projectFile.FullName } | Out-Null
    $assets = Get-Content $evaluation.Properties.ProjectAssetsFile -Raw | ConvertFrom-Json -AsHashtable

    # The same vulnerable package is usually reported for every target framework, it's only reported once
    $projectFindings = [ordered]@{}

    foreach ($framework in Get-VulnerablePackages $projectFile.FullName) {
      $packages = @($framework.topLevelPackages | Where-Object { $_ } | ForEach-Object { $_ | Add-Member -PassThru IsTransitive $false }) +
        @($framework.transitivePackages | Where-Object { $_ } | ForEach-Object { $_ | Add-Member -PassThru IsTransitive $true })

      foreach ($package in $packages) {
        foreach ($vulnerability in $package.vulnerabilities) {
          $key = "$($package.id)|$($package.resolvedVersion)|$($vulnerability.advisoryurl)"
          if (-not $projectFindings.Contains($key)) {
            $projectFindings[$key] = [pscustomobject]@{
              Project = $projectName
              PackageId = $package.id
              Version = $package.resolvedVersion
              IsTransitive = $package.IsTransitive
              RequestedRange = $package.requestedVersion
              Severity = $vulnerability.severity
              AdvisoryUrl = $vulnerability.advisoryurl
              TargetFrameworks = [System.Collections.Generic.List[string]]::new()
            }
          }

          $projectFindings[$key].TargetFrameworks.Add($framework.framework)
        }
      }
    }

    foreach ($finding in $projectFindings.Values) {
      if ($suppressedAdvisories -contains $finding.AdvisoryUrl) {
        $accepted += $finding
        continue
      }

      Write-Host "Looking for fixes of $($finding.PackageId) $($finding.Version) ($($finding.AdvisoryUrl))"
      $suggestions = @()

      if ($finding.IsTransitive) {
        foreach ($ancestor in Get-TopLevelAncestors $assets $finding.TargetFrameworks[0] $finding.PackageId) {
          $fixedVersion = Find-MinimumFixedVersion $ancestor.Id $ancestor.Version $ancestor.Range $finding.AdvisoryUrl $targetFrameworks
          if ($fixedVersion) {
            $suggestions += "Raise the minimum version of ``$($ancestor.Id)`` $($ancestor.Version) to $(Format-FixedVersion $ancestor.Id $ancestor.Version $fixedVersion)"
          }
          else {
            $suggestions += "No version of ``$($ancestor.Id)`` allowed by ``$($ancestor.Range)`` fixes it"
          }
        }

        $fixedVersion = Find-MinimumFixedVersion $finding.PackageId $finding.Version $null $finding.AdvisoryUrl $targetFrameworks
        if ($fixedVersion) {
          $suggestions += "Or reference $(Format-FixedVersion $finding.PackageId $finding.Version $fixedVersion) directly"
        }
        else {
          $suggestions += "No newer version of ``$($finding.PackageId)`` fixes it"
        }
      }
      else {
        $fixedVersion = Find-MinimumFixedVersion $finding.PackageId $finding.Version $finding.RequestedRange $finding.AdvisoryUrl $targetFrameworks
        if ($fixedVersion) {
          $suggestions += "Raise the minimum version to $(Format-FixedVersion $finding.PackageId $finding.Version $fixedVersion)"
        }
        else {
          $suggestions += "No version allowed by ``$($finding.RequestedRange)`` fixes it"
        }
      }

      $findings += $finding | Add-Member -PassThru Suggestions $suggestions
    }
  }

  function Format-Finding($Finding) {
    $advisoryId = $Finding.AdvisoryUrl.Split("/")[-1]
    $kind = $Finding.IsTransitive ? "transitive" : "direct"
    return "**$($Finding.PackageId) $($Finding.Version)** ($kind, $($Finding.TargetFrameworks -join ", ")): $($Finding.Severity), [$advisoryId]($($Finding.AdvisoryUrl))"
  }

  $severityOrder = @{ "Critical" = 0; "High" = 1; "Moderate" = 2; "Low" = 3 }
  $report = [System.Text.StringBuilder]::new()

  if ($findings.Count -gt 0) {
    [void]$report.AppendLine("The scheduled vulnerability scan found known vulnerabilities in the dependencies of the published packages.")

    foreach ($group in $findings | Group-Object Project) {
      [void]$report.AppendLine().AppendLine("### $($group.Name)").AppendLine()
      foreach ($finding in $group.Group | Sort-Object { $severityOrder[$_.Severity] }, PackageId) {
        [void]$report.AppendLine("- $(Format-Finding $finding)")
        foreach ($suggestion in $finding.Suggestions) {
          [void]$report.AppendLine("  - $suggestion")
        }
      }
    }
  }
  else {
    [void]$report.AppendLine("The scheduled vulnerability scan didn't find any known vulnerability in the dependencies of the published packages.")
  }

  if ($accepted.Count -gt 0) {
    [void]$report.AppendLine().AppendLine("### Accepted").AppendLine().AppendLine("These advisories are suppressed with ``NuGetAuditSuppress``, see the project files for the reasons.").AppendLine()
    foreach ($finding in $accepted | Sort-Object Project, PackageId) {
      [void]$report.AppendLine("- $($finding.Project): $(Format-Finding $finding)")
    }
  }

  New-Item -ItemType Directory -Force (Split-Path $OutputPath) | Out-Null
  Set-Content $OutputPath $report.ToString()
  Write-Host $report.ToString()

  if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content $env:GITHUB_STEP_SUMMARY $report.ToString()
  }

  if ($env:GITHUB_OUTPUT) {
    Add-Content $env:GITHUB_OUTPUT "has-findings=$(($findings.Count -gt 0).ToString().ToLowerInvariant())"
  }
}
