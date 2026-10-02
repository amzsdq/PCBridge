import fs from 'node:fs';
import vm from 'node:vm';

const domSource=fs.readFileSync('provider/chatgpt-extension/chatgpt-dom.js','utf8');
const bgSource=fs.readFileSync('provider/chatgpt-extension/background.js','utf8');
const manifest=JSON.parse(fs.readFileSync('provider/chatgpt-extension/manifest.json','utf8'));
function assert(ok,message){if(!ok)throw new Error(message);}

const exposed={};
const sandbox={
  globalThis:exposed,
  location:{pathname:'/',href:'https://chatgpt.com/'},
  document:{querySelectorAll:()=>[],querySelector:()=>null,documentElement:{}},
  getComputedStyle:()=>({display:'block',visibility:'visible'}),
  InputEvent:class {},
  MutationObserver:class {observe(){} disconnect(){}},
  setTimeout,clearTimeout,console
};
vm.createContext(sandbox);
vm.runInContext(domSource,sandbox);
const DOM=exposed.PCBridgeChatGPTDOM;
assert(DOM,'DOM adapter did not export');
const id='12345678-abcd-4abc-8abc-123456789012';
assert(DOM.conversationFromPath('/c/'+id)===id,'root conversation route');
assert(DOM.conversationFromPath('/g/project-slug/c/'+id)===id,'project conversation route');
assert(DOM.conversationFromPath('/share/c/'+id)===null,'share route refused');
assert(DOM.conversationFromPath('/c/not-an-id')===null,'invalid id refused');

assert(manifest.manifest_version===3,'MV3 required');
assert(manifest.content_scripts?.some(x=>x.matches?.includes('https://chatgpt.com/*')),'ChatGPT content script missing');
assert(manifest.host_permissions?.every(p=>!p.includes('*://*/*')),'global host permission forbidden');
assert(!/SendKeys|navigator\.clipboard|document\.execCommand\(['"]paste/i.test(bgSource),'OS/clipboard send path forbidden');
assert(/active:false/.test(bgSource),'new exact-target tab must be background');
assert(/pcbridgeM1Terminal/.test(bgSource),'terminal command dedupe cache required');
assert(/\/v1\/authorize/.test(bgSource),'pre-Send authorization barrier required');
assert(/status:'ambiguous'/.test(domSource),'ambiguous delivery state required');
assert(typeof DOM.responseSnapshot==='function','exact response ownership snapshot required');
assert(typeof DOM.latestUserResponseSnapshot==='function','source-turn ownership snapshot required');
assert(typeof DOM.providerIssue==='function','provider issue classifier required');
assert(typeof DOM.reconcileReceipt==='function','ambiguous receipt reconciliation required');
assert(/draft\.rebind/.test(domSource),'pre-Send composer remount recovery required');
assert(/button\.click\(\)/.test(domSource),'native Send control required');
assert(!/KeyboardEvent/.test(domSource),'keyboard-event Send fallback forbidden');

const contentSource=fs.readFileSync('provider/chatgpt-extension/content.js','utf8');
assert(/source_bound/.test(contentSource) && /source_terminal/.test(contentSource),'source-turn gate events required');
assert(/response_started/.test(contentSource) && /terminal_observed/.test(contentSource) && /terminal_confirmed/.test(contentSource),'response ownership lifecycle required');
assert(/TERMINAL_SETTLE_MS/.test(contentSource),'terminal settle window required');
assert(/provider_rate_limited/.test(contentSource) && /provider_auth_required/.test(contentSource) && /provider_load_failed/.test(contentSource),'provider recovery classifications required');
assert(/ambiguous_delivered/.test(contentSource) && /ambiguous_unresolved/.test(contentSource),'ambiguous reconciliation outcomes required');
assert(/MutationObserver/.test(contentSource),'response evidence must be mutation-driven');
assert(/gate_current_turn/.test(bgSource) && /send_handoff/.test(bgSource) && /observe_response/.test(bgSource),'staged browser routing required');
assert(/recover_target/.test(bgSource) && /reconcile_ambiguous/.test(bgSource),'recovery and ambiguity command routing required');
assert(/chrome\.tabs\.reload/.test(bgSource),'exact-target recovery must use background tab reload');
assert(/ambiguous_unresolved/.test(bgSource),'unresolved ambiguous delivery must fail closed');
const reconcileFn=bgSource.slice(bgSource.indexOf('async function deliverReconcile'),bgSource.indexOf('async function deliver(command)'));
assert(reconcileFn.includes('pcbridge.m1.reconcile'),'reconcile function must invoke only the reconciliation content path');
assert(!reconcileFn.includes('pcbridge.m1.send'),'ambiguity reconciliation must not reuse native Send');
assert(/active:false/.test(bgSource),'provider tab must never be activated by normal delivery');
assert(!/chrome\.windows\.update\([^\n]*focused\s*:\s*true/.test(bgSource),'foreground focus activation forbidden');

console.log('M1 browser companion contract PASS');
