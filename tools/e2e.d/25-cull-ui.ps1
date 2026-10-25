# Cull UI: the Cull menu (with layer-aware shortcut labels), the new File / Markers / Help items and the
# control bar's cull chip state, checked through the real app (menu verb + dump + key verb).
# Dot-sourced by tools\e2e.ps1 (hidden desktop, helpers Test/Check/Act/Ctl/Dump/WaitFor/Key).
# Non-ASCII menu texts are built from code points so the file reads the same in any PowerShell.

$cuStar = [string][char]0x2605
$cuEllipsis = [string][char]0x2026

function CuNode($nodes, [string]$text) {
    foreach ($n in @($nodes)) { if ($n.text -eq $text) { return $n } }
    return $null
}
function CuItems($node) { return @(@($node.children) | ForEach-Object { "$($_.text)|$($_.shortcut)" }) }
function CuMenu { return (Ctl 'menu' @('context')) | ConvertFrom-Json }
function CuCull { return CuNode (CuMenu) 'Cull' }

Test 'cull-ui-menu' {
    Ctl 'open' @($clip1) | Out-Null
    WaitFor { param($d) $d.path -like '*clip01*' } 6000 'clip01' | Out-Null
    $m = CuMenu
    $top = @($m | ForEach-Object { $_.text })
    $iNav = [array]::IndexOf($top, 'Navigate'); $iCull = [array]::IndexOf($top, 'Cull'); $iMarkers = [array]::IndexOf($top, 'Markers')
    Check 'Cull submenu between Navigate and Markers' ($iCull -gt 0 -and $iCull -eq $iNav + 1 -and $iMarkers -eq $iCull + 1) "nav=$iNav cull=$iCull markers=$iMarkers"

    $cull = CuNode $m 'Cull'
    $expected = @('Cull Mode|C', 'Pick (Keep)|P (Cull mode)', 'Reject|X (Cull mode)', 'Clear Pick / Reject|U (Cull mode)', 'Rate|',
        'Next Undecided Clip|N (Cull mode)', 'Undo Decision|Ctrl+Z', 'Redo Decision|Ctrl+Y', 'Advance After Each Decision|',
        'Show|', 'Move Clip to Bin|', "Apply Decisions$cuEllipsis|Ctrl+Enter")
    $got = CuItems $cull
    Check 'Cull submenu items + layer shortcut labels' (($got -join ';') -eq ($expected -join ';')) ($got -join ' ; ')

    $rate = CuItems (CuNode $cull.children 'Rate')
    $rateExpected = @(1..5 | ForEach-Object { ($cuStar * $_) + "|$_ / Num$_ (Cull mode)" }) + @('Clear Rating|0 / Num0 (Cull mode)')
    Check 'Rate submenu' (($rate -join ';') -eq ($rateExpected -join ';')) ($rate -join ' ; ')

    $show = CuNode $cull.children 'Show'
    $showTexts = @(@($show.children) | ForEach-Object { $_.text })
    Check 'Show submenu (five filters)' (($showTexts -join ';') -eq 'All Clips;Picks Only;Rejects Only;Undecided Only;Rated Only') ($showTexts -join ' ; ')
    $checkedFilters = @(@($show.children) | Where-Object { $_.checked } | ForEach-Object { $_.text })
    Check 'exactly one filter checked' ($checkedFilters.Count -eq 1) ($checkedFilters -join ',')

    $bins = @(@((CuNode $cull.children 'Move Clip to Bin').children))
    $binOk = $bins.Count -eq 5
    for ($i = 0; $i -lt $bins.Count; $i++) { if (-not ($bins[$i].text -like "Bin $($i + 1)*") -or $bins[$i].shortcut -ne "F$($i + 5) (Cull mode)") { $binOk = $false } }
    Check 'Move Clip to Bin 1-5 on F5-F9' $binOk (($bins | ForEach-Object { "$($_.text)|$($_.shortcut)" }) -join ' ; ')

    $file = CuItems (CuNode $m 'File')
    $iShow = [array]::IndexOf($file, 'Show in Folder|Ctrl+E')
    Check 'File: Rename File after Show in Folder' ($iShow -ge 0 -and $file[$iShow + 1] -eq "Rename File$cuEllipsis|F2") ($file -join ' ; ')

    $markers = @(@((CuNode $m 'Markers').children) | ForEach-Object { $_.text })
    $iCopy = [array]::IndexOf($markers, 'Copy Markers to Clipboard')
    Check 'Markers: CSV and EDL export after Copy' ($iCopy -ge 0 -and $markers[$iCopy + 1] -eq "Export as CSV$cuEllipsis" -and $markers[$iCopy + 2] -eq "Export as EDL (DaVinci Resolve)$cuEllipsis") ($markers -join ' ; ')

    $iAbout = [array]::IndexOf($top, 'About VidFlow Player')
    Check 'Quick Start Guide before About' ($iAbout -gt 0 -and $top[$iAbout - 1] -eq 'Quick Start Guide') ($top[($iAbout - 3)..$iAbout] -join ' ; ')
    Check 'Check for Updates hidden without an update repository' (-not ($top -contains "Check for Updates$cuEllipsis")) ($top -join ' ; ')
}

