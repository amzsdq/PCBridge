/*
 * PCBridge M1 dedicated-profile service worker.
 * The pairing token stays in extension storage and is never exposed to the ChatGPT page.
 */
const PORTS=[8795,8796,8797,8798,8799];
const PROTOCOL='m1-browser-v1';
const TERMINAL_CACHE_MAX=256;
let broker=null,pumpWork=null,stopped=false;

function headers(auth=true) {
  const h={'X-PCBridge-Protocol':PROTOCOL,'Content-Type':'application/json'};
  if (auth && broker?.token) h.Authorization='Bearer '+broker.token;
  return h;
}

async function call(port,path,options={}) {
  const controller=new AbortController();
  const timeout=setTimeout(()=>controller.abort(),options.timeout ?? 30000);
  try {
    const response=await fetch('http://127.0.0.1:'+port+path,{
      method:options.method || 'GET',
      headers:options.headers || headers(options.auth!==false),
      body:options.body===undefined ? undefined : JSON.stringify(options.body),
      signal:controller.signal,
      cache:'no-store'
    });
    const text=await response.text();
    let body={};
    try { body=text ? JSON.parse(text) : {}; } catch {}
    return {ok:response.ok,status:response.status,body};
  } finally { clearTimeout(timeout); }
}

async function pair() {
  const saved=await chrome.storage.local.get(['pcbridgeM1Port','pcbridgeM1Token']);
  const candidates=saved.pcbridgeM1Port
    ? [saved.pcbridgeM1Port,...PORTS.filter(port=>port!==saved.pcbridgeM1Port)]
    : PORTS;

  for (const port of candidates) {
    try {
      const hello=await call(port,'/hello',{auth:false,timeout:1500});
      if (!hello.ok || hello.body?.app!=='pcbridge' || hello.body?.protocol!==PROTOCOL) continue;

      if (saved.pcbridgeM1Port===port && saved.pcbridgeM1Token) {
        broker={port,token:saved.pcbridgeM1Token};
        const probe=await call(port,'/v1/presence',{timeout:1500});
        if (probe.ok) return true;
      }

      const response=await call(port,'/pair',{
        method:'POST',auth:false,body:{protocol:PROTOCOL},timeout:2000
      });
      if (!response.ok || typeof response.body?.token!=='string') continue;
      broker={port,token:response.body.token};
      await chrome.storage.local.set({
        pcbridgeM1Port:port,
        pcbridgeM1Token:response.body.token
      });
      return true;
    } catch {}
  }
  broker=null;
  return false;
}

async function terminalIds() {
  const {pcbridgeM1Terminal=[]}=await chrome.storage.local.get('pcbridgeM1Terminal');
  return Array.isArray(pcbridgeM1Terminal) ? pcbridgeM1Terminal : [];
}

async function rememberTerminal(id) {
  const prior=await terminalIds();
  await chrome.storage.local.set({
    pcbridgeM1Terminal:[...prior.filter(value=>value!==id),id].slice(-TERMINAL_CACHE_MAX)
  });
}

async function postEvent(event) {
  if (!broker && !await pair()) return {ok:false};
  try {
    const response=await call(broker.port,'/v1/event',{
      method:'POST',body:event,timeout:5000
    });
    if (!response.ok && response.status===401) {
      broker=null;
      return {ok:false};
    }
    if (response.ok && [
      'source_terminal','source_superseded',
      'delivered','ambiguous',
      'terminal_confirmed','response_superseded'
    ].includes(event.kind)) await rememberTerminal(event.command_id);
    return {ok:response.ok,status:response.status};
  } catch {
    return {ok:false};
  }
}

async function authorize(message) {
  if (!broker && !await pair()) return {ok:false};
  try {
    const response=await call(broker.port,'/v1/authorize',{
      method:'POST',
      body:{
        command_id:message.command_id,
        conversation_id:message.conversation_id,
        document_epoch:message.document_epoch,
        payload_hash:message.payload_hash
      },
      timeout:5000
    });
    if (!response.ok && response.status===401) broker=null;
    return {ok:response.ok && response.body?.ok===true,status:response.status};
  } catch {
    return {ok:false};
  }
}

function conversationFromUrl(raw) {
  try {
    const url=new URL(raw || '');
    if (url.protocol!=='https:' || !['chatgpt.com','chat.openai.com'].includes(url.hostname)) return null;
    return /^\/(?:g\/[^/]+\/)?c\/([0-9a-f-]{8,64})(?:\/|$)/i.exec(url.pathname)?.[1] || null;
  } catch { return null; }
}

async function exactTab(conversationId) {
  const tabs=await chrome.tabs.query({});
  const matches=tabs.filter(tab=>conversationFromUrl(tab.url)===conversationId);
  return matches.length===1 ? matches[0] : null;
}

async function deliver(command) {
  if (!command || typeof command.id!=='string' || typeof command.conversation_id!=='string') return;
  if ((await terminalIds()).includes(command.id)) return;

  let tab=await exactTab(command.conversation_id);
  if (!tab) {
    tab=await chrome.tabs.create({
      url:'https://chatgpt.com/c/'+encodeURIComponent(command.conversation_id),
      active:false
    });
    if (!tab?.id) return;
  }

  const type=
    command.kind==='gate_current_turn' ? 'pcbridge.m1.gate' :
    command.kind==='send_handoff' ? 'pcbridge.m1.send' :
    command.kind==='observe_response' ? 'pcbridge.m1.observe' :
    null;
  if (!type) return;

  try {
    await chrome.tabs.sendMessage(tab.id,{type,command});
  } catch {
    // The exact tab may still be loading. Core retains custody and the command lease expires.
  }
}

async function pump() {
  if (pumpWork) return pumpWork;
  pumpWork=(async()=>{
    while(!stopped) {
      if (!broker && !await pair()) {
        await new Promise(resolve=>setTimeout(resolve,5000));
        continue;
      }
      try {
        const response=await call(broker.port,'/v1/command?wait_ms=25000',{timeout:30000});
        if (response.status===401) {
          broker=null;
          continue;
        }
        if (response.ok && response.body?.command) await deliver(response.body.command);
      } catch {
        broker=null;
        await new Promise(resolve=>setTimeout(resolve,2000));
      }
    }
  })().finally(()=>{pumpWork=null;});
  return pumpWork;
}

chrome.runtime.onMessage.addListener((message,sender,sendResponse)=>{
  if (message?.type==='pcbridge.m1.event') {
    void postEvent(message.event).then(sendResponse);
    return true;
  }
  if (message?.type==='pcbridge.m1.authorize') {
    void authorize(message).then(sendResponse);
    return true;
  }
  if (message?.type==='pcbridge.m1.page') {
    void postEvent({kind:'page_presence',...message.page}).then(sendResponse);
    return true;
  }
});

chrome.runtime.onStartup.addListener(()=>{stopped=false;void pump();});
chrome.runtime.onInstalled.addListener(()=>{stopped=false;void pump();});
chrome.alarms.create('pcbridge-m1-wake',{periodInMinutes:0.5});
chrome.alarms.onAlarm.addListener(alarm=>{
  if (alarm.name==='pcbridge-m1-wake') void pump();
});
void pump();
