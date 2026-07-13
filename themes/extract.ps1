param([string]$Path = ".dotsy\sessions\20260628.3.jsonl")

$lines = Get-Content -Encoding utf8 $Path
$sessionId = $null
$displayCount = 0
$msgs = New-Object System.Collections.Generic.List[object]

foreach ($l in $lines) {
    if ([string]::IsNullOrWhiteSpace($l)) { continue }
    try { $o = $l | ConvertFrom-Json } catch { continue }
    if ($null -eq $sessionId -and $o.sessionId) { $sessionId = $o.sessionId }
    $type = $o.type

    if ($type -eq 'user') {
        $content = $o.message.content
        if ($content -is [string]) {
            $displayCount++
            $t = $content.TrimEnd()
            if (-not [string]::IsNullOrWhiteSpace($t)) {
                $msgs.Add([pscustomobject]@{ role = 'user'; blocks = @($t) })
            }
        } elseif ($content) {
            # array content: could hold text and/or tool_result blocks
            $displayCount++
            $texts = @()
            foreach ($b in $content) {
                if ($b.type -eq 'text' -and -not [string]::IsNullOrWhiteSpace($b.text)) { $texts += $b.text.TrimEnd() }
            }
            if ($texts.Count -gt 0) { $msgs.Add([pscustomobject]@{ role = 'user'; blocks = $texts }) }
        }
    }
    elseif ($type -eq 'assistant') {
        $displayCount++
        $texts = @()
        foreach ($b in $o.message.content) {
            if ($b.type -eq 'text' -and -not [string]::IsNullOrWhiteSpace($b.text)) { $texts += $b.text.TrimEnd() }
            elseif ($b.type -eq 'thinking' -and -not [string]::IsNullOrWhiteSpace($b.thinking)) { $texts += "THINK" + $b.thinking.TrimEnd() }
        }
        if ($texts.Count -gt 0) { $msgs.Add([pscustomobject]@{ role = 'agent'; blocks = $texts }) }
    }
    elseif ($type -eq 'tool_result') {
        $displayCount++
    }
}

$out = [pscustomobject]@{
    sessionId = $sessionId
    count     = $displayCount
    messages  = $msgs
}
$json = $out | ConvertTo-Json -Depth 8 -Compress
Set-Content -Path (Join-Path $PSScriptRoot 'convo-data.json') -Value $json -Encoding utf8
"sessionId=$sessionId count=$displayCount rendered=$($msgs.Count) bytes=$($json.Length)"

