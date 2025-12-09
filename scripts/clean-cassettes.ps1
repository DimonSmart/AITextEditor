$ErrorActionPreference = "Stop"

$cassetteDir = Join-Path $PSScriptRoot "..\tests\AiTextEditor.Domain.Tests\Fixtures\Cassettes"
$cassetteDir = [System.IO.Path]::GetFullPath($cassetteDir)

Write-Host "Cleaning cassettes in: $cassetteDir"

if (Test-Path $cassetteDir) {
    $files = Get-ChildItem -Path $cassetteDir -Filter "*.json"
    if ($files.Count -gt 0) {
        $files | Remove-Item -Force -Verbose
        Write-Host "Successfully deleted $($files.Count) cassette(s)."
    } else {
        Write-Host "No cassettes found to delete."
    }
} else {
    Write-Warning "Cassette directory does not exist."
}
