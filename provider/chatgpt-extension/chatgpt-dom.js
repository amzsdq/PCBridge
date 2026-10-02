/*
 * PCBridge M1 ChatGPT DOM adapter.
 * Selectively derived from Chat On Steroids v2.1.25, commit
 * a1879601684712cbc3d6100ee4fe2dacbdf1b7b4, extension/chatgpt-dom.js (MIT).
 */
(() => {
  'use strict';
  const LEGACY_TURN = 'section[data-testid^="conversation-turn"]';
  const SHELL_TURN = '[data-app-shell-main-surface] [data-thread-find-target="conversation"] [data-turn-key]';
  const SEARCH_TURN = '[data-chatgpt-search-unit-key]';
  const TURN = `${LEGACY_TURN}, ${SHELL_TURN}, ${SEARCH_TURN}`;
  const SEND = 'button[data-testid="send-button"], form button[aria-label^="Send" i], form[data-chatgpt-composer] button[type="submit"]';
  const STOP = 'button[data-testid="stop-button"], button[data-testid="composer-stop-button"], button[aria-label="Stop streaming"], button[aria-label="Stop generating"], button[aria-label="Stop answering"]';
  const STOP_SQUARE = /^\s*M4\.5 5\.75/;
  const safe = (fn, fallback) => { try { const value=fn(); return value==null ? fallback : value; } catch { return fallback; } };
  const compact = value => String(value ?? '').replace(/\s+/g, '');
  const normalized = value => String(value ?? '').replace(/\r\n?/g, '\n').trim();

  function conversationFromPath(pathname) {
    const match = /^\/(?:g\/[^/]+\/)?c\/([0-9a-f-]{8,64})(?:\/|$)/i.exec(String(pathname || ''));
    return match ? match[1] : null;
  }
  function conversationId() { return safe(() => conversationFromPath(location.pathname), null); }

  function onKeptPage(node) {
    return safe(() => {
      for (let page=node?.closest?.('[data-app-shell-page-surface]'); page;
        page=page.parentElement?.closest('[data-app-shell-page-surface]')) {
        if (getComputedStyle(page).display === 'none') return true;
      }
      return false;
    }, false);
  }

  function composer() {
    return safe(() => {
      const classic=[...document.querySelectorAll('#prompt-textarea')].filter(node => !onKeptPage(node));
      if (classic.length) return classic.length===1 ? classic[0] : null;
      const candidates=[...document.querySelectorAll(
        'form[data-chatgpt-composer] [contenteditable="true"][role="textbox"], ' +
        'form [data-composer-markdown][contenteditable="true"][role="textbox"]'
      )].filter(node => !node.closest(`${TURN},.markdown,[hidden],[aria-hidden="true"],[inert]`) && !onKeptPage(node));
      return candidates.length===1 ? candidates[0] : null;
    }, null);
  }
  function composerBox() { const box=composer(); return box?.closest?.('form') || box?.parentElement || null; }
  function renderedComposerNode(node) {
    if (!node?.isConnected || node.closest(`${TURN},[data-message-author-role],[hidden],[aria-hidden="true"],[inert]`)) return false;
    for (let parent=node; parent; parent=parent.parentElement) {
      const style=getComputedStyle(parent);
      if (style.display==='none' || style.visibility==='hidden' || style.visibility==='collapse') return false;
    }
    return true;
  }
  function nativeComposerControls(selector) {
    const form=composer()?.closest('form');
    return [...(form || document).querySelectorAll(selector)].filter(button =>
      renderedComposerNode(button) && (!form || button.closest('form')===form));
  }
  function primarySlotControls() {
    const form=composer()?.closest('form');
    if (!form) return [];
    return [...form.querySelectorAll('button[class*="size-token-button-composer"][class*="bg-composer-primary"]')]
      .filter(button => renderedComposerNode(button) && button.closest('form')===form);
  }
  function isStopSquare(button) {
    if (!button || button.hasAttribute('data-state')) return false;
    const paths=button.querySelectorAll('svg path');
    return paths.length===1 && STOP_SQUARE.test(paths[0].getAttribute('d') || '');
  }
  function stopControls() {
    const labelled=nativeComposerControls(STOP);
    return labelled.length ? labelled : primarySlotControls().filter(isStopSquare);
  }
  function generating() {
    return safe(() => {
      if (stopControls().length>0) return true;
      const primary=primarySlotControls();
      if (primary.length>0 && !primary.some(isStopSquare)) return false;
      return false;
    }, false);
  }
  function localeFreeSendControls() {
    const box=composer(), form=box?.closest('form');
    if (!form) return [];
    const drafted=(typeof box.innerText==='string' ? box.innerText : box.textContent || '').trim();
    if (!drafted || generating()) return [];
    return [...form.querySelectorAll('button[class*="size-token-button-composer"][class*="bg-composer-primary"]')]
      .filter(button => {
        if (!renderedComposerNode(button) || button.closest('form')!==form || button.hasAttribute('data-state')) return false;
        const paths=button.querySelectorAll('svg path');
        if (paths.length===1 && STOP_SQUARE.test(paths[0].getAttribute('d') || '')) return false;
        return paths.length>=1 && paths.length<=2;
      });
  }
  function sendButton() {
    return safe(() => {
      const labelled=nativeComposerControls(SEND);
      const buttons=labelled.length ? labelled : localeFreeSendControls();
      return buttons.length===1 ? buttons[0] : null;
    }, null);
  }
  function composerWritable() {
    const box=composer();
    return !!box?.isConnected && box.getAttribute('aria-disabled')!=='true' && box.getAttribute('contenteditable')!=='false';
  }
  function composerVisible() {
    const box=composer();
    return !!box && !box.closest('[hidden],[aria-hidden="true"],[inert]') && box.getClientRects().length>0;
  }
  function hasComposerAttachments() {
    return safe(() => {
      const root=composerBox();
      return !!root?.querySelector('[data-testid*="attachment"],[data-inline-file-uploading],[role="progressbar"],button[aria-label*="Remove" i]');
    }, false);
  }
  function composerReady() {
    const box=composer();
    return !!box && composerVisible() && composerWritable() && !generating() &&
      !hasComposerAttachments() && normalized(box.textContent)==='';
  }

  function messageIdOf(node) {
    if (!node) return null;
    const explicit=node.getAttribute?.('data-message-id');
    if (explicit) return explicit;
    const selected=node.getAttribute?.('data-chatgpt-selection-message-id') ||
      node.querySelector?.('[data-chatgpt-selection-message-id]')?.getAttribute?.('data-chatgpt-selection-message-id');
    if (selected) return selected;
    const listed=node.getAttribute?.('data-chatgpt-search-message-ids') || '';
    return listed.trim().split(/\s+/).find(Boolean) || null;
  }
  function userMessages() {
    return safe(() => {
      const out=[], seen=new Set();
      for (const node of [...document.querySelectorAll('[data-message-author-role="user"][data-message-id]')].filter(n => !onKeptPage(n))) {
        const id=messageIdOf(node); if (!id || seen.has(id)) continue;
        seen.add(id); out.push({id,text:normalized(node.textContent),node});
      }
      for (const slot of document.querySelectorAll(`${SHELL_TURN} [data-content-search-unit-key$=":user"]`)) {
        if (onKeptPage(slot)) continue;
        const owner=slot.closest('[data-turn-key]');
        if (!owner || slot.closest('[data-turn-key]')!==owner) continue;
        const id=messageIdOf(slot); if (!id || seen.has(id)) continue;
        seen.add(id);
        const bubble=slot.querySelector('[data-user-message-bubble]') || slot;
        out.push({id,text:normalized(bubble.textContent),node:slot});
      }
      return out;
    }, []);
  }

  function insertPrompt(value, failure=()=>undefined) {
    const box=composer();
    const reject=reason => { safe(() => failure(reason), undefined); return false; };
    try {
      if (!box || !composerWritable() || !composerVisible()) return reject('composer_missing_or_unwritable');
      if (normalized(box.textContent)!=='') return reject('existing_draft');
      box.focus();
      const selection=document.getSelection();
      if (!selection) return reject('selection_missing');
      selection.selectAllChildren(box);
      if (!box.isConnected || composer()!==box || document.activeElement!==box) return reject('editor_replaced');
      const paragraph=document.createElement('p');
      const host=box.matches('[data-composer-markdown]') && box.closest('form[data-chatgpt-composer]') ? document.createElement('span') : paragraph;
      if (host!==paragraph) { host.setAttribute('data-prompt-literal-paste',''); paragraph.append(host); }
      String(value).split('\n').forEach((line,index) => {
        if (index) host.append(document.createElement('br'));
        host.append(document.createTextNode(line));
      });
      if (!document.execCommand('insertHTML',false,paragraph.innerHTML)) return reject('native_edit_rejected');
      if (!box.isConnected || composer()!==box || compact(box.textContent)!==compact(value)) return reject('text_mismatch');
      return true;
    } catch { return reject('insertion_exception'); }
  }
  function clearPromptExact(value) {
    return safe(() => {
      const box=composer();
      if (!box || compact(box.textContent)!==compact(value)) return false;
      box.focus(); document.execCommand('selectAll',false); document.execCommand('delete',false);
      box.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'deleteContentBackward',data:null}));
      return normalized(box.textContent)==='';
    }, false);
  }
  function captureDraft(value, stillCurrent=()=>true) {
    let box=composer(), host=composerBox(), rebound=false, touched=false;
    const inserted=compact(box?.textContent);
    const events=['input','change','keydown','pointerdown','paste','drop'];
    const changed=event => { if (event.isTrusted) touched=true; };
    for (const event of events) host?.addEventListener(event,changed,true);
    const current=() => !touched && stillCurrent() && composer()===box && box?.isConnected &&
      compact(box.textContent)===inserted && !hasComposerAttachments();
    return {
      current,
      rebind() {
        if (rebound || touched || !stillCurrent() || current() || hasComposerAttachments()) return false;
        const next=composer(), nextHost=next?.closest?.('form') || next?.parentElement;
        if (!next?.isConnected || next===box || compact(next.textContent)!==inserted) return false;
        for (const event of events) host?.removeEventListener(event,changed,true);
        box=next; host=nextHost; rebound=true;
        for (const event of events) host?.addEventListener(event,changed,true);
        return current();
      },
      clear() { return current() && clearPromptExact(value); },
      dispose() { for (const event of events) host?.removeEventListener(event,changed,true); }
    };
  }
  function waitFor(read,current,timeoutMs) {
    return new Promise(resolve => {
      let done=false;
      const finish=value => { if (done) return; done=true; observer.disconnect(); clearTimeout(timer); resolve(value); };
      const check=() => {
        if (!current()) return finish(null);
        let value=null; try { value=read(); } catch {}
        if (value) finish(value);
      };
      const observer=new MutationObserver(check);
      observer.observe(document.documentElement,{subtree:true,childList:true,attributes:true,characterData:true});
      const timer=setTimeout(() => finish(null),timeoutMs);
      check();
    });
  }

  async function prepareSend(command) {
    if (!command || conversationId()!==command.conversation_id) return {ok:false,error:'target_mismatch'};
    if (!composerReady()) return {ok:false,error:'composer_not_ready'};
    const beforeIds=new Set(userMessages().map(row => row.id));
    let failure='';
    if (!insertPrompt(command.text,reason => { failure=reason; })) return {ok:false,error:failure || 'insert_failed'};
    const sameTarget=() => conversationId()===command.conversation_id;
    const draft=captureDraft(command.text,sameTarget);
    let rebound=false;
    try {
      let button=await waitFor(() => draft.current() ? sendButton() : null,sameTarget,15000);
      if (!button && !draft.current() && !rebound && draft.rebind()) {
        rebound=true; button=await waitFor(() => draft.current() ? sendButton() : null,sameTarget,5000);
      }
      if (!button || !draft.current()) return {ok:false,error:'send_control_unavailable',before_ids:[...beforeIds]};
      return {ok:true,before_ids:[...beforeIds],draft,button,rebound};
    } catch {
      draft.dispose(); return {ok:false,error:'prepare_exception',before_ids:[...beforeIds]};
    }
  }

  async function dispatchPrepared(command,prepared) {
    const {draft,button}=prepared || {};
    if (!draft?.current() || conversationId()!==command.conversation_id) return {status:'not_sent',error:'lease_lost'};
    if (!button?.isConnected || button.disabled || button.getAttribute('aria-disabled')==='true') return {status:'not_sent',error:'send_disabled'};
    const beforeIds=new Set(prepared.before_ids || []);
    button.click();
    const receipt=await waitFor(() => {
      const candidates=userMessages().filter(row => !beforeIds.has(row.id));
      if (candidates.length!==1) return null;
      const row=candidates[0];
      return compact(row.text)===compact(command.text) ? row : null;
    }, () => conversationId()===command.conversation_id,20000);
    if (!receipt) return {status:'ambiguous',error:'receipt_unconfirmed'};
    return {status:'delivered',user_message_id:receipt.id,conversation_id:conversationId()};
  }

  globalThis.PCBridgeChatGPTDOM=Object.freeze({
    conversationFromPath,conversationId,composer,composerReady,composerVisible,composerWritable,
    generating,sendButton,userMessages,insertPrompt,captureDraft,prepareSend,dispatchPrepared,compact
  });
})();
