$dataPath = Join-Path $PSScriptRoot 'convo-data.json'
$json = Get-Content -Raw -Encoding utf8 $dataPath
$json = $json.Replace('</','<\/')   # keep transcript text from closing the <script> tag

$html = @'
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>dotsy &mdash; Conversation Panel Colors + Latest Session</title>
<style>
  body { font-family:"Segoe UI",system-ui,sans-serif; background:#1e1e1e; color:#e0e0e0; margin:0; padding:28px; }
  h1 { font-size:22px; margin:0 0 4px; }
  h2 { font-size:17px; margin:28px 0 10px; }
  p.sub { color:#999; margin:0 0 20px; font-size:13px; }
  code { font-family:Consolas,monospace; color:#ce9178; }
  table.map { border-collapse:collapse; width:auto; max-width:none; margin-bottom:8px; }
  table.map th, table.map td { border:1px solid #3a3a3a; font-size:13px; vertical-align:middle; }
  table.map th { background:#2b2b2b; color:#ddd; padding:8px 12px; font-weight:600; }
  table.map td.use { color:#ddd; padding:6px 12px; }
  table.map td.role { font-family:Consolas,monospace; color:#9cdcfe; padding:6px 12px; }
  table.map td.hex { font-family:Consolas,monospace; color:#bbb; padding:6px 10px; text-align:center; }
  input.hexinput { width:84px; font-family:Consolas,monospace; font-size:12px; padding:3px 5px;
                   background:#161616; color:#ddd; border:1px solid #555; border-radius:3px; }
  .tablewrap { overflow-x:auto; margin-bottom:8px; }
  .btn { font-family:inherit; font-size:13px; padding:6px 12px; background:#2b2b2b; color:#e0e0e0;
         border:1px solid #555; border-radius:4px; cursor:pointer; }
  .btn:hover { background:#3a3a3a; }
  html { scroll-behavior:smooth; }
  h2 { scroll-margin-top:12px; }
  nav.toc { background:#232323; border:1px solid #3a3a3a; border-radius:6px; padding:10px 16px; margin:0 0 18px; display:inline-block; }
  nav.toc b { color:#ddd; }
  nav.toc ol { margin:6px 0 0; padding-left:20px; }
  nav.toc li { margin:2px 0; }
  nav.toc a { color:#9cdcfe; text-decoration:none; }
  nav.toc a:hover { text-decoration:underline; }
  .swatch { font-family:Consolas,monospace; padding:8px 12px; white-space:nowrap; }
  .cols { display:flex; gap:14px; align-items:flex-start; }
  .themecol { flex:1 1 0; min-width:0; }
  .themecol.collapsed { flex:0 0 auto; }   /* hidden theme shrinks to its header width */
  .themecol h3 { margin:0 0 6px; font-size:14px; font-weight:600; white-space:nowrap; }
  .themecol h3 label { cursor:pointer; display:inline-flex; align-items:center; gap:6px; }
  pre.convo { margin:0; padding:14px 16px; border-radius:6px; font-family:Consolas,"Cascadia Mono",monospace;
              font-size:12px; line-height:1.45; white-space:pre-wrap; word-break:break-word; overflow-wrap:anywhere;
              max-height:82vh; overflow:auto; border:1px solid #3a3a3a; }
  .hidden-note { padding:14px 16px; border-radius:6px; border:1px dashed #3a3a3a; color:#888;
                 font-family:Consolas,monospace; font-size:12px; font-style:italic; }
</style>
</head>
<body>
  <h1>dotsy &mdash; TUI Panel Colors</h1>
  <p class="sub">Each panel has its own palette table (with a live-editable <b>Custom</b> theme) and a rendered
  sample across <b>Dark / Light / Borland / Custom</b>. Toggle a theme's checkbox to hide &amp; collapse it.
  The Custom theme starts as a copy of Dark &mdash; edit any <code>fg</code>/<code>bg</code> box to recolor its
  column (in every panel) live. Conversation sample = session <code id="sid"></code> replayed through dotsy's real renderers.</p>
  <p><button id="setAllBg" class="btn">Set all Custom backgrounds to the first row's value</button></p>

  <nav class="toc">
    <b>Contents</b>
    <ol>
      <li><a href="#conversation">Conversation panel</a></li>
      <li><a href="#tools-history">Tools history panel</a></li>
      <li><a href="#changed-files">Changed files panel</a></li>
      <li><a href="#tool-detail">Tool call detail (inspection)</a></li>
      <li><a href="#approval">Approval panel</a></li>
    </ol>
  </nav>

  <h2 id="conversation">1. Conversation panel</h2>
  <div class="tablewrap" id="conv_table"></div>
  <div class="cols" id="conv_cols"></div>

  <h2 id="tools-history">2. Tools history panel</h2>
  <div class="tablewrap" id="th_table"></div>
  <div class="cols" id="th_cols"></div>

  <h2 id="changed-files">3. Changed files panel</h2>
  <div class="tablewrap" id="cf_table"></div>
  <div class="cols" id="cf_cols"></div>

  <h2 id="tool-detail">4. Tool call detail (inspection) &mdash; variants</h2>
  <div class="tablewrap" id="td_table"></div>
  <div class="cols" id="td_cols"></div>

  <h2 id="approval">5. Approval panel &mdash; variants</h2>
  <div class="tablewrap" id="ap_table"></div>
  <div class="cols" id="ap_cols"></div>

<script>
/*__DATA__*/

// ---- exact per-theme palette -------------------------------------------------
// ColorName16 -> RGB straight from Terminal.Gui's ColorToName16Map
// (extern/terminal.gui/.../Color.ColorExtensions.cs). dotsy emits true-color, so
// these are the exact bytes the terminal receives, independent of its own scheme.
//   Black #000000  Blue #0000ff  Green #008000  Cyan #00ffff  Red #ff0000
//   Magenta #ff00ff  Yellow #ffff00  Gray #808080  DarkGray #767676
//   BrightBlue #3b78ff  BrightGreen #16c60c  BrightCyan #61d6d6  BrightRed #e74856
//   BrightMagenta #b4009e  BrightYellow #f9f1a5  White #ffffff
// SelRow (selection bar) carries its own background; DiffHdr/DiffCtx used by the inspection diff view.
const THEMES = {
  dark: { bg:'#000000',
    roles:{ Normal:'#cccccc', Dim:'#767676', Bright:'#ffffff', Cmd:'#61d6d6', Success:'#16c60c',
      Err:'#e74856', Warn:'#f9f1a5', Bullet:'#61d6d6', Code:'#16c60c', Running:'#f9f1a5',
      SynKeyword:'#61d6d6', SynType:'#16c60c', SynString:'#f9f1a5', SynNumber:'#b4009e',
      SelRow:'#ffffff', DiffHdr:'#61d6d6', DiffCtx:'#808080', BtnFocus:'#ffffff' },
    rolesBg:{ SelRow:'#767676', BtnFocus:'#767676' } },
  light: { bg:'#ffffff',
    roles:{ Normal:'#000000', Dim:'#767676', Bright:'#000000', Cmd:'#0000ff', Success:'#008000',
      Err:'#ff0000', Warn:'#a16207', Bullet:'#0000ff', Code:'#008000', Running:'#a16207',
      SynKeyword:'#0000ff', SynType:'#008000', SynString:'#ff0000', SynNumber:'#ff00ff',
      SelRow:'#000000', DiffHdr:'#0000ff', DiffCtx:'#000000', BtnFocus:'#ffffff' },
    rolesBg:{ SelRow:'#808080', BtnFocus:'#0000ff' } },
  borland: { bg:'#0000ff',
    roles:{ Normal:'#cccccc', Dim:'#00ffff', Bright:'#ffffff', Cmd:'#61d6d6', Success:'#16c60c',
      Err:'#e74856', Warn:'#f9f1a5', Bullet:'#61d6d6', Code:'#16c60c', Running:'#f9f1a5',
      SynKeyword:'#ffffff', SynType:'#61d6d6', SynString:'#f9f1a5', SynNumber:'#16c60c',
      SelRow:'#000000', DiffHdr:'#f9f1a5', DiffCtx:'#808080', BtnFocus:'#000000' },
    rolesBg:{ SelRow:'#808080', BtnFocus:'#808080' } },
};

// ---- glyphs (kept as escapes so the generator stays pure ASCII) ----------
const CC = c => String.fromCharCode(c);
const G = { bar:CC(0x2502), bullet:CC(0x2022), dash:CC(0x2500), cross:CC(0x253C), chev:CC(0x203A) };

// ---- char helpers --------------------------------------------------------
const isDigit = c => c >= '0' && c <= '9';
const isLetter = c => /\p{L}/u.test(c);
const isLetterOrDigit = c => /[\p{L}\p{Nd}]/u.test(c);
const isUpper = c => (c >= 'A' && c <= 'Z') || /\p{Lu}/u.test(c);
const isNumChar = c => isDigit(c)||c==='.'||c==='_'||c==='x'||c==='X'||c==='b'||c==='B'||(c>='a'&&c<='f')||(c>='A'&&c<='F');

// ---- SyntaxHighlighter (port of SyntaxHighlighter.cs) ---------------------
const KW = {
  csharp:new Set("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while async await dynamic get set value where yield record init required with not and or file scoped nint nuint global partial when".split(' ')),
  python:new Set("False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield match case type self cls".split(' ')),
  shell:new Set("if then else elif fi for while do done case esac function return in export local readonly declare echo exit source alias unset shift break continue true false null cd ls mkdir rm cp mv cat grep sed awk find chmod curl wget git sudo chown ping".split(' ')),
  js:new Set("break case catch class const continue debugger default delete do else export extends false finally for function if import in instanceof let new null return static super switch this throw true try typeof undefined var void while with yield async await of from interface type enum declare abstract implements namespace module readonly override as satisfies keyof infer never unknown any".split(' ')),
  sql:new Set("SELECT FROM WHERE JOIN LEFT RIGHT INNER OUTER ON AS INSERT INTO VALUES UPDATE SET DELETE CREATE TABLE DROP ALTER ADD COLUMN INDEX VIEW PROCEDURE FUNCTION TRIGGER AND OR NOT IN EXISTS LIKE IS NULL TRUE FALSE GROUP BY ORDER HAVING LIMIT OFFSET DISTINCT ALL UNION WITH CASE WHEN THEN ELSE END BEGIN COMMIT ROLLBACK PRIMARY KEY FOREIGN REFERENCES UNIQUE DEFAULT CHECK CONSTRAINT".split(' ')),
};
function langConfig(lang){
  switch(lang){
    case 'csharp': case 'cs': case 'c#': case 'dotnet': return {kw:KW.csharp,line:'//',hash:false,block:true,str:['"',"'"]};
    case 'c': return {kw:KW.csharp,line:'//',hash:false,block:true,str:['"',"'"]};
    case 'cpp': case 'c++': case 'cc': case 'cxx': return {kw:KW.csharp,line:'//',hash:false,block:true,str:['"',"'"]};
    case 'python': case 'py': return {kw:KW.python,line:null,hash:true,block:false,str:['"',"'"]};
    case 'bash': case 'sh': case 'shell': case 'zsh': case 'fish':
    case 'powershell': case 'ps': case 'ps1': case 'pwsh': return {kw:KW.shell,line:null,hash:true,block:false,str:['"',"'"]};
    case 'javascript': case 'js': case 'typescript': case 'ts': case 'jsx': case 'tsx': return {kw:KW.js,line:'//',hash:false,block:true,str:['"',"'",'`']};
    case 'sql': case 'mysql': case 'postgres': case 'sqlite': return {kw:KW.sql,line:'--',hash:false,block:true,str:['"',"'"],ci:true};
    default: return {kw:new Set(),line:'//',hash:false,block:false,str:['"',"'"]};
  }
}
function highlight(lang, line, inBlock, push){
  if(lang==='json'||lang==='jsonc'){ highlightJson(line, push); return false; }
  return highlightLine(line, langConfig(lang), inBlock, push);
}
function kwHas(cfg,word){ if(cfg.ci){ for(const k of cfg.kw) if(k.toLowerCase()===word.toLowerCase()) return true; return false;} return cfg.kw.has(word); }
function highlightLine(line, cfg, inBlock, push){
  let buf=''; let i=0;
  const flush=()=>{ if(buf){ push(buf,'Normal'); buf=''; } };
  if(inBlock){
    let close = cfg.block ? line.indexOf('*/') : -1;
    if(close<0){ push(line,'Dim'); return true; }
    push(line.slice(0,close+2),'Dim'); i=close+2; inBlock=false;
  }
  while(i<line.length){
    const c=line[i];
    if(cfg.block && c==='/' && line[i+1]==='*'){
      flush(); let close=line.indexOf('*/',i+2);
      if(close>=0){ push(line.slice(i,close+2),'Dim'); i=close+2; }
      else { push(line.slice(i),'Dim'); return true; }
      continue;
    }
    if(cfg.hash && c==='#'){ flush(); push(line.slice(i),'Dim'); return false; }
    if(cfg.line && line.startsWith(cfg.line,i)){ flush(); push(line.slice(i),'Dim'); return false; }
    if(cfg.str.includes(c)){
      flush(); let end=i+1;
      while(end<line.length){
        if(line[end]==='\\' && end+1<line.length){ end+=2; continue; }
        if(line[end]===c){ end++; break; }
        end++;
      }
      push(line.slice(i,end),'SynString'); i=end; continue;
    }
    if(isDigit(c)){
      flush(); let end=i+1;
      while(end<line.length && isNumChar(line[end])) end++;
      push(line.slice(i,end),'SynNumber'); i=end; continue;
    }
    if(isLetter(c)||c==='_'){
      flush(); let end=i;
      while(end<line.length && (isLetterOrDigit(line[end])||line[end]==='_')) end++;
      const word=line.slice(i,end);
      let role='Normal';
      if(kwHas(cfg,word)) role='SynKeyword';
      else if(isUpper(c) && word.length>1) role='SynType';
      push(word,role); i=end; continue;
    }
    buf+=c; i++;
  }
  flush(); return false;
}
function highlightJson(line, push){
  let i=0;
  while(i<line.length){
    const c=line[i];
    if(c==='"'){
      let end=i+1;
      while(end<line.length){
        if(line[end]==='\\'&&end+1<line.length){ end+=2; continue; }
        if(line[end]==='"'){ end++; break; }
        end++;
      }
      let peek=end; while(peek<line.length && line[peek]===' ') peek++;
      const isKey = peek<line.length && line[peek]===':';
      push(line.slice(i,end), isKey?'Cmd':'SynString'); i=end; continue;
    }
    if(isDigit(c)||(c==='-'&&isDigit(line[i+1]||''))){
      let end=i+1;
      while(end<line.length && (isDigit(line[end])||'.eE+-'.includes(line[end]))) end++;
      push(line.slice(i,end),'SynNumber'); i=end; continue;
    }
    if(isLetter(c)){
      let end=i; while(end<line.length && isLetter(line[end])) end++;
      const w=line.slice(i,end);
      push(w, (w==='true'||w==='false'||w==='null')?'SynKeyword':'Normal'); i=end; continue;
    }
    push(c,'Normal'); i++;
  }
}

// ---- MarkdownRenderer (port of MarkdownRenderer.cs) -----------------------
class MD {
  constructor(wrapWidth, push){ this.push=push; this.w=wrapWidth>0?wrapWidth:48;
    this.inCode=false; this.lang=''; this.inBlockComment=false; this.table=[]; this.line=''; }
  write(chunk){ for(const ch of chunk){ if(ch==='\n') this.commit(true); else this.line+=ch; } }
  flush(){ if(this.line.length>0) this.commit(false); if(this.table.length>0) this.emitTable(); }
  commit(nl){ this.renderLine(this.line, nl); this.line=''; }
  renderLine(raw, nl){
    const end=()=>{ if(nl) this.push('\n','Normal'); };
    const t=raw.replace(/^\s+/,'');
    if(t.startsWith('```')||t.startsWith('~~~')){
      if(!this.inCode){ this.lang=t.slice(3).trim().toLowerCase(); this.inBlockComment=false; }
      else { this.lang=''; this.inBlockComment=false; }
      this.inCode=!this.inCode; end(); return;
    }
    if(this.inCode){ this.push('  ','Normal'); this.inBlockComment=highlight(this.lang, raw, this.inBlockComment, this.push); end(); return; }
    if(t.startsWith('|')){ this.table.push(raw); return; }
    if(this.table.length>0) this.emitTable();
    if(t.length===0){ end(); return; }
    if(t[0]==='#'){ const body=t.replace(/^#+/,'').replace(/^\s+/,''); if(body.length>0){ this.inline(body,'Bright'); end(); return; } }
    if(t==='---'||t==='***'||t==='___'||t==='==='){ this.push(G.dash.repeat(this.w),'Dim'); end(); return; }
    if(t.startsWith('> ')){ this.push(G.bar+' ','Dim'); this.inline(t.slice(2),'Dim'); end(); return; }
    if(raw.startsWith('    ')||raw.startsWith('\t')){ this.push('  '+t,'Code'); end(); return; }
    if(t.length>2 && (t.startsWith('- ')||t.startsWith('* ')||t.startsWith('+ '))){ this.push('  '+G.bullet+' ','Bullet'); this.inline(t.slice(2),'Normal'); end(); return; }
    { const dot=t.indexOf('. '); if(dot>0 && dot<=3 && /^[0-9]+$/.test(t.slice(0,dot))){ this.push('  '+t.slice(0,dot+2),'Bullet'); this.inline(t.slice(dot+2),'Normal'); end(); return; } }
    this.inline(raw,'Normal'); end();
  }
  inline(text, base){
    let buf=''; let i=0;
    const flush=()=>{ if(buf){ this.push(buf,base); buf=''; } };
    while(i<text.length){
      const c=text[i];
      if(c==='['){ const cb=text.indexOf(']',i+1);
        if(cb>0 && cb+1<text.length && text[cb+1]==='('){ const cp=text.indexOf(')',cb+2);
          if(cp>0){ flush(); this.push(text.slice(i+1,cb),'Cmd'); i=cp+1; continue; } } }
      if(c==='`'){ const end=text.indexOf('`',i+1); if(end>i){ flush(); this.push(text.slice(i+1,end),'Code'); i=end+1; continue; } }
      if(c==='~' && text[i+1]==='~'){ const end=text.indexOf('~~',i+2); if(end>=0){ flush(); this.push(text.slice(i+2,end),'Dim'); i=end+2; continue; } }
      if((c==='*'||c==='_') && text[i+1]===c){ const m=c+c; const end=text.indexOf(m,i+2); if(end>=0){ flush(); this.push(text.slice(i+2,end),'Bright'); i=end+2; continue; } }
      if(c==='*'){ const end=text.indexOf('*',i+1); if(end>i+1){ flush(); this.push(text.slice(i+1,end),'Bright'); i=end+1; continue; } }
      buf+=c; i++;
    }
    flush();
  }
  emitTable(){
    if(this.table.length===0) return;
    const rows=this.table.map(splitRow); this.table=[];
    const sep=rows.findIndex(cells=>cells.length>0 && cells.every(isSepCell));
    const cols=Math.max(...rows.map(r=>r.length));
    const width=new Array(cols).fill(0);
    for(let ri=0;ri<rows.length;ri++){ if(ri===sep) continue; for(let ci=0;ci<rows[ri].length;ci++) width[ci]=Math.max(width[ci],rows[ri][ci].length); }
    for(let ri=0;ri<rows.length;ri++){
      if(ri===sep){ this.push(width.map(w=>G.dash.repeat(w)).join(G.dash+G.cross+G.dash),'Dim'); this.push('\n','Normal'); continue; }
      const role = sep>=0 && ri<sep ? 'Bright':'Normal';
      const cells=rows[ri]; let s='';
      for(let ci=0;ci<cols;ci++){ s+=((ci<cells.length?cells[ci]:'')).padEnd(width[ci]); if(ci<cols-1) s+=' '+G.bar+' '; }
      this.push(s,role); this.push('\n','Normal');
    }
  }
}
function splitRow(row){ let t=row.trim(); if(t.startsWith('|')) t=t.slice(1); if(t.endsWith('|')) t=t.slice(0,-1); return t.split('|').map(c=>stripInline(c.trim())); }
function isSepCell(c){ return c.length>0 && [...c].every(ch=>ch==='-'||ch===':'||ch===' ') && c.includes('-'); }
function stripInline(s){ let sb=''; let i=0;
  while(i<s.length){ const c=s[i];
    if(c==='['){ const cb=s.indexOf(']',i+1); if(cb>0 && cb+1<s.length && s[cb+1]==='('){ const cp=s.indexOf(')',cb+2); if(cp>0){ sb+=s.slice(i+1,cb); i=cp+1; continue; } } }
    if(c==='`'){ i++; continue; }
    if(c==='~' && s[i+1]==='~'){ i+=2; continue; }
    if((c==='*'||c==='_') && s[i+1]===c){ i+=2; continue; }
    if(c==='*'){ i++; continue; }
    sb+=c; i++; }
  return sb; }

// ---- replay driver (port of AgentWindow.ResumeReplay.cs) ------------------
function buildSegments(data){
  const segs=[];
  const push=(t,role)=>segs.push({t,role});
  push('Resumed session: '+data.sessionId+'\n','Success');
  push('Messages loaded: '+data.count+'\n\n','Dim');
  for(const m of data.messages){
    if(m.role==='user'){
      const text=m.blocks.join('\n').replace(/\s+$/,'');
      if(text.trim().length>0) push('User '+G.chev+' '+text+'\n\n','Cmd');
    } else {
      let header=false;
      for(const b of m.blocks){
        if(b.startsWith('THINK')){ push('Think '+G.chev+' ','Dim'); push(b.slice(5).replace(/\s+$/,'')+'\n','Dim'); continue; }
        if(!header){ push('Agent '+G.chev+' ','Bullet'); header=true; }
        const md=new MD(76, push); md.write(b.replace(/\s+$/,'')); md.flush();
        push('\n','Normal');
      }
      if(header) push('\n','Normal');
    }
  }
  return segs;
}

// ---- render to DOM -------------------------------------------------------
const esc = s => s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
// A "theme" is { bg, roles:{role->fg}, rolesBg:{role->bg} }. A span's background is the role's
// own bg when defined (e.g. the SelRow highlight bar), otherwise the panel background.
function paint(segs, th){
  let html='';
  for(const s of segs){
    const fg=th.roles[s.role]||th.roles.Normal;
    const bg=(th.rolesBg && th.rolesBg[s.role]) || th.bg;
    html+='<span style="color:'+fg+';background:'+bg+'">'+esc(s.t)+'</span>';
  }
  return html;
}

// Live-only lines: Err / Warn / Running never occur in a replayed transcript, so append
// authentic examples (exact dotsy strings; error text is real, pulled from session logs).
function buildExtraSegments(){
  const s=[]; const push=(t,role)=>s.push({t,role});
  push('\n','Normal');
  push(G.dash.repeat(58)+'\n','Dim');
  push('live-only lines (never produced by a replay): Running / Warn / Err\n','Dim');
  push(G.dash.repeat(58)+'\n\n','Dim');
  // Running: the startup banner
  push('         _       _\n','Running');
  push('      __| | ___ | |_ ___ _  _\n','Running');
  push('     / _` |/ _ \\| __/ __| || |\n','Running');
  push('  _ | (_| | (_) | |_\\__ \\ || |\n','Running');
  push(' (_) \\__,_|\\___/ \\__|___/\\_, |\n','Running');
  push('                         |__/\n\n','Running');
  // Warn
  push('[Agent is busy '+CC(0x2014)+' press Ctrl+G to cancel]\n','Warn');
  push('[cancelled]\n','Warn');
  push('unknown command: /foobar  (try /help)\n','Warn');
  push('[warn] trajectory export failed: disk full\n\n','Warn');
  // Err (real tool-error strings from the session logs)
  push('  ['+CC(0x2717)+' Read] File not found: src/Dotsy.Core/ThemeManager.cs\n','Err');
  push('  ['+CC(0x2717)+' Glob] Path not found: **/*Palette.cs\n','Err');
  push('  Unhandled provider error: connection reset by peer\n','Err');
  return s;
}

// Custom theme: per-role fg AND bg, seeded from Dark (dark fg on dark bg; SelRow keeps its bar).
const CUSTOM = { fg:{}, bg:{} };
for(const r of Object.keys(THEMES.dark.roles)){
  CUSTOM.fg[r]=THEMES.dark.roles[r];
  CUSTOM.bg[r]=(THEMES.dark.rolesBg && THEMES.dark.rolesBg[r]) || THEMES.dark.bg;
}
function customTheme(){ return { bg:CUSTOM.bg.Normal, roles:CUSTOM.fg, rolesBg:CUSTOM.bg }; }

// ---- panel sample builders ----------------------------------------------
const conversationSegs = buildExtraSegments().concat(buildSegments(DATA));

// Tools history: one line per call, whole row coloured by status; grouped calls get a dim
// bracket gutter; the selected row is drawn in the SelRow highlight bar (padded to full width).
function toolsHistorySegs(){
  const s=[]; const push=(t,role)=>s.push({t,role});
  const W=40;
  const pad=t=> t.length>=W ? t : t+' '.repeat(W-t.length);
  const V=CC(0x2713), X=CC(0x2717), R=CC(0x25CC);         // check / cross / running dotted circle
  const TL=CC(0x250C), MID=CC(0x2502), BL=CC(0x2514);     // group bracket corners
  function row(bracket, icon, name, arg, tail, role){
    const text=' '+icon+'  '+name.padEnd(10)+arg.padEnd(22)+tail;
    if(role==='SelRow'){ push(pad((bracket||' ')+text)+'\n','SelRow'); return; }
    push(bracket||' ','Dim'); push(pad(text)+'\n',role);
  }
  row(TL,  V, 'Read', 'src/Program.cs', '2s', 'Success');
  row(MID, V, 'Grep', '"Palette" *.cs', '1s', 'Success');
  row(BL,  X, 'Read', 'src/Missing.cs', '', 'Err');
  row('',  R, 'Bash', 'dotnet build', '3s'+CC(0x2026), 'Running');
  row('', ' ', 'Write', 'src/New.cs', '', 'Dim');         // pending
  row('',  V, 'Read', 'src/App.cs (dup)', '', 'Dim');     // skipped
  row('',  V, 'Edit', 'src/Themes.cs', '', 'SelRow');     // selected row
  return s;
}

// Changed files: +/- markers in success/error, path in bright, +N/-N stats; selected row in the bar.
function changedFilesSegs(){
  const s=[]; const push=(t,role)=>s.push({t,role});
  const A=CC(0x21B3);                                      // downwards-arrow-with-tip-right (modified)
  push('  + ','Success'); push('src/Themes.cs\n','Bright');
  push('  - ','Err');     push('src/Old/Legacy.cs\n','Bright');
  push('  '+A+' ','Normal'); push('src/Program.cs','Bright');
  push('   ','Normal'); push('+12','Success'); push('  ','Normal'); push('-3','Err'); push('\n','Normal');
  push('  '+A+' ','Normal'); push('README.md','Bright');
  push('   ','Normal'); push('+4','Success'); push('  ','Normal'); push('-0','Err'); push('\n','Normal');
  const sel='  '+A+' src/Themes.cs   +8  -2';
  push(sel+' '.repeat(Math.max(0,40-sel.length))+'\n','SelRow');   // selected modified row
  return s;
}

// Tool call detail (inspection) - several tool variants stacked. Mirrors ShowInspect,
// FormatEditInspectCells, and RenderUnifiedDiff.
function toolDetailSegs(){
  const s=[]; const push=(t,role)=>s.push({t,role});
  const rule=()=>push('  '+G.dash.repeat(42)+'\n','Dim');
  // A: generic (Read) inspection
  push('  Tool     Read\n','Bright');
  push('  Args     src/Themes.cs\n','Normal');
  push('  Folder   C:\\dev\\ai\\dotsy-development\n','Dim');
  push('\n','Normal');
  push('  Status   OK\n','Success');
  push('  Elapsed  2s\n','Normal');
  push('  Started  14:03:21\n','Normal');
  push('\n','Normal');
  push('  Output:\n','Bright');
  push('\n','Normal');
  push('  namespace Dotsy.Cli.Tui.Colors;\n','Normal');
  push('  internal static class Palette { }\n\n','Normal');
  rule();
  // B: Edit inspection (Search old -> Warn, Replace new -> Success)
  push('  Output\n','Bright');
  push('  1 replacement applied\n','Normal');
  push('\n','Normal');
  push('  Path      ','Dim'); push('src/Themes.cs\n','Normal');
  push('\n','Normal');
  push('  Search:\n','Bright');
  push('    Normal = A(ColorName16.Gray, ColorName16.Black),\n','Warn');
  push('\n','Normal');
  push('  Replace:\n','Bright');
  push('    Normal = new(new Color(204, 204, 204), ColorName16.Black),\n\n','Success');
  rule();
  // C: errored tool inspection (Status ERR -> Err)
  push('  Tool     Read\n','Bright');
  push('  Args     src/Missing.cs\n','Normal');
  push('  Status   ERR\n','Err');
  push('  Elapsed  0s\n','Normal');
  push('\n','Normal');
  push('  Output:\n','Bright');
  push('  File not found: src/Missing.cs\n\n','Normal');
  rule();
  // D: unified-diff view (RenderUnifiedDiff)
  push('  diff --git a/Themes.cs b/Themes.cs\n','Dim');
  push('  @@ -14,7 +14,8 @@ Theme Dark\n','DiffHdr');
  push('     Name = "dark",\n','DiffCtx');
  push('  -    Normal = A(ColorName16.Gray, ColorName16.Black),\n','Err');
  push('  +    // brighter body text\n','Success');
  push('  +    Normal = new(new Color(204,204,204), ColorName16.Black),\n','Success');
  push('     Dim = A(ColorName16.DarkGray, ColorName16.Black),\n','DiffCtx');
  push('  \\ No newline at end of file\n','Dim');
  return s;
}

// Approval panel (ApprovalView.cs): framed prompt in Bright, message in Normal, buttons in Bright
// with the focused button drawn in the BtnFocus bar. Two variants: write-tool (4 choices) and
// read-tool (3 choices), with different buttons focused.
function approvalSegs(){
  const s=[]; const push=(t,role)=>s.push({t,role});
  const H=CC(0x2500), TL=CC(0x250C), BL=CC(0x2514), V=CC(0x2502);
  // The app lays every visible button on one row (PositionButtons), wrapping only when too narrow.
  function panel(title, msg, buttons, focusedIdx){
    push(TL+H+' '+title+' '+H.repeat(Math.max(3,46-title.length))+'\n','Bright');
    push(V+'  ','Bright'); push(msg,'Normal'); push('\n','Bright');
    push(V+'\n','Bright');
    push(V+'  ','Bright');
    buttons.forEach((b,i)=>{
      push(' '+b+' ', i===focusedIdx ? 'BtnFocus' : 'Bright');
      if(i<buttons.length-1) push('  ','Bright');
    });
    push('\n','Bright');
    push(BL+H.repeat(52)+'\n','Bright');
  }
  // In-cwd write: the project button reads "Allow for project".
  panel('Tool approval', 'Write  src/Themes.cs',
        ['Allow once','Always allow','Deny','Allow for project'], 0);
  push('\n','Normal');
  // Out-of-cwd write: the project button shows the cwd-relative path to that project's root.
  panel('Tool approval', 'Write  ..\\shared-lib\\Core.cs',
        ['Allow once','Always allow','Deny','Allow for ..\\shared-lib'], 3);
  return s;
}

// ---- generic section renderer (4 theme columns w/ collapse checkbox) -----
const customPanels=[];   // {pre, segs} repainted live on custom edits
function renderSection(colsId, segs){
  const cols=document.getElementById(colsId);
  for(const name of ['dark','light','borland','custom']){
    const div=document.createElement('div'); div.className='themecol';
    const h=document.createElement('h3');
    const label=document.createElement('label');
    const cb=document.createElement('input'); cb.type='checkbox'; cb.checked=true;
    label.appendChild(cb); label.appendChild(document.createTextNode(name));
    h.appendChild(label); div.appendChild(h);

    const pre=document.createElement('pre'); pre.className='convo';
    const th = name==='custom' ? customTheme() : THEMES[name];
    pre.style.background=th.bg;
    pre.innerHTML=paint(segs, th);
    if(name==='custom') customPanels.push({pre, segs});

    const note=document.createElement('div'); note.className='hidden-note';
    note.textContent='Hidden'; note.style.display='none';
    cb.addEventListener('change', ()=>{
      const on=cb.checked;
      pre.style.display = on ? '' : 'none';
      note.style.display = on ? 'none' : '';
      div.classList.toggle('collapsed', !on);
    });
    div.appendChild(pre); div.appendChild(note); cols.appendChild(div);
  }
}

document.getElementById('sid').textContent = DATA.sessionId;
renderSection('conv_cols', conversationSegs);
renderSection('th_cols',   toolsHistorySegs());
renderSection('cf_cols',   changedFilesSegs());
renderSection('td_cols',   toolDetailSegs());
renderSection('ap_cols',   approvalSegs());

// ---- live custom recolor -------------------------------------------------
function recolorCustom(){
  const th=customTheme();
  for(const p of customPanels){ p.pre.style.background=th.bg; p.pre.innerHTML=paint(p.segs, th); }
  document.querySelectorAll('td.custom-swatch').forEach(td=>{
    const r=td.dataset.role; td.style.color=CUSTOM.fg[r]; td.style.background=CUSTOM.bg[r];
  });
}
const HEX=/^#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/;
function onCustomInput(e){
  const role=e.target.dataset.role, kind=e.target.dataset.kind, val=e.target.value.trim();
  if(!HEX.test(val)){ e.target.style.borderColor='#c0392b'; return; }   // invalid, don't apply
  e.target.style.borderColor='';
  CUSTOM[kind][role]=val;
  document.querySelectorAll('input.hexinput').forEach(inp=>{            // sync duplicate-role boxes
    if(inp!==e.target && inp.dataset.role===role && inp.dataset.kind===kind) inp.value=val;
  });
  recolorCustom();
}

// ---- palette tables (one per panel) --------------------------------------
const SAMPLE = role => 'The quick brown fox '+CC(0x2014)+' '+role;
function buildTable(containerId, rows){
  const tbl=document.createElement('table'); tbl.className='map';
  const thead=document.createElement('thead');
  thead.innerHTML='<tr><th>Element<\/th><th>Palette role<\/th>'
    +'<th style="background:#000000;color:#ffffff;">Dark<\/th><th>Dark hex<\/th>'
    +'<th style="background:#ffffff;color:#000000;">Light<\/th><th>Light hex<\/th>'
    +'<th style="background:#0000ff;color:#ffffff;">Borland<\/th><th>Borland hex<\/th>'
    +'<th style="background:#000000;color:#ffffff;">Custom<\/th><th>Custom fg<\/th><th>Custom bg<\/th><\/tr>';
  tbl.appendChild(thead);
  const tb=document.createElement('tbody');
  for(const [use,role] of rows){
    const tr=document.createElement('tr');
    tr.innerHTML='<td class="use">'+use+'<\/td><td class="role">'+role+'<\/td>';
    for(const t of ['dark','light','borland']){
      const th=THEMES[t];
      const fg=th.roles[role]||th.roles.Normal;
      const bgc=(th.rolesBg && th.rolesBg[role]) || th.bg;
      const sw=document.createElement('td'); sw.className='swatch';
      sw.style.background=bgc; sw.style.color=fg; sw.textContent=SAMPLE(role); tr.appendChild(sw);
      const hx=document.createElement('td'); hx.className='hex'; hx.textContent=fg; tr.appendChild(hx);
    }
    const csw=document.createElement('td'); csw.className='swatch custom-swatch'; csw.dataset.role=role;
    csw.style.color=CUSTOM.fg[role]; csw.style.background=CUSTOM.bg[role]; csw.textContent=SAMPLE(role); tr.appendChild(csw);
    for(const kind of ['fg','bg']){
      const td=document.createElement('td');
      const inp=document.createElement('input');
      inp.type='text'; inp.className='hexinput'; inp.value=CUSTOM[kind][role];
      inp.dataset.role=role; inp.dataset.kind=kind; inp.addEventListener('input', onCustomInput);
      td.appendChild(inp); tr.appendChild(td);
    }
    tb.appendChild(tr);
  }
  tbl.appendChild(tb);
  document.getElementById(containerId).appendChild(tbl);
}

buildTable('conv_table', [
  ['User prompt / link','Cmd'],
  ['Agent label / bullet','Bullet'],
  ['Body & list text','Normal'],
  ['Thinking / blockquote / dim','Dim'],
  ['Heading / bold','Bright'],
  ['Inline & fenced code','Code'],
  ['Resumed header','Success'],
  ['Error line','Err'],
  ['Warning / cancelled','Warn'],
  ['Startup banner','Running'],
  ['Code: keyword','SynKeyword'],
  ['Code: type','SynType'],
  ['Code: string','SynString'],
  ['Code: number','SynNumber'],
]);
buildTable('th_table', [
  ['Completed (OK)','Success'],
  ['Error (ERR)','Err'],
  ['Running','Running'],
  ['Skipped/pending & group bracket','Dim'],
  ['Selected row (highlight bar)','SelRow'],
]);
buildTable('cf_table', [
  ['Added marker (+)','Success'],
  ['Deleted marker (-)','Err'],
  ['File path','Bright'],
  ['Modified arrow & spacing','Normal'],
  ['Selected row (highlight bar)','SelRow'],
]);
buildTable('td_table', [
  ['Field / section header','Bright'],
  ['Values & output text','Normal'],
  ['Labels / folder / file-header','Dim'],
  ['Status OK / additions / Replace','Success'],
  ['Status ERR / deletions','Err'],
  ['Status other / Search (old)','Warn'],
  ['Diff hunk header','DiffHdr'],
  ['Diff context','DiffCtx'],
]);
buildTable('ap_table', [
  ['Panel border & title','Bright'],
  ['Prompt message (tool + args)','Normal'],
  ['Button (unfocused)','Bright'],
  ['Focused button (highlight bar)','BtnFocus'],
]);

// Button: flatten every Custom bg to the first bg box's value (the first table's first row),
// then repaint the Custom sample columns.
document.getElementById('setAllBg').addEventListener('click', ()=>{
  const first=document.querySelector('input.hexinput[data-kind="bg"]');
  if(!first) return;
  const val=first.value.trim();
  if(!HEX.test(val)){ first.style.borderColor='#c0392b'; return; }
  for(const r of Object.keys(CUSTOM.bg)) CUSTOM.bg[r]=val;
  document.querySelectorAll('input.hexinput[data-kind="bg"]').forEach(inp=>{ inp.value=val; inp.style.borderColor=''; });
  recolorCustom();
});
</script>
</body>
</html>
'@

$html = $html.Replace('/*__DATA__*/', "const DATA = $json;")
$outPath = (Join-Path $PSScriptRoot 'themes.html')
Set-Content -Path $outPath -Value $html -Encoding utf8
"Wrote $outPath ($([math]::Round($html.Length/1KB,1)) KB)"

