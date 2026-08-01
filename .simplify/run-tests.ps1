# Test runner for the simplify-isomorphically harness.
#
# The harness ships no C#/dotnet parser, so out of the box a .NET repo verifies
# at exit-code fidelity only -- a deleted or renamed test would pass unnoticed.
# This wrapper distils the TRX into a sorted "name=outcome" manifest, which
# config.json registers as a golden. Hashing that manifest is what upgrades the
# claim to per-test-id fidelity: any test that disappears, is renamed, or flips
# outcome changes the hash and fails verify.
#
# Exits with dotnet test's own exit code so the harness still sees red/green.

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$trxDir = Join-Path $repo '.simplify/trx'
$trx = Join-Path $trxDir 'run.trx'
$manifest = Join-Path $repo '.simplify/testids.txt'

if (Test-Path $trx) { Remove-Item $trx -Force }

dotnet test (Join-Path $repo 'src/Gloam.sln') -c Release `
    --logger "trx;LogFileName=run.trx" --results-directory $trxDir
$testExit = $LASTEXITCODE

if (-not (Test-Path $trx)) {
    # No TRX means no manifest. Write a sentinel rather than leaving a stale
    # file in place, which would otherwise hash clean and fake a passing verify.
    "NO-TRX-PRODUCED exit=$testExit" | Set-Content -Path $manifest -Encoding utf8
    exit ($testExit -eq 0 ? 1 : $testExit)
}

[xml]$doc = Get-Content $trx -Raw
$ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
$ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

# testName carries the fully-qualified name including the inline-data arguments
# for parameterised cases, so theory rows are individually tracked.
$rows = $doc.SelectNodes('//t:UnitTestResult', $ns) | ForEach-Object {
    '{0}={1}' -f $_.testName, $_.outcome
}
$rows | Sort-Object -CaseSensitive | Set-Content -Path $manifest -Encoding utf8

Write-Host "[run-tests] manifest: $($rows.Count) test ids -> $manifest"
exit $testExit
