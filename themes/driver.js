// ---- batch analyzer (appended after the extracted renderer) --------------
const _fs = require('fs');
const _path = require('path');

function parseSession(file){
  const lines = _fs.readFileSync(file, 'utf8').split(/\r?\n/);
  let sessionId = null, count = 0;
  const messages = [];
  for(const l of lines){
    if(!l.trim()) continue;
    let o; try { o = JSON.parse(l); } catch { continue; }
    if(!sessionId && o.sessionId) sessionId = o.sessionId;
    const type = o.type;
    if(type === 'user'){
      const c = o.message && o.message.content;
      if(typeof c === 'string'){ count++; const t = c.replace(/\s+$/,''); if(t.trim()) messages.push({role:'user', blocks:[t]}); }
      else if(Array.isArray(c)){ count++; const texts=[]; for(const b of c){ if(b.type==='text' && b.text && b.text.trim()) texts.push(b.text.replace(/\s+$/,'')); } if(texts.length) messages.push({role:'user', blocks:texts}); }
    } else if(type === 'assistant'){
      count++; const texts=[]; const cont = (o.message && o.message.content) || [];
      for(const b of cont){
        if(b.type==='text' && b.text && b.text.trim()) texts.push(b.text.replace(/\s+$/,''));
        else if(b.type==='thinking' && b.thinking && b.thinking.trim()) texts.push('THINK'+b.thinking.replace(/\s+$/,''));
      }
      if(texts.length) messages.push({role:'agent', blocks:texts});
    } else if(type === 'tool_result'){ count++; }
  }
  return { sessionId, count, messages };
}

const dirsFile = process.argv[2];
const dirs = _fs.readFileSync(dirsFile, 'utf8').split(/\r?\n/).map(s=>s.trim()).filter(Boolean);
const results = [];
for(const dir of dirs){
  let files; try { files = _fs.readdirSync(dir); } catch { continue; }
  for(const f of files){
    if(!f.endsWith('.jsonl')) continue;
    const p = _path.join(dir, f);
    let data; try { data = parseSession(p); } catch { continue; }
    if(!data.messages.length) continue;
    let segs; try { segs = buildSegments(data); } catch { continue; }
    const roles = new Set(segs.map(s => s.role));
    results.push({ p, id: data.sessionId||f, distinct: roles.size, roles: [...roles].sort(), segs: segs.length });
  }
}
results.sort((a,b) => b.distinct - a.distinct || b.segs - a.segs);
console.log('DISTINCT\tSEGS\tSESSION\tROLES\tPATH');
for(const r of results.slice(0, 20)){
  console.log(r.distinct + '\t' + r.segs + '\t' + r.id + '\t[' + r.roles.join(',') + ']\t' + r.p);
}
console.log('--- total sessions analyzed: ' + results.length + ' ---');
