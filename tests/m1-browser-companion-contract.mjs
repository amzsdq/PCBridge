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
assert(/draft\.rebind/.test(domSource),'pre-Send composer remount recovery required');
assert(/button\.click\(\)/.test(domSource),'native Send control required');
assert(!/KeyboardEvent/.test(domSource),'keyboard-event Send fallback forbidden');

console.log('M1 browser companion contract PASS');
