param(
    [Parameter(Mandatory = $false)]
    [string] $ResultsDirectory = "TestResults"
)

$ErrorActionPreference = "Stop"

$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add("## Backend test results")
$summary.Add("")

$trxFiles = @(Get-ChildItem -Path $ResultsDirectory -Recurse -Filter "*.trx" -ErrorAction SilentlyContinue)
if ($trxFiles.Count -eq 0) {
    $summary.Add("No TRX results were produced.")
} else {
    $totals = @{ Total = 0; Executed = 0; Passed = 0; Failed = 0 }
    foreach ($trxFile in $trxFiles) {
        [xml] $trx = Get-Content -LiteralPath $trxFile.FullName -Raw
        $counters = $trx.TestRun.ResultSummary.Counters
        $totals.Total += [int] $counters.total
        $totals.Executed += [int] $counters.executed
        $totals.Passed += [int] $counters.passed
        $totals.Failed += [int] $counters.failed
    }

    $summary.Add("| Total | Executed | Passed | Failed |")
    $summary.Add("| ---: | ---: | ---: | ---: |")
    $summary.Add("| $($totals.Total) | $($totals.Executed) | $($totals.Passed) | $($totals.Failed) |")
}

$summary.Add("")
$summary.Add("## Line coverage")
$summary.Add("")

$coverageFiles = @(Get-ChildItem -Path $ResultsDirectory -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue)
if ($coverageFiles.Count -eq 0) {
    $summary.Add("No Cobertura coverage results were produced.")
} else {
    $lines = @{}
    foreach ($coverageFile in $coverageFiles) {
        [xml] $coverage = Get-Content -LiteralPath $coverageFile.FullName -Raw
        foreach ($class in @($coverage.coverage.packages.package.classes.class)) {
            foreach ($line in @($class.lines.line)) {
                $key = "$($class.filename):$($line.number)"
                $hits = [int] $line.hits
                if (-not $lines.ContainsKey($key) -or $hits -gt $lines[$key]) {
                    $lines[$key] = $hits
                }
            }
        }
    }

    $lineCount = $lines.Count
    $coveredCount = @($lines.Values | Where-Object { $_ -gt 0 }).Count
    $coveragePercent = if ($lineCount -eq 0) { 0 } else { 100 * $coveredCount / $lineCount }
    $summary.Add("Covered **$coveredCount** of **$lineCount** executable lines (**$($coveragePercent.ToString('0.00'))%**).")
}

$content = $summary -join [Environment]::NewLine
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $content
} else {
    Write-Output $content
}
