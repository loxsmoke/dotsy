// ---- slice finder (appended after the extracted renderer) ----------------
const _fs = require('fs');
const SESSION = process.argv[2];
const OUT = process.argv[3];

function parseSession(file){
  const lines = _fs.readFileSync(file, 'utf8').split(/\r?\n/);
  let sessionId = null;
  const messages = [];
  for(const l of lines){
    if(!l.trim()) continue;
    let o; try { o = JSON.parse(l); } catch { continue; }
    if(!sessionId && o.sessionId) sessionId = o.sessionId;
    const type = o.type;
    if(type === 'user'){
      const c = o.message && o.message.content;
      if(typeof c === 'string'){ const t=c.replace(/\s+$/,''); if(t.trim()) messages.push({role:'user', blocks:[t]}); }
      else if(Array.isArray(c)){ const texts=[]; for(const b of c){ if(b.type==='text'&&b.text&&b.text.trim()) texts.push(b.text.replace(/\s+$/,'')); } if(texts.length) messages.push({role:'user', blocks:texts}); }
    } else if(type === 'assistant'){
      const texts=[]; const cont=(o.message&&o.message.content)||[];
      for(const b of cont){
        if(b.type==='text'&&b.text&&b.text.trim()) texts.push(b.text.replace(/\s+$/,''));
        else if(b.type==='thinking'&&b.thinking&&b.thinking.trim()) texts.push('THINK'+b.thinking.replace(/\s+$/,''));
      }
      if(texts.length) messages.push({role:'agent', blocks:texts});
    }
  }
  return { sessionId, messages };
}

// render one message WITHOUT the resumed-header, mirroring buildSegments' loop body
function renderMsg(m){
  const segs=[]; const push=(t,role)=>segs.push({t,role});
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
  return segs;
}
const nl = segs => segs.reduce((n,s)=> n + (s.t.split('\n').length-1), 0);

const data = parseSession(SESSION);
const M = data.messages;
const per = M.map(m => { const s=renderMsg(m); return { roles:new Set(s.map(x=>x.role)), lines:nl(s) }; });

const NEED = ['Cmd','Bullet','Normal','Bright','Code','SynKeyword','SynType','SynString','SynNumber'];
const HEADER_LINES = 3;

// minimal contiguous window covering all NEED roles (min lines); shrink-from-left sliding window
function windowStats(i,j){
  const roles=new Set(['Success','Dim']); let lines=HEADER_LINES;
  for(let k=i;k<=j;k++){ per[k].roles.forEach(r=>roles.add(r)); lines+=per[k].lines; }
  return { roles, lines };
}
function covers(roles){ return NEED.every(r=>roles.has(r)); }

console.log('messages='+M.length);
// role spread diagnostics
for(const r of NEED){
  const idx=[]; per.forEach((p,k)=>{ if(p.roles.has(r)) idx.push(k); });
  console.log('  '+r.padEnd(11)+' count='+String(idx.length).padStart(3)+' first='+(idx[0]??'-')+' last='+(idx[idx.length-1]??'-'));
}

// Structure: opening user turn (msg 0, the only Cmd) + contiguous tail block that
// includes the code-heavy messages carrying every syntax color. Pick the tail start
// so the whole thing lands near TARGET lines without exceeding MAX.
const TARGET=125, MAX=150;
const END = M.length-1;               // 255 (has SynNumber/SynType/Bright)
const MUST_INCLUDE = 241;             // has SynKeyword/SynString/SynType
function total(k){ let L=HEADER_LINES+per[0].lines; for(let j=k;j<=END;j++) L+=per[j].lines; return L; }
let bestK=MUST_INCLUDE, bestDiff=Infinity;
for(let k=MUST_INCLUDE;k>=1;k--){
  const t=total(k);
  if(t>MAX) break;
  const diff=Math.abs(t-TARGET);
  if(diff<bestDiff){ bestDiff=diff; bestK=k; }
}
const sel=[0];
for(let j=bestK;j<=END;j++) sel.push(j);
const curLines=total(bestK);
console.log('tail start='+bestK+'  slice=[0]+['+bestK+'..'+END+']  lines='+curLines);

const slice = sel.map(k=>M[k]);
const out = { sessionId: data.sessionId, count: slice.length, messages: slice };
const fsegs = buildSegments(out);
const froles=[...new Set(fsegs.map(s=>s.role))].sort();
const totalNl = nl(fsegs)+1;
console.log('FINAL slice: msgs='+slice.length+' rendered-lines='+totalNl+' distinct-roles='+froles.length+' ['+froles+']');
_fs.writeFileSync(OUT, JSON.stringify(out));
console.log('wrote '+OUT);
