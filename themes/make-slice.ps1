# Rebuild convo-data.json from a dotsy session, trimmed to the smallest slice that still
# exercises the maximum number of conversation colour roles (see README "Sample data").
#
#   ./make-slice.ps1 -Session C:\dev\timepom-desktop\.dotsy\sessions\20260709.1.jsonl
#
# Reuses the renderer that is embedded inside generate.ps1 (so there is a single source of
# truth for the markdown / syntax / replay logic) by extracting its <script> block and running
# it under Node with a minimal DOM stub, then appending slice.js as the driver.
param(
  [Parameter(Mandatory=$true)][string]$Session,
  [string]$Out = (Join-Path $PSScriptRoot 'convo-data.json')
)

$gen = Get-Content -Raw -Encoding utf8 (Join-Path $PSScriptRoot 'generate.ps1')
$js  = [regex]::Match($gen, '(?s)<script>\r?\n(.*)</script>').Groups[1].Value
$js  = $js.Replace('/*__DATA__*/', 'const DATA={sessionId:"",count:0,messages:[]};')

$stub = @'
function El(){ return { style:{}, dataset:{}, className:'', type:'', value:'', checked:false,
  appendChild(){}, addEventListener(){}, set innerHTML(v){}, set textContent(v){}, classList:{toggle(){}} }; }
global.document={ getElementById:()=>El(), createElement:()=>El(), createTextNode:()=>El(), querySelectorAll:()=>({forEach(){}}) };
'@

$slice = Get-Content -Raw -Encoding utf8 (Join-Path $PSScriptRoot 'slice.js')
$tmp = Join-Path $env:TEMP 'themes-runslice.js'
Set-Content $tmp ($stub + "`n" + $js + "`n" + $slice) -Encoding utf8
node $tmp $Session $Out