Test 'cull-ui-state' {
    Ctl 'open' @($clip1) | Out-Null
    WaitFor { param($d) $d.path -like '*clip01*' } 6000 'clip01' | Out-Null
    $d = Dump
    if ($d.cull.mode) { Act 'ToggleCullMode' }
    if ($d.cull.autoAdvance) { Act 'CullAutoAdvance' }   # stay on this clip while deciding
    $d = Dump
    Check 'chip hidden while Cull mode is off' ($d.cull.bar.mode -eq $false) "$($d.cull.bar | ConvertTo-Json -Compress)"
    Check 'Cull Mode unchecked in the menu' (-not (CuNode (CuCull).children 'Cull Mode').checked)

    Act 'ToggleCullMode'
    $d = WaitFor { param($d) $d.cull.bar.mode -eq $true } 3000 'chip shown'
    Check 'Cull mode on: chip shown, undecided' ($d.cull.bar.mode -and $d.cull.bar.flag -eq 0) "$($d.cull.bar | ConvertTo-Json -Compress)"
    $c = CuCull
    Check 'Cull Mode checked in the menu' ((CuNode $c.children 'Cull Mode').checked)
    Check 'Advance After Each Decision unchecked' (-not (CuNode $c.children 'Advance After Each Decision').checked)

    $r = Key @('P')
    $d = WaitFor { param($d) $d.cull.bar.flag -eq 1 } 3000 'pick'
    Check 'P (Cull mode) picks: green chip' ($r -eq 'ok CullPick' -and $d.cull.bar.flag -eq 1) "key=[$r] bar=$($d.cull.bar | ConvertTo-Json -Compress)"
    $r = Key @('3')
    $d = WaitFor { param($d) $d.cull.bar.rating -eq 3 } 3000 'rating 3'
    Check '3 (Cull mode) rates: three stars on the chip' ($r -eq 'ok CullRate:3' -and $d.cull.bar.rating -eq 3) "key=[$r]"
    $rate = @((CuNode (CuCull).children 'Rate').children)
    $checked = @($rate | Where-Object { $_.checked } | ForEach-Object { $_.text })
    Check 'menu: three stars checked, nothing else' ($checked.Count -eq 1 -and $checked[0] -eq ($cuStar * 3)) ($checked -join ',')
    Check 'menu: Pick (Keep) checked' ((CuNode (CuCull).children 'Pick (Keep)').checked)
    $r = Key @('X')
    $d = WaitFor { param($d) $d.cull.bar.flag -eq -1 } 3000 'reject'
    Check 'X (Cull mode) rejects: red chip' ($r -eq 'ok CullReject' -and $d.cull.bar.flag -eq -1) "key=[$r]"
    Check 'chip shows progress text' ([string]$d.cull.bar.progress -ne '') "progress='$($d.cull.bar.progress)'"
    Key @('U') | Out-Null
    Key @('0') | Out-Null
    $d = WaitFor { param($d) $d.cull.bar.flag -eq 0 -and $d.cull.bar.rating -eq 0 } 3000 'cleared'
    Check 'U and 0 clear the chip' ($d.cull.bar.flag -eq 0 -and $d.cull.bar.rating -eq 0)
    $rate = @((CuNode (CuCull).children 'Rate').children)
    Check 'menu: Clear Rating never shows a check' (-not ($rate | Where-Object { $_.text -eq 'Clear Rating' }).checked -and -not ($rate | Where-Object { $_.checked }))

    Act 'ToggleCullMode'
    $d = WaitFor { param($d) $d.cull.bar.mode -eq $false } 3000 'chip hidden'
    Check 'Cull mode off: chip hidden' ($d.cull.bar.mode -eq $false)
    $r = Key @('X')
    Check 'X is the global key again outside Cull mode' ($r -eq 'ok SubDelayUp') "key=[$r]"
    Act 'SubDelayReset'
    if (-not (Dump).cull.autoAdvance) { Act 'CullAutoAdvance' }
}
