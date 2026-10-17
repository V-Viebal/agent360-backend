// Usage: OUT_DIR=.tmp/x/ [OP_KEY=<operator token>] node ui_cdp_check.mjs   (Chrome CDP on 127.0.0.1:9222)
// Expects: health line, locked prompt, External section, topology; with OP_KEY: unlocked + ledger rows.
const out = process.env.OUT_DIR || "./";
const PARENT_RE = process.env.PARENT_LABEL_RE || "^Mons \\(Co";
const APP = process.env.APP_URL || "https://agent360.antares.vn/";
import fs from "fs";
const t = await (await fetch("http://127.0.0.1:9222/json/new?"+APP, {method:"PUT"})).json();
const ws = new WebSocket(t.webSocketDebuggerUrl); let id=0; const pend={};
const reqs={};ws.onmessage = e => { const m=JSON.parse(e.data); if(m.method==='Network.requestWillBeSent')reqs[m.params.requestId]=m.params.request.method+' '+m.params.request.url; if(m.method==='Network.responseReceived'&&m.params.response.status>=400)console.log('NET',m.params.response.status,reqs[m.params.requestId]); if(m.id&&pend[m.id]){pend[m.id](m);delete pend[m.id];} };
await new Promise(r=>ws.onopen=r);
const send=(method,params={})=>new Promise(r=>{const i=++id;pend[i]=r;ws.send(JSON.stringify({id:i,method,params}));});
const ev=async(x)=>(await send("Runtime.evaluate",{expression:x,awaitPromise:true,returnByValue:true})).result?.result?.value;
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
await send("Network.enable");
await send("Emulation.setDeviceMetricsOverride",{width:1600,height:1000,deviceScaleFactor:1,mobile:false});
await sleep(6000);
const shot=async n=>{const s=await send("Page.captureScreenshot",{format:"png"}); fs.writeFileSync(out+n,Buffer.from(s.result.data,"base64"));};
console.log("url", await ev("location.href"));
console.log("buttons", await ev("[...document.querySelectorAll('button,a')].map(b=>b.innerText.trim()).filter(Boolean).slice(0,60).join(' | ')"));
await shot("01-landing.png");
// try open workspace
for (const sel of ["Workspace","Open Workspace","Launch","Get started","Console"]) {
  const ok = await ev(`(()=>{const b=[...document.querySelectorAll('button,a')].find(b=>b.innerText.trim().toLowerCase().includes(${JSON.stringify(sel.toLowerCase())}));if(b){b.click();return true}return false})()`);
  if (ok) { console.log("clicked", sel); break; }
}
await sleep(6000);
console.log("url2", await ev("location.href"));
await shot("02-workspace.png");
// pick mons
console.log("pick", await ev(`(()=>{const e=[...document.querySelectorAll('button,[role=button],div')].filter(x=>new RegExp(${JSON.stringify(PARENT_RE)}).test((x.innerText||'').trim())).pop();if(e){e.click();return e.innerText.slice(0,40)}return null})()`));
await sleep(3000);
const tab = await ev(`(()=>{const b=[...document.querySelectorAll('button')].find(b=>b.innerText.trim()==='Agents'||/^Agents/.test(b.innerText.trim()));if(b){b.click();return b.innerText}return null})()`);
console.log("agents tab", tab);
await sleep(6000);
await ev("(()=>{const b=[...document.querySelectorAll('button')].filter(b=>/^Delegations/.test(b.innerText.trim())).pop();b&&b.click()})()"); await sleep(3000);
console.log("health", await ev("(()=>{const t=document.body.innerText;const i=t.indexOf('AgentTeams A2A ok');const j=t.indexOf('AgentTeams A2A degraded');return i>=0?t.slice(i,i+160):j>=0?t.slice(j,j+200):'none'})()"));
console.log("lockedPrompt", await ev("!!document.querySelector('input[placeholder^=\"A2A operator key\"]')"));
console.log("hasExternal", await ev("/External \\(AgentTeams A2A\\)/i.test(document.body.innerText)"));
console.log("hasGm", await ev("document.body.innerText.includes('agent://gm/default')"));
console.log("snippet", await ev("(()=>{const t=document.body.innerText;const i=t.search(/External Sub-Agents/i);return i<0?t.slice(0,600):t.slice(i,i+900)})()"));
await shot("ui-locked.png");
if (process.env.OP_KEY) {
  await ev(`(()=>{localStorage.setItem('agent360.a2a.operatorKey', ${JSON.stringify(process.env.OP_KEY||"")});})()`);
  await send("Page.reload"); await sleep(8000);
  await ev(`(()=>{const e=[...document.querySelectorAll('button,[role=button],div')].filter(x=>new RegExp(${JSON.stringify(PARENT_RE)}).test((x.innerText||'').trim())).pop();e&&e.click()})()`); await sleep(3000);
  await ev(`(()=>{const b=[...document.querySelectorAll('button')].find(b=>b.innerText.trim()==='Agents');b&&b.click()})()`); await sleep(5000);
  await ev("(()=>{const b=[...document.querySelectorAll('button')].filter(b=>/^Delegations \\(/.test(b.innerText.trim())).pop();b&&b.click()})()"); await sleep(3000);
  console.log("unlocked", await ev("document.body.innerText.includes('Operator unlocked')"));
  console.log("delegationsTab", await ev("([...document.querySelectorAll('button')].map(b=>b.innerText.trim()).filter(t=>/^Delegations \\(/.test(t)))"));
  console.log("ledgerRows", await ev("(document.body.innerText.match(/a2a_[a-z_0-9]+/g)||[]).length"));
  await ev("localStorage.removeItem('agent360.a2a.operatorKey')");
  await shot("ui-unlocked.png");
}
await send("Target.closeTarget",{targetId:t.id}).catch(()=>{});
ws.close(); process.exit(0);
