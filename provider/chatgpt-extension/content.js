/*
 * PCBridge M1 content bridge.
 * Durable state remains in PCBridge Core; this document only reports exact DOM evidence.
 */
(() => {
  'use strict';
  const DOM=globalThis.PCBridgeChatGPTDOM;
  if (!DOM || globalThis.__pcbridgeM1Content) return;
  globalThis.__pcbridgeM1Content=true;

  const documentEpoch=crypto.randomUUID();
  const inFlight=new Set();
  const monitors=new Map();
  const TERMINAL_SETTLE_MS=1500;
  const sendRuntime=payload => chrome.runtime.sendMessage(payload).catch(() => null);

  const eventFor=(command,kind,extra={}) => ({
    type:'pcbridge.m1.event',
    event:{
      command_id:command.id,
      kind,
      conversation_id:command.conversation_id,
      document_epoch:documentEpoch,
      payload_hash:command.payload_hash || '',
      ...extra
    }
  });

  async function post(command,kind,extra={}) {
    return sendRuntime(eventFor(command,kind,extra));
  }

  function startResponseMonitor(command,mode) {
    if (!command || monitors.has(command.id) || DOM.conversationId()!==command.conversation_id) return false;

    let stopped=false,busy=false,dirty=false;
    let sourceUser=command.user_message_id || '';
    let sourceTurn=command.response_turn_id || '';
    let responseStarted=false;
    let terminalSignature='';
    let terminalSince=0;
    let settleTimer=null;

    const stop=() => {
      if (stopped) return;
      stopped=true;
      observer.disconnect();
      if (settleTimer) clearTimeout(settleTimer);
      monitors.delete(command.id);
    };

    const scheduleSettle=() => {
      if (settleTimer || stopped) return;
      settleTimer=setTimeout(() => {
        settleTimer=null;
        void check();
      },TERMINAL_SETTLE_MS+100);
    };

    const runCheck=async() => {
      if (DOM.conversationId()!==command.conversation_id) { stop(); return; }

      if (mode==='gate') {
        let snapshot=sourceUser
          ? DOM.latestUserResponseSnapshot(sourceUser,sourceTurn)
          : DOM.latestUserResponseSnapshot();

        if (!snapshot?.found) return;
        if (snapshot.superseded) {
          await post(command,'source_superseded',{
            user_message_id:sourceUser || snapshot.user_message_id || '',
            response_turn_id:sourceTurn || snapshot.response_turn_id || ''
          });
          stop();return;
        }

        if (!sourceUser) {
          if (!snapshot.user_message_id || !snapshot.response_turn_id) return;
          sourceUser=snapshot.user_message_id;
          sourceTurn=snapshot.response_turn_id;
          const bound=await post(command,'source_bound',{
            user_message_id:sourceUser,response_turn_id:sourceTurn
          });
          if (!bound?.ok) { stop();return; }
          snapshot=DOM.latestUserResponseSnapshot(sourceUser,sourceTurn);
        }

        if (snapshot.response_turn_id && snapshot.response_turn_id!==sourceTurn) {
          await post(command,'source_superseded',{
            user_message_id:sourceUser,response_turn_id:sourceTurn,error:'source_response_turn_changed'
          });
          stop();return;
        }

        if (snapshot.terminal_candidate && snapshot.terminal_signature) {
          if (terminalSignature!==snapshot.terminal_signature) {
            terminalSignature=snapshot.terminal_signature;
            terminalSince=Date.now();
            scheduleSettle();
            return;
          }
          if (Date.now()-terminalSince>=TERMINAL_SETTLE_MS) {
            const verify=DOM.latestUserResponseSnapshot(sourceUser,sourceTurn);
            if (verify?.terminal_candidate && verify.terminal_signature===terminalSignature && !verify.superseded) {
              const done=await post(command,'source_terminal',{
                user_message_id:sourceUser,response_turn_id:sourceTurn
              });
              if (done?.ok) stop();
            }
          } else scheduleSettle();
        } else {
          terminalSignature='';
          terminalSince=0;
        }
        return;
      }

      const snapshot=DOM.responseSnapshot(command.user_message_id,sourceTurn);
      if (!snapshot?.found) return;
      if (snapshot.superseded || snapshot.conflict) {
        await post(command,'response_superseded',{
          user_message_id:command.user_message_id,
          response_turn_id:sourceTurn || snapshot.response_turn_id || '',
          error:snapshot.conflict || 'newer_user_message'
        });
        stop();return;
      }

      if (snapshot.started && snapshot.response_turn_id) {
        if (!sourceTurn) sourceTurn=snapshot.response_turn_id;
        if (snapshot.response_turn_id!==sourceTurn) {
          await post(command,'response_superseded',{
            user_message_id:command.user_message_id,response_turn_id:sourceTurn,
            error:'response_turn_changed'
          });
          stop();return;
        }
        if (!responseStarted) {
          const bound=await post(command,'response_started',{
            user_message_id:command.user_message_id,response_turn_id:sourceTurn
          });
          if (!bound?.ok) return;
          responseStarted=true;
        }
      }

      if (!responseStarted) return;

      if (snapshot.terminal_candidate && snapshot.terminal_signature) {
        if (terminalSignature!==snapshot.terminal_signature) {
          terminalSignature=snapshot.terminal_signature;
          terminalSince=Date.now();
          const observed=await post(command,'terminal_observed',{
            user_message_id:command.user_message_id,response_turn_id:sourceTurn
          });
          if (!observed?.ok) { terminalSignature='';terminalSince=0;return; }
          scheduleSettle();
          return;
        }
        if (Date.now()-terminalSince>=TERMINAL_SETTLE_MS) {
          const verify=DOM.responseSnapshot(command.user_message_id,sourceTurn);
          if (verify?.terminal_candidate && verify.terminal_signature===terminalSignature && !verify.superseded) {
            const confirmed=await post(command,'terminal_confirmed',{
              user_message_id:command.user_message_id,response_turn_id:sourceTurn
            });
            if (confirmed?.ok) stop();
          }
        } else scheduleSettle();
      } else if (terminalSignature) {
        terminalSignature='';
        terminalSince=0;
        await post(command,'turn_running',{
          user_message_id:command.user_message_id,response_turn_id:sourceTurn
        });
      }
    };

    const check=async() => {
      if (stopped) return;
      if (busy) { dirty=true;return; }
      busy=true;
      try { await runCheck(); }
      finally {
        busy=false;
        if (dirty && !stopped) { dirty=false;queueMicrotask(() => void check()); }
      }
    };

    const observer=new MutationObserver(() => void check());
    observer.observe(document.documentElement,{
      subtree:true,childList:true,attributes:true,characterData:true,
      attributeFilter:[
        'data-message-id','data-message-author-role','data-turn','data-turn-id','data-turn-key',
        'data-content-search-turn-key','data-content-search-unit-key','data-chatgpt-search-message-ids',
        'data-chatgpt-selection-message-id','data-testid','aria-label','class'
      ]
    });
    monitors.set(command.id,stop);
    void check();
    return true;
  }

  async function handleSend(command) {
    if (!command || typeof command.id!=='string' || inFlight.has(command.id)) return;
    if (DOM.conversationId()!==command.conversation_id) return;
    inFlight.add(command.id);
    let prepared=null;
    try {
      prepared=await DOM.prepareSend(command);
      if (!prepared.ok) {
        await post(command,'pre_send_failed',{error:prepared.error || 'prepare_failed'});
        return;
      }

      const claimed=await post(command,'composer_claimed',{rebound:prepared.rebound===true});
      if (!claimed?.ok || !prepared.draft.current()) return;

      const authorized=await sendRuntime({
        type:'pcbridge.m1.authorize',
        command_id:command.id,
        conversation_id:command.conversation_id,
        document_epoch:documentEpoch,
        payload_hash:command.payload_hash
      });
      if (!authorized?.ok) return;

      if (!prepared.draft.current()) {
        await post(command,'ambiguous',{error:'lease_lost_after_dispatch_intent'});
        return;
      }

      const result=await DOM.dispatchPrepared(command,prepared);
      await post(command,result.status==='delivered' ? 'delivered' : 'ambiguous',{
        error:result.error || '',
        user_message_id:result.user_message_id || ''
      });
    } finally {
      try { prepared?.draft?.dispose(); } catch {}
      inFlight.delete(command.id);
    }
  }

  chrome.runtime.onMessage.addListener((message,sender,sendResponse) => {
    const command=message?.command;
    if (!command || DOM.conversationId()!==command.conversation_id) {
      if (message?.type?.startsWith?.('pcbridge.m1.'))
        sendResponse({accepted:false,error:'target_mismatch',document_epoch:documentEpoch});
      return;
    }

    if (message.type==='pcbridge.m1.send') {
      void handleSend(command);
      sendResponse({accepted:true,document_epoch:documentEpoch});
      return true;
    }
    if (message.type==='pcbridge.m1.gate') {
      const accepted=startResponseMonitor(command,'gate');
      sendResponse({accepted,document_epoch:documentEpoch});
      return true;
    }
    if (message.type==='pcbridge.m1.observe') {
      const accepted=startResponseMonitor(command,'observe');
      sendResponse({accepted,document_epoch:documentEpoch});
      return true;
    }
  });

  void sendRuntime({type:'pcbridge.m1.page',page:{
    conversation_id:DOM.conversationId(),document_epoch:documentEpoch,ready:DOM.composerReady()
  }});
})();
