using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

public sealed class AutomationTarget {
 public string provider="";
 public string provider_profile_id="";
 public string conversation_id="";
}

public sealed class AutomationHandoff {
 public string message_id="";
 public int seq;
 public long generation;
 public string payload_hash="";
 public string payload="";
 public string summary="";
 public string next_action="";
 public string verification="";
 public string blocking_state="none";
 public string state="HANDOFF_COMMITTED";
 public string created_utc="";
 public string updated_utc="";
 public string send_authorized_utc="";
 public string dispatched_utc="";
 public string receipt_utc="";
 public string provider_user_message_id="";
 public string provider_document_epoch="";
 public string source_user_message_id="";
 public string source_response_turn_id="";
 public string response_turn_id="";
 public string terminal_utc="";
 public int attempt;
 public string next_attempt_utc="";
 public string error_class="";
}

public sealed class AutomationRun {
 public string run_id="";
 public string client_session_key="";
 public AutomationTarget target=new AutomationTarget();
 public long generation=1;
 public int seq;
 public string status="RUNNING";
 public string owner_token="";
 public string predecessor_message_id="";
 public List<AutomationHandoff> handoffs=new List<AutomationHandoff>();
 public string attention="";
 public string created_utc="";
 public string updated_utc="";
 public string completed_utc="";
}

public sealed class AutomationStateV1 {
 public int schema_version=1;
 public List<AutomationRun> runs=new List<AutomationRun>();
}

public sealed class AutomationHandoffResult {
 public string run_id="";
 public long generation;
 public int seq;
 public string message_id="";
 public string payload_hash="";
 public string state="";
 public bool existing;
 public bool blocked;
 public string blocking_state="";
}

public sealed class AutomationCompleteResult {
 public string run_id="";
 public long generation;
 public string status="";
 public bool existing;
}

public sealed class AutomationStateStore {
 readonly string root;
 readonly string path;
 readonly string mutexName;
 static readonly Regex IdRx=new Regex(@"\A[A-Za-z0-9_-]{8,160}\z",RegexOptions.CultureInvariant);

 public AutomationStateStore(string rootPath) {
  if(String.IsNullOrWhiteSpace(rootPath))throw new ArgumentException("rootPath");
  root=Path.GetFullPath(rootPath);
  Directory.CreateDirectory(root);
  path=Path.Combine(root,"automation-state.dpapi");
  mutexName="Local\\PCBridge-M1-"+DigestText(root).Substring(0,24);
 }

 public string StatePath { get { return path; } }

 public AutomationRun CreateRun(AutomationTarget target) {
  return CreateRun(target,"");
 }

 public AutomationRun CreateRun(AutomationTarget target,string clientSessionKey) {
  return Locked<AutomationRun>(delegate {
   ValidateTarget(target);
   string session=NormalizeSessionKey(clientSessionKey);
   var state=LoadUnsafe();
   if(session.Length>0) {
    var existing=state.runs.Find(delegate(AutomationRun candidate) {
     return candidate.client_session_key==session && candidate.status!="COMPLETED" &&
      candidate.status!="CANCELLED" && candidate.status!="FAILED_TERMINAL";
    });
    if(existing!=null) {
     if(existing.target==null ||
        !String.Equals(existing.target.provider,target.provider,StringComparison.OrdinalIgnoreCase) ||
        existing.target.provider_profile_id!=target.provider_profile_id ||
        existing.target.conversation_id!=target.conversation_id)
      throw new InvalidOperationException("client_session_target_conflict");
     return CloneRun(existing);
    }
   }
   var run=new AutomationRun();
   run.run_id=Guid.NewGuid().ToString("N");
   run.client_session_key=session;
   run.target=CloneTarget(target);
   run.generation=1;
   run.seq=0;
   run.status="RUNNING";
   run.owner_token=Guid.NewGuid().ToString("N");
   run.created_utc=Utc();
   run.updated_utc=run.created_utc;
   state.runs.Add(run);
   SaveUnsafe(state);
   return CloneRun(run);
  });
 }

 public AutomationRun GetRun(string runId) {
  return Locked<AutomationRun>(delegate {
   var state=LoadUnsafe();
   return CloneRun(FindRun(state,runId));
  });
 }

