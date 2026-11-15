<#
.SYNOPSIS
    Generates the ClassicAssist macro command reference with ExportCommands.exe and converts it into a single PDF.

.DESCRIPTION
    ExportCommands.exe writes its Markdown into a "Docs" folder next to the assembly it documents, so the
    ClassicAssist binaries are copied to a temporary folder first (the original output folder is not modified).
    The index (Macro-Commands.md) links to one file per category; --follow-links merges them into one PDF
    and turns the links into internal PDF links.

.EXAMPLE
    ./scripts/Build-ClassicAssistPdf.ps1 -Locale ko-KR -PageSize Letter
#>
param(
    [string]$ClassicAssistDir = "$env:USERPROFILE\Documents\UO\ClassicAssist\Output\net48",
    [string]$Output = "$PSScriptRoot\..\artifacts\ClassicAssist-Macro-Commands.pdf",
    [ValidateSet("en-US", "it-IT", "pl-PL", "ko-KR", "cs-CZ")]
    [string]$Locale = "en-US",
    [string]$PageSize = "A4",
    [string[]]$ExtraArgs = @()
)
$ErrorActionPreference = "Stop"

$work = Join-Path ([IO.Path]::GetTempPath()) ("ca-docs-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    Copy-Item (Join-Path $ClassicAssistDir "*") -Destination $work -Include *.dll, *.exe, *.config
    foreach ($culture in "it-IT", "pl-PL", "ko-KR", "cs-CZ", "it", "pl", "ko", "cs") {
        $dir = Join-Path $ClassicAssistDir $culture
        if (Test-Path $dir) { Copy-Item $dir -Destination $work -Recurse }
    }
    New-Item -ItemType Directory -Path (Join-Path $work "Docs") | Out-Null

    Push-Location $work
    try { & (Join-Path $work "ExportCommands.exe") (Join-Path $work "ClassicAssist.dll") | Out-Null }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "ExportCommands.exe failed with exit code $LASTEXITCODE" }

    $index = if ($Locale -eq "en-US") { "Macro-Commands.md" } else { "Macro-Commands ($Locale).md" }
    $project = Join-Path $PSScriptRoot "..\src\MarkdownToPdf.Tool"
    dotnet run --project $project -c Release -- (Join-Path $work "Docs\$index") --follow-links -p $PageSize -o $Output @ExtraArgs
    if ($LASTEXITCODE -ne 0) { throw "markdowntopdf failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
