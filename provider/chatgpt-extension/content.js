/*
 * PCBridge M1 content bridge. Durable state remains in PCBridge Core.
 */
(() => {
  'use strict';
  const DOM=globalThis.PCBridgeChatGPTDOM;
  if (!DOM || globalThis.__pcbridgeM1Content) return;
  globalThis.__pcbridgeM1Content=true;
  const documentEpoch=crypto.randomUUID();
  const inFlight=new Set();
  const sendRuntime=payload => chrome.runtime.sendMessage(payload).catch(() => null);

  async function handle(command) {
    if (!command || typeof command.id!=='string' || inFlight.has(command.id)) return;
    if (DOM.conversationId()!==command.conversation_id) return;
    inFlight.add(command.id);
    let prepared=null;
    try {
      prepared=await DOM.prepareSend(command);
      if (!prepared.ok) {
        await sendRuntime({type:'pcbridge.m1.event',event:{
          command_id:command.id,kind:'pre_send_failed',error:prepared.error,
          conversation_id:DOM.conversationId(),document_epoch:documentEpoch
        }});
        return;
      }
      const claimed=await sendRuntime({type:'pcbridge.m1.event',event:{
        command_id:command.id,kind:'composer_claimed',conversation_id:command.conversation_id,
        document_epoch:documentEpoch,rebound:prepared.rebound===true
      }});
      if (!claimed?.ok || !prepared.draft.current()) return;
      const authorized=await sendRuntime({type:'pcbridge.m1.authorize',
        command_id:command.id,conversation_id:command.conversation_id,
        document_epoch:documentEpoch,payload_hash:command.payload_hash});
      if (!authorized?.ok) return;
      if (!prepared.draft.current()) {
        await sendRuntime({type:'pcbridge.m1.event',event:{
          command_id:command.id,kind:'ambiguous',error:'lease_lost_after_dispatch_intent',
          conversation_id:DOM.conversationId(),document_epoch:documentEpoch
        }});
        return;
      }
      const result=await DOM.dispatchPrepared(command,prepared);
      await sendRuntime({type:'pcbridge.m1.event',event:{
        command_id:command.id,
        kind:result.status==='delivered' ? 'delivered' : 'ambiguous',
        error:result.error || '',user_message_id:result.user_message_id || '',
        conversation_id:result.conversation_id || DOM.conversationId(),document_epoch:documentEpoch
      }});
    } finally {
      try { prepared?.draft?.dispose(); } catch {}
      inFlight.delete(command.id);
    }
  }

  chrome.runtime.onMessage.addListener((message,sender,sendResponse) => {
    if (message?.type!=='pcbridge.m1.send') return;
    const command=message.command;
    if (DOM.conversationId()!==command?.conversation_id) {
      sendResponse({accepted:false,error:'target_mismatch',document_epoch:documentEpoch}); return;
    }
    void handle(command);
    sendResponse({accepted:true,document_epoch:documentEpoch});
    return true;
  });

  void sendRuntime({type:'pcbridge.m1.page',page:{
    conversation_id:DOM.conversationId(),document_epoch:documentEpoch,ready:DOM.composerReady()
  }});
})();
