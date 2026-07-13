# Rank every dotsy session found under -Root by how many distinct conversation colour roles
# its replay emits (most colours first). Use it to pick a rich session for make-slice.ps1.
#
#   ./rank-sessions.ps1 -Root C:\dev
#
# Same trick as make-slice.ps1: run generate.ps1's embedded renderer under Node with a DOM
# stub, then append driver.js as the batch analyzer.
param([string]$Root = 'C:\dev')

$dirs = Get-ChildItem $Root -Directory -Recurse -Depth 6 -Filter sessions -ErrorAction SilentlyContinue |
  Where-Object { $_.Parent.Name -eq '.dotsy' } | Select-Object -Expand FullName -Unique
$dirsFile = Join-Path $env:TEMP 'themes-dirs.txt'
$dirs | Set-Content $dirsFile -Encoding utf8
"session dirs found: $($dirs.Count)"

$gen = Get-Content -Raw -Encoding utf8 (Join-Path $PSScriptRoot 'generate.ps1')
$js  = [regex]::Match($gen, '(?s)<script>\r?\n(.*)</script>').Groups[1].Value
$js  = $js.Replace('/*__DATA__*/', 'const DATA={sessionId:"",count:0,messages:[]};')

$stub = @'
function El(){ return { style:{}, dataset:{}, className:'', type:'', value:'', checked:false,
  appendChild(){}, addEventListener(){}, set innerHTML(v){}, set textContent(v){}, classList:{toggle(){}} }; }
global.document={ getElementById:()=>El(), createElement:()=>El(), createTextNode:()=>El(), querySelectorAll:()=>({forEach(){}}) };
'@

$driver = Get-Content -Raw -Encoding utf8 (Join-Path $PSScriptRoot 'driver.js')
$tmp = Join-Path $env:TEMP 'themes-rank.js'
Set-Content $tmp ($stub + "`n" + $js + "`n" + $driver) -Encoding utf8
node $tmp $dirsFile
