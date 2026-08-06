$ErrorActionPreference = "Stop"

$jsonOutput = & dotnet list src/Lexarbor.sln package `
    --vulnerable `
    --include-transitive `
    --format json
if ($LASTEXITCODE -ne 0) {
    throw "The .NET vulnerability audit could not be completed."
}

$report = ($jsonOutput -join [Environment]::NewLine) | ConvertFrom-Json
$findings = [System.Collections.Generic.List[object]]::new()

foreach ($project in @($report.projects)) {
    foreach ($framework in @($project.frameworks)) {
        $packages = @($framework.topLevelPackages) + @($framework.transitivePackages)
        foreach ($package in $packages) {
            if ($null -eq $package -or $null -eq $package.vulnerabilities) {
                continue
            }
            foreach ($vulnerability in @($package.vulnerabilities)) {
                $findings.Add([pscustomobject]@{
                    Project = $project.path
                    Package = $package.id
                    Version = $package.resolvedVersion
                    Severity = $vulnerability.severity
                    Advisory = $vulnerability.advisoryurl
                })
            }
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Output "No vulnerable direct or transitive .NET packages were found."
    exit 0
}

$findings | Format-Table -AutoSize | Out-String | Write-Error
throw "Found $($findings.Count) vulnerable .NET package reference(s)."
