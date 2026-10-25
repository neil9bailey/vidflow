<#
.SYNOPSIS
  Sends one control-pipe request to a running VidFlow (see docs\SPEC.md "Automation").
.EXAMPLE
  .\tools\vidflowctl.ps1 -Pipe VidFlowTest dump
  .\tools\vidflowctl.ps1 -Pipe VidFlowTest action FrameStep FrameStep
  .\tools\vidflowctl.ps1 -Pipe VidFlowTest key Ctrl+Right
#>
param(
    [string]$Pipe = 'VidFlowTest',
    [int]$TimeoutMs = 5000,
    [Parameter(Mandatory = $true, Position = 0)][string]$Verb,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Args
)
$ErrorActionPreference = 'Stop'
$c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut)
try {
    $c.Connect($TimeoutMs)
    $enc = New-Object System.Text.UTF8Encoding($false)
    $w = New-Object System.IO.StreamWriter($c, $enc, 4096, $true)
    $w.Write("$Verb`n")
    foreach ($a in $Args) { $w.Write("$a`n") }
    $w.Write("<<END>>`n")
    $w.Flush()
    $r = New-Object System.IO.StreamReader($c, $enc, $false, 4096, $true)
    $lines = New-Object System.Collections.Generic.List[string]
    while ($null -ne ($line = $r.ReadLine()) -and $line -ne '<<END>>') { $lines.Add($line) }
    $lines -join "`n"
} finally {
    $c.Dispose()
}