 public AutomationRun GetActiveRunByClientSession(string clientSessionKey) {
  return Locked<AutomationRun>(delegate {
   string session=NormalizeSessionKey(clientSessionKey);
   if(session.Length==0)throw new ArgumentException("client_session_key required");
   var state=LoadUnsafe();
   for(int i=state.runs.Count-1;i>=0;i--) {
    var run=state.runs[i];
    if(run.client_session_key!=session)continue;
    if(run.status=="COMPLETED"||run.status=="CANCELLED"||run.status=="FAILED_TERMINAL")continue;
    return CloneRun(run);
   }
   return null;
  });
 }

 public AutomationRun Takeover(string runId,long expectedGeneration) {
  return Locked<AutomationRun>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,expectedGeneration);
   var active=ActiveHandoff(run);
   if(active!=null && IsUncertainOrDispatched(active.state))
    throw new InvalidOperationException("handoff_uncertain_or_dispatched: reconcile before ownership takeover");
   if(active!=null && !IsTerminalHandoff(active.state)) {
    active.state="CANCELLED";
    active.error_class="ownership_takeover";
    active.updated_utc=Utc();
   }
   run.generation++;
   run.owner_token=Guid.NewGuid().ToString("N");
   run.status="RUNNING";
   run.updated_utc=Utc();
   SaveUnsafe(state);
   return CloneRun(run);
  });
 }

 public AutomationHandoffResult QueueHandoff(
  string runId,long generation,string summary,string nextAction,string verification,string blockingState) {
  return Locked<AutomationHandoffResult>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,generation);
   if(run.status=="COMPLETED")throw new InvalidOperationException("run_completed");
   if(run.status=="CANCELLED")throw new InvalidOperationException("run_cancelled");

   string block=NormalizeBlock(blockingState);
   if(block!="none") {
    var activeBlocked=ActiveHandoff(run);
    if(activeBlocked!=null && !IsTerminalHandoff(activeBlocked.state))
     throw new InvalidOperationException("active_handoff_exists");
    run.status=BlockStatus(block);
    run.attention=block;
    run.updated_utc=Utc();
    SaveUnsafe(state);
    return new AutomationHandoffResult {
     run_id=run.run_id,generation=run.generation,seq=run.seq,state=run.status,
     existing=false,blocked=true,blocking_state=block
    };
   }

   string cleanSummary=Required(summary,"summary",4000);
   string cleanNext=Required(nextAction,"next_action",4000);
   string cleanVerification=Required(verification,"verification",4000);
   string semantic=CanonicalFields(run.run_id,Convert.ToString(run.generation),cleanSummary,cleanNext,cleanVerification);
   string hash=DigestText(semantic);
   var active=ActiveHandoff(run);
   if(active!=null && !IsTerminalHandoff(active.state)) {
    if(active.payload_hash!=hash)throw new InvalidOperationException("pending_handoff_conflict");
    return Result(run,active,true);
   }

   var h=new AutomationHandoff();
   h.message_id=Guid.NewGuid().ToString("N");
   h.seq=run.seq+1;
   h.generation=run.generation;
   h.payload_hash=hash;
   h.summary=cleanSummary;
   h.next_action=cleanNext;
   h.verification=cleanVerification;
   h.blocking_state="none";
   h.state="HANDOFF_COMMITTED";
   h.created_utc=Utc();
   h.updated_utc=h.created_utc;
   h.payload=BuildPayload(run,h);
   run.seq=h.seq;
   run.predecessor_message_id=h.message_id;
   run.handoffs.Add(h);
   run.status="HANDOFF_PENDING";
   run.attention="";
   run.updated_utc=Utc();
   SaveUnsafe(state);
   return Result(run,h,false);
  });
 }

 public AutomationCompleteResult Complete(string runId,long generation,string verification) {
  return Locked<AutomationCompleteResult>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,generation);
   Required(verification,"verification",4000);
   if(run.status=="COMPLETED")
    return new AutomationCompleteResult{run_id=run.run_id,generation=run.generation,status=run.status,existing=true};
   var active=ActiveHandoff(run);
   if(active!=null && !IsTerminalHandoff(active.state)) {
    if(IsUncertainOrDispatched(active.state))
     throw new InvalidOperationException("cannot_complete_with_uncertain_delivery");
    active.state="CANCELLED";
    active.error_class="run_completed_before_send";
    active.updated_utc=Utc();
   }
   run.status="COMPLETED";
   run.attention="";
   run.completed_utc=Utc();
   run.updated_utc=run.completed_utc;
   SaveUnsafe(state);
   return new AutomationCompleteResult{run_id=run.run_id,generation=run.generation,status=run.status,existing=false};
  });
 }

 public AutomationHandoff BindSourceTurn(
  string runId,long generation,string messageId,string sourceUserMessageId,string sourceResponseTurnId,string documentEpoch) {
  return Locked<AutomationHandoff>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,generation);
   var h=FindHandoff(run,messageId);
   if(h.state!="HANDOFF_COMMITTED" && h.state!="WAIT_CURRENT_TURN_END")
    throw new InvalidOperationException("source_turn_bind_wrong_state");
   string user=ProviderId(sourceUserMessageId,"source_user_message_id");
   string turn=ProviderId(sourceResponseTurnId,"source_response_turn_id");
   string epoch=ProviderId(documentEpoch,"provider_document_epoch");
   if(h.source_user_message_id.Length>0 && h.source_user_message_id!=user)
    throw new InvalidOperationException("source_user_message_conflict");
   if(h.source_response_turn_id.Length>0 && h.source_response_turn_id!=turn)
    throw new InvalidOperationException("source_response_turn_conflict");
   h.source_user_message_id=user;
   h.source_response_turn_id=turn;
   h.provider_document_epoch=epoch;
   if(h.state=="HANDOFF_COMMITTED")h.state="WAIT_CURRENT_TURN_END";
   h.updated_utc=Utc();
   run.status="HANDOFF_PENDING";
   run.updated_utc=h.updated_utc;
   SaveUnsafe(state);
   return CloneHandoff(h);
  });
 }

 public AutomationHandoff ConfirmProviderReceipt(
  string runId,long generation,string messageId,string providerUserMessageId,string documentEpoch) {
  return Locked<AutomationHandoff>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,generation);
   var h=FindHandoff(run,messageId);
   if(h.state!="SEND_DISPATCHED" && h.state!="USER_RECEIPT_CONFIRMED")
    throw new InvalidOperationException("provider_receipt_wrong_state");
   string user=ProviderId(providerUserMessageId,"provider_user_message_id");
   string epoch=ProviderId(documentEpoch,"provider_document_epoch");
   if(h.provider_user_message_id.Length>0 && h.provider_user_message_id!=user)
    throw new InvalidOperationException("provider_user_message_conflict");
   h.provider_user_message_id=user;
   h.provider_document_epoch=epoch;
   if(h.state=="SEND_DISPATCHED") {
    h.state="USER_RECEIPT_CONFIRMED";
    h.receipt_utc=Utc();
   }
   h.updated_utc=Utc();
   run.status="TURN_RUNNING";
   run.updated_utc=h.updated_utc;
   SaveUnsafe(state);
   return CloneHandoff(h);
  });
 }

 public AutomationHandoff BindResponseTurn(
  string runId,long generation,string messageId,string providerUserMessageId,string responseTurnId,string documentEpoch) {
  return Locked<AutomationHandoff>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,generation);
   var h=FindHandoff(run,messageId);
   string user=ProviderId(providerUserMessageId,"provider_user_message_id");
   string turn=ProviderId(responseTurnId,"response_turn_id");
   string epoch=ProviderId(documentEpoch,"provider_document_epoch");
   if(h.provider_user_message_id.Length==0 || h.provider_user_message_id!=user)
    throw new InvalidOperationException("response_question_not_owned");
   if(h.provider_document_epoch.Length>0 && h.provider_document_epoch!=epoch)
    throw new InvalidOperationException("response_document_epoch_mismatch");
   if(h.response_turn_id.Length>0 && h.response_turn_id!=turn)
    throw new InvalidOperationException("response_turn_conflict");
   if(h.state=="USER_RECEIPT_CONFIRMED")h.state="RESPONSE_BINDING";
   if(h.state!="RESPONSE_BINDING" && h.state!="TURN_RUNNING" && h.state!="TERMINAL_OBSERVED")
    throw new InvalidOperationException("response_turn_bind_wrong_state");
   h.response_turn_id=turn;
   h.provider_document_epoch=epoch;
   if(h.state=="RESPONSE_BINDING")h.state="TURN_RUNNING";
   h.updated_utc=Utc();
   run.status="TURN_RUNNING";
   run.updated_utc=h.updated_utc;
   SaveUnsafe(state);
   return CloneHandoff(h);
  });
 }

 public AutomationHandoff MarkSourceTerminal(
  string runId,long generation,string messageId,string sourceUserMessageId,string sourceResponseTurnId,string documentEpoch) {
  return Locked<AutomationHandoff>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,generation);
   var h=FindHandoff(run,messageId);
   if(h.state!="WAIT_CURRENT_TURN_END")throw new InvalidOperationException("source_terminal_wrong_state");
   if(h.source_user_message_id!=ProviderId(sourceUserMessageId,"source_user_message_id") ||
      h.source_response_turn_id!=ProviderId(sourceResponseTurnId,"source_response_turn_id"))
    throw new InvalidOperationException("source_terminal_identity_mismatch");
   if(h.provider_document_epoch!=ProviderId(documentEpoch,"provider_document_epoch"))
    throw new InvalidOperationException("source_terminal_epoch_mismatch");
   h.state="TARGET_READY";
   h.updated_utc=Utc();
   run.status="HANDOFF_PENDING";
   run.updated_utc=h.updated_utc;
   SaveUnsafe(state);
   return CloneHandoff(h);
  });
 }

 public AutomationHandoff MarkResponseEvidence(
  string runId,long generation,string messageId,string responseTurnId,string documentEpoch,string evidenceState,string errorClass) {
  return Locked<AutomationHandoff>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,generation);
   var h=FindHandoff(run,messageId);
   string turn=ProviderId(responseTurnId,"response_turn_id");
   string epoch=ProviderId(documentEpoch,"provider_document_epoch");
   if(h.response_turn_id.Length==0 || h.response_turn_id!=turn)
    throw new InvalidOperationException("response_evidence_turn_mismatch");
   if(h.provider_document_epoch.Length>0 && h.provider_document_epoch!=epoch)
    throw new InvalidOperationException("response_evidence_epoch_mismatch");
   string target=(evidenceState??"").Trim().ToUpperInvariant();
   if(target=="TURN_RUNNING") {
    if(h.state=="TERMINAL_OBSERVED")h.state="TURN_RUNNING";
    else if(h.state!="TURN_RUNNING")throw new InvalidOperationException("response_running_wrong_state");
   } else if(target=="TERMINAL_OBSERVED") {
    if(h.state!="TURN_RUNNING")throw new InvalidOperationException("terminal_observed_wrong_state");
    h.state="TERMINAL_OBSERVED";
   } else if(target=="TERMINAL_CONFIRMED") {
    if(h.state!="TERMINAL_OBSERVED")throw new InvalidOperationException("terminal_confirmed_wrong_state");
    h.state="TERMINAL_CONFIRMED";
    h.terminal_utc=Utc();
   } else throw new ArgumentException("evidence_state invalid");
   if(!String.IsNullOrWhiteSpace(errorClass))h.error_class=errorClass.Trim();
   h.updated_utc=Utc();
   run.status=RunStatusForHandoff(h.state);
   run.updated_utc=h.updated_utc;
   SaveUnsafe(state);
   return CloneHandoff(h);
  });
 }

 public AutomationHandoff FailTerminal(
  string runId,long generation,string messageId,string reason) {
  return Locked<AutomationHandoff>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,generation);
   var h=FindHandoff(run,messageId);
   if(IsTerminalHandoff(h.state))return CloneHandoff(h);
   h.state="FAILED_TERMINAL";
   h.error_class=Required(reason,"reason",1000);
   h.updated_utc=Utc();
   run.status="WAITING_HUMAN";
   run.attention=h.error_class;
   run.updated_utc=h.updated_utc;
   SaveUnsafe(state);
   return CloneHandoff(h);
  });
 }

 public AutomationHandoff CancelForHuman(
  string runId,long generation,string messageId,string reason) {
  return Locked<AutomationHandoff>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,generation);
   var h=FindHandoff(run,messageId);
   if(IsUncertainOrDispatched(h.state))
    throw new InvalidOperationException("cannot_cancel_after_dispatch");
   if(IsTerminalHandoff(h.state))return CloneHandoff(h);
   h.state="CANCELLED";
   h.error_class=Required(reason,"reason",1000);
   h.updated_utc=Utc();
   run.status="WAITING_HUMAN";
   run.attention=h.error_class;
   run.updated_utc=h.updated_utc;
   SaveUnsafe(state);
   return CloneHandoff(h);
  });
 }

 public AutomationHandoff Advance(
  string runId,long generation,string messageId,string nextState,string responseTurnId,string errorClass) {
  return Locked<AutomationHandoff>(delegate {
   var state=LoadUnsafe();
   var run=FindRun(state,runId);
   RequireGeneration(run,generation);
   var h=FindHandoff(run,messageId);
   string target=(nextState??"").Trim().ToUpperInvariant();
   if(!CanTransition(h.state,target))throw new InvalidOperationException("invalid_handoff_transition: "+h.state+" -> "+target);
   h.state=target;
   h.updated_utc=Utc();
   if(target=="SEND_AUTHORIZED")h.send_authorized_utc=h.updated_utc;
   if(target=="SEND_DISPATCHED") { h.dispatched_utc=h.updated_utc;h.attempt++; }
   if(target=="USER_RECEIPT_CONFIRMED")h.receipt_utc=h.updated_utc;
   if(target=="RESPONSE_BINDING" && !String.IsNullOrWhiteSpace(responseTurnId))h.response_turn_id=responseTurnId.Trim();
   if(target=="TERMINAL_CONFIRMED")h.terminal_utc=h.updated_utc;
   if(!String.IsNullOrWhiteSpace(errorClass))h.error_class=errorClass.Trim();
   run.status=RunStatusForHandoff(target);
   run.updated_utc=Utc();
   SaveUnsafe(state);
   return CloneHandoff(h);
  });
 }

 public bool CanAutoDispatch(string runId,long generation,string messageId) {
  return Locked<bool>(delegate {
   var run=FindRun(LoadUnsafe(),runId);
   RequireGeneration(run,generation);
   var h=FindHandoff(run,messageId);
   return h.state=="HANDOFF_COMMITTED" || h.state=="WAIT_CURRENT_TURN_END" ||
    h.state=="TARGET_READY" || h.state=="COMPOSER_CLAIMED" || h.state=="PRE_SEND_RETRY" ||
    h.state=="RATE_LIMITED" || h.state=="LOAD_RECOVERY";
  });
 }

 public AutomationStateV1 Snapshot() {
  return Locked<AutomationStateV1>(delegate { return CloneState(LoadUnsafe()); });
 }

 T Locked<T>(Func<T> work) {
  using(var mutex=new Mutex(false,mutexName)) {
   bool entered=false;
   try {
    try { entered=mutex.WaitOne(TimeSpan.FromSeconds(20)); }
    catch(AbandonedMutexException) { entered=true; }
    if(!entered)throw new TimeoutException("automation_state_lock_timeout");
    return work();
   } finally { if(entered)mutex.ReleaseMutex(); }
  }
 }

 AutomationStateV1 LoadUnsafe() {
  if(!File.Exists(path))return new AutomationStateV1();
  byte[] sealedBytes=File.ReadAllBytes(path);
  byte[] plain=null;
  try {
   plain=ProtectedData.Unprotect(sealedBytes,null,DataProtectionScope.CurrentUser);
   var state=Json().Deserialize<AutomationStateV1>(Encoding.UTF8.GetString(plain));
   if(state==null || state.schema_version!=1 || state.runs==null)throw new InvalidDataException("automation_state_schema_invalid");
   return state;
  } catch(CryptographicException) {
   throw new InvalidDataException("automation_state_unreadable");
  } finally {
   if(plain!=null)Array.Clear(plain,0,plain.Length);
   Array.Clear(sealedBytes,0,sealedBytes.Length);
  }
 }

 void SaveUnsafe(AutomationStateV1 state) {
  byte[] plain=Encoding.UTF8.GetBytes(Json().Serialize(state));
  byte[] sealedBytes=null;
  try {
   sealedBytes=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);
   Atomic(path,sealedBytes);
  } finally {
   Array.Clear(plain,0,plain.Length);
   if(sealedBytes!=null)Array.Clear(sealedBytes,0,sealedBytes.Length);
  }
 }

 static void Atomic(string file,byte[] bytes) {
  string temp=file+".new";
  using(var f=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {
   f.Write(bytes,0,bytes.Length);
   f.Flush(true);
  }
  if(File.Exists(file))File.Replace(temp,file,null);else File.Move(temp,file);
 }

 static JavaScriptSerializer Json() {
  return new JavaScriptSerializer{MaxJsonLength=4000000,RecursionLimit=128};
 }

 static void ValidateTarget(AutomationTarget t) {
  if(t==null)throw new ArgumentNullException("target");
  if(!String.Equals(t.provider,"chatgpt",StringComparison.OrdinalIgnoreCase))
   throw new ArgumentException("M1 provider must be chatgpt");
  if(!IdRx.IsMatch(t.provider_profile_id??""))throw new ArgumentException("provider_profile_id invalid");
  if(!IdRx.IsMatch(t.conversation_id??""))throw new ArgumentException("conversation_id invalid");
 }

 static AutomationTarget CloneTarget(AutomationTarget t) {
  return new AutomationTarget{provider=t.provider,provider_profile_id=t.provider_profile_id,conversation_id=t.conversation_id};
 }

 static AutomationRun FindRun(AutomationStateV1 state,string runId) {
  string id=Required(runId,"run_id",80);
  var run=state.runs.Find(delegate(AutomationRun x){return x.run_id==id;});
  if(run==null)throw new InvalidOperationException("run_not_found");
  return run;
 }

 static AutomationHandoff FindHandoff(AutomationRun run,string messageId) {
  string id=Required(messageId,"message_id",80);
  var h=run.handoffs.Find(delegate(AutomationHandoff x){return x.message_id==id;});
  if(h==null)throw new InvalidOperationException("handoff_not_found");
  return h;
 }

 static AutomationHandoff ActiveHandoff(AutomationRun run) {
  for(int i=run.handoffs.Count-1;i>=0;i--)if(!IsTerminalHandoff(run.handoffs[i].state))return run.handoffs[i];
  return null;
 }

 static void RequireGeneration(AutomationRun run,long generation) {
  if(run.generation!=generation)throw new InvalidOperationException("stale_generation");
 }

 static string NormalizeSessionKey(string value) {
  string v=(value??"").Trim().ToUpperInvariant();
  if(v.Length==0)return "";
  if(!Regex.IsMatch(v,@"\A[A-F0-9]{64}\z"))throw new ArgumentException("client_session_key invalid");
  return v;
 }

 static string ProviderId(string value,string name) {
  string v=(value??"").Trim();
  if(v.Length<1 || v.Length>200 || v.Any(Char.IsControl))throw new ArgumentException(name+" invalid");
  return v;
 }

 static string Required(string value,string name,int max) {
  string v=(value??"").Trim();
  if(v.Length==0 || v.Length>max)throw new ArgumentException(name+" required, max "+max);
  return v;
 }

 static string NormalizeBlock(string block) {
  string b=(block??"none").Trim().ToLowerInvariant();
  if(b=="" || b=="none")return "none";
  if(b=="hardlock"||b=="human"||b=="external")return b;
  throw new ArgumentException("blocking_state invalid");
 }

 static string BlockStatus(string block) {
  if(block=="hardlock")return "WAITING_HARDLOCK";
  if(block=="human")return "WAITING_HUMAN";
  return "WAITING_EXTERNAL";
 }

 static string BuildPayload(AutomationRun run,AutomationHandoff h) {
  var b=new StringBuilder();
  b.AppendLine("PCBRIDGE_AUTOMATION_HANDOFF");
  b.AppendLine("run_id="+run.run_id);
  b.AppendLine("generation="+run.generation);
  b.AppendLine("seq="+h.seq);
  b.AppendLine("message_id="+h.message_id);
  b.AppendLine("payload_hash="+h.payload_hash);
  b.AppendLine();
  b.AppendLine("Verified work:");
  b.AppendLine(h.summary);
  b.AppendLine();
  b.AppendLine("Next work:");
  b.AppendLine(h.next_action);
  b.AppendLine();
  b.AppendLine("Verification:");
  b.AppendLine(h.verification);
  b.AppendLine();
  b.Append("Continue the same requested task. Do not repeat already verified work.");
  return b.ToString();
 }

 static AutomationHandoffResult Result(AutomationRun run,AutomationHandoff h,bool existing) {
  return new AutomationHandoffResult {
   run_id=run.run_id,generation=run.generation,seq=h.seq,message_id=h.message_id,
   payload_hash=h.payload_hash,state=h.state,existing=existing,blocked=false,blocking_state="none"
  };
 }

 static bool IsTerminalHandoff(string s) {
  return s=="TERMINAL_CONFIRMED" || s=="CANCELLED" || s=="FAILED_TERMINAL";
 }

 static bool IsUncertainOrDispatched(string s) {
  return s=="SEND_DISPATCHED" || s=="AMBIGUOUS" || s=="USER_RECEIPT_CONFIRMED" ||
   s=="RESPONSE_BINDING" || s=="TURN_RUNNING" || s=="TERMINAL_OBSERVED";
 }

 static bool CanTransition(string from,string to) {
  if(from==to)return true;
  if(from=="HANDOFF_COMMITTED")return to=="WAIT_CURRENT_TURN_END"||to=="CANCELLED";
  if(from=="WAIT_CURRENT_TURN_END")return to=="TARGET_READY"||to=="CANCELLED"||to=="LOAD_RECOVERY";
  if(from=="TARGET_READY")return to=="COMPOSER_CLAIMED"||to=="PRE_SEND_RETRY"||to=="TARGET_MISMATCH"||to=="BLOCKED_AUTH"||to=="RATE_LIMITED"||to=="LOAD_RECOVERY";
  if(from=="COMPOSER_CLAIMED")return to=="SEND_AUTHORIZED"||to=="PRE_SEND_RETRY"||to=="TARGET_MISMATCH"||to=="LOAD_RECOVERY";
  if(from=="SEND_AUTHORIZED")return to=="SEND_DISPATCHED"||to=="PRE_SEND_RETRY"||to=="RATE_LIMITED"||to=="LOAD_RECOVERY";
  if(from=="SEND_DISPATCHED")return to=="USER_RECEIPT_CONFIRMED"||to=="AMBIGUOUS";
  if(from=="AMBIGUOUS")return to=="USER_RECEIPT_CONFIRMED"||to=="PRE_SEND_RETRY"||to=="FAILED_TERMINAL";
  if(from=="PRE_SEND_RETRY"||from=="RATE_LIMITED"||from=="LOAD_RECOVERY")return to=="TARGET_READY"||to=="BLOCKED_AUTH"||to=="TARGET_MISMATCH"||to=="FAILED_TERMINAL";
  if(from=="USER_RECEIPT_CONFIRMED")return to=="RESPONSE_BINDING";
  if(from=="RESPONSE_BINDING")return to=="TURN_RUNNING"||to=="TERMINAL_OBSERVED";
  if(from=="TURN_RUNNING")return to=="TERMINAL_OBSERVED";
  if(from=="TERMINAL_OBSERVED")return to=="TERMINAL_CONFIRMED"||to=="TURN_RUNNING";
  return false;
 }

 static string RunStatusForHandoff(string state) {
  if(state=="AMBIGUOUS")return "AMBIGUOUS";
  if(state=="BLOCKED_AUTH")return "BLOCKED_AUTH";
  if(state=="TARGET_MISMATCH")return "TARGET_MISMATCH";
  if(state=="FAILED_TERMINAL")return "FAILED_TERMINAL";
  if(state=="TERMINAL_CONFIRMED")return "RUNNING";
  if(state=="TURN_RUNNING"||state=="RESPONSE_BINDING"||state=="USER_RECEIPT_CONFIRMED")return "TURN_RUNNING";
  return "HANDOFF_PENDING";
 }

 static string Utc() { return DateTime.UtcNow.ToString("o"); }

 static string CanonicalFields(params string[] fields) {
  var b=new StringBuilder();
  foreach(string raw in fields) {
   string s=raw??"";
   byte[] bytes=Encoding.UTF8.GetBytes(s);
   b.Append(bytes.Length).Append(':').Append(s).Append('|');
  }
  return b.ToString();
 }

 static string DigestText(string value) {
  using(var sha=SHA256.Create()) {
   byte[] bytes=Encoding.UTF8.GetBytes(value??"");
   try { return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-",""); }
   finally { Array.Clear(bytes,0,bytes.Length); }
  }
 }

 static AutomationStateV1 CloneState(AutomationStateV1 s) {
  return Json().Deserialize<AutomationStateV1>(Json().Serialize(s));
 }
 static AutomationRun CloneRun(AutomationRun r) {
  return Json().Deserialize<AutomationRun>(Json().Serialize(r));
 }
 static AutomationHandoff CloneHandoff(AutomationHandoff h) {
  return Json().Deserialize<AutomationHandoff>(Json().Serialize(h));
 }
}
