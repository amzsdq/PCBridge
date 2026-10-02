using System;
using System.IO;
using System.Text;

static class M1AutomationTests {
 static int passed;
 static string root;

 static void Check(bool ok,string name) {
  if(!ok)throw new Exception("FAIL: "+name);
  passed++;
 }

 static void ExpectError(Action action,string fragment,string name) {
  bool hit=false;
  try { action(); }
  catch(Exception e) { hit=e.Message.IndexOf(fragment,StringComparison.OrdinalIgnoreCase)>=0; }
  Check(hit,name);
 }

 static AutomationTarget Target(string suffix) {
  return new AutomationTarget {
   provider="chatgpt",
   provider_profile_id="pcbridgeprofile_"+suffix,
   conversation_id="12345678-abcd-4abc-8abc-"+suffix.PadRight(12,'0').Substring(0,12)
  };
 }

 static void TestCreatePersistAndIdempotentHandoff() {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("persist"));
  Check(run.generation==1 && run.seq==0 && run.status=="RUNNING","create run");

  var first=store.QueueHandoff(run.run_id,run.generation,"edited files","run tests","compile pass","none");
  Check(first.seq==1 && first.state=="HANDOFF_COMMITTED" && !first.existing,"first handoff committed");

  var same=store.QueueHandoff(run.run_id,run.generation,"edited files","run tests","compile pass","none");
  Check(same.existing && same.message_id==first.message_id && same.payload_hash==first.payload_hash,"duplicate handoff returns same record");

  var reopened=new AutomationStateStore(root);
  var restored=reopened.GetRun(run.run_id);
  Check(restored.seq==1 && restored.handoffs.Count==1 && restored.handoffs[0].message_id==first.message_id,"restart persistence");

  byte[] raw=File.ReadAllBytes(reopened.StatePath);
  string accidental=Encoding.UTF8.GetString(raw);
  Check(accidental.IndexOf(run.run_id,StringComparison.Ordinal)<0,"state is DPAPI protected");
 }

 static void TestConflictAndGenerationFence() {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("fence"));
  store.QueueHandoff(run.run_id,run.generation,"one","two","three","none");
  ExpectError(delegate {
   store.QueueHandoff(run.run_id,run.generation,"different","two","three","none");
  },"pending_handoff_conflict","conflicting pending handoff rejected");

  var clean=store.CreateRun(Target("takeover"));
  var newer=store.Takeover(clean.run_id,clean.generation);
  Check(newer.generation==2 && newer.owner_token!=clean.owner_token,"takeover increments fencing generation");
  ExpectError(delegate {
   store.QueueHandoff(clean.run_id,1,"old","must fail","stale owner","none");
  },"stale_generation","stale generation fenced");
 }

 static void TestCompletionAndBlocking() {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("complete"));
  var h=store.QueueHandoff(run.run_id,run.generation,"done turn","would continue","verified","none");
  var complete=store.Complete(run.run_id,run.generation,"whole request independently verified");
  Check(complete.status=="COMPLETED","run completed");
  var restored=store.GetRun(run.run_id);
  Check(restored.handoffs[0].state=="CANCELLED","unsent baton cancelled on completion");
  var again=store.Complete(run.run_id,run.generation,"whole request independently verified");
  Check(again.existing,"completion is idempotent");

  var blocked=store.CreateRun(Target("blocked"));
  var result=store.QueueHandoff(blocked.run_id,blocked.generation,"x","y","z","hardlock");
  Check(result.blocked && result.state=="WAITING_HARDLOCK","hardlock becomes wait state without baton");
  Check(store.GetRun(blocked.run_id).handoffs.Count==0,"blocking state does not create continue message");
 }

 static void TestAmbiguityFenceAndTransitions() {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("ambiguous"));
  var h=store.QueueHandoff(run.run_id,run.generation,"phase complete","continue next","verified","none");
  store.Advance(run.run_id,run.generation,h.message_id,"WAIT_CURRENT_TURN_END","","");
  store.Advance(run.run_id,run.generation,h.message_id,"TARGET_READY","","");
  store.Advance(run.run_id,run.generation,h.message_id,"COMPOSER_CLAIMED","","");
  store.Advance(run.run_id,run.generation,h.message_id,"SEND_AUTHORIZED","","");
  store.Advance(run.run_id,run.generation,h.message_id,"SEND_DISPATCHED","","");
  store.Advance(run.run_id,run.generation,h.message_id,"AMBIGUOUS","","receipt_missing");
  Check(!store.CanAutoDispatch(run.run_id,run.generation,h.message_id),"ambiguous delivery cannot auto-dispatch");
  ExpectError(delegate {
   store.Complete(run.run_id,run.generation,"not safe while send is ambiguous");
  },"cannot_complete_with_uncertain_delivery","cannot complete across ambiguous send");
  var lateReceipt=store.ConfirmProviderReceipt(run.run_id,run.generation,h.message_id,"late-user","doc-late");
  Check(lateReceipt.state=="USER_RECEIPT_CONFIRMED" && lateReceipt.provider_user_message_id=="late-user","ambiguous delivery can only recover through an exact provider receipt");
  store.Advance(run.run_id,run.generation,h.message_id,"RESPONSE_BINDING","turn-2","");
  store.Advance(run.run_id,run.generation,h.message_id,"TURN_RUNNING","","");
  store.Advance(run.run_id,run.generation,h.message_id,"TERMINAL_OBSERVED","","");
  var terminal=store.Advance(run.run_id,run.generation,h.message_id,"TERMINAL_CONFIRMED","","");
  Check(terminal.response_turn_id=="turn-2" && terminal.terminal_utc.Length>0,"response lease reaches terminal");
 }

 static void TestProviderOwnershipEvidence() {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("ownership"));
  var h=store.QueueHandoff(run.run_id,run.generation,"source work","continue","verified","none");

  var source=store.BindSourceTurn(run.run_id,run.generation,h.message_id,"source-user","source-turn","doc-one");
  Check(source.state=="WAIT_CURRENT_TURN_END" && source.source_response_turn_id=="source-turn","source response lease bound");
  var ready=store.MarkSourceTerminal(run.run_id,run.generation,h.message_id,"source-user","source-turn","doc-one");
  Check(ready.state=="TARGET_READY","exact source terminal opens handoff send");

  store.Advance(run.run_id,run.generation,h.message_id,"COMPOSER_CLAIMED","","");
  store.Advance(run.run_id,run.generation,h.message_id,"SEND_AUTHORIZED","","");
  store.Advance(run.run_id,run.generation,h.message_id,"SEND_DISPATCHED","","");
  var receipt=store.ConfirmProviderReceipt(run.run_id,run.generation,h.message_id,"handoff-user","doc-two");
  Check(receipt.state=="USER_RECEIPT_CONFIRMED" && receipt.provider_user_message_id=="handoff-user","exact native send receipt persisted");

  var response=store.BindResponseTurn(run.run_id,run.generation,h.message_id,"handoff-user","response-turn","doc-two");
  Check(response.state=="TURN_RUNNING" && response.response_turn_id=="response-turn","successor response lease bound");
  var observed=store.MarkResponseEvidence(run.run_id,run.generation,h.message_id,"response-turn","doc-two","TERMINAL_OBSERVED","");
  Check(observed.state=="TERMINAL_OBSERVED","provider terminal remains provisional");
  var reopened=store.MarkResponseEvidence(run.run_id,run.generation,h.message_id,"response-turn","doc-two","TURN_RUNNING","");
  Check(reopened.state=="TURN_RUNNING","new response activity revokes provisional terminal");
  store.MarkResponseEvidence(run.run_id,run.generation,h.message_id,"response-turn","doc-two","TERMINAL_OBSERVED","");
  var terminal=store.MarkResponseEvidence(run.run_id,run.generation,h.message_id,"response-turn","doc-two","TERMINAL_CONFIRMED","");
  Check(terminal.state=="TERMINAL_CONFIRMED" && terminal.terminal_utc.Length>0,"same exact response terminal confirmed");

  ExpectError(delegate {
   store.BindResponseTurn(run.run_id,run.generation,h.message_id,"different-user","response-turn","doc-two");
  },"response_question_not_owned","different native question cannot own response");
 }

 static void TestHumanSupersessionAndTerminalFailure() {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("humanstop"));
  var h=store.QueueHandoff(run.run_id,run.generation,"a","b","c","none");
  store.BindSourceTurn(run.run_id,run.generation,h.message_id,"source-user-2","source-turn-2","doc-x");
  var cancelled=store.CancelForHuman(run.run_id,run.generation,h.message_id,"newer user input");
  Check(cancelled.state=="CANCELLED" && store.GetRun(run.run_id).status=="WAITING_HUMAN","human supersession cancels only pre-send baton");

  var failedRun=store.CreateRun(Target("terminalfail"));
  var fh=store.QueueHandoff(failedRun.run_id,failedRun.generation,"a","b","c","none");
  store.BindSourceTurn(failedRun.run_id,failedRun.generation,fh.message_id,"src","src-turn","doc-f");
  store.MarkSourceTerminal(failedRun.run_id,failedRun.generation,fh.message_id,"src","src-turn","doc-f");
  store.Advance(failedRun.run_id,failedRun.generation,fh.message_id,"COMPOSER_CLAIMED","","");
  store.Advance(failedRun.run_id,failedRun.generation,fh.message_id,"SEND_AUTHORIZED","","");
  store.Advance(failedRun.run_id,failedRun.generation,fh.message_id,"SEND_DISPATCHED","","");
  var failed=store.FailTerminal(failedRun.run_id,failedRun.generation,fh.message_id,"response ownership conflict");
  Check(failed.state=="FAILED_TERMINAL" && store.GetRun(failedRun.run_id).status=="WAITING_HUMAN","post-dispatch response conflict fails closed");
 }

 static void TestDurableRecoveryScheduling() {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("recovery"));
  var h=store.QueueHandoff(run.run_id,run.generation,"a","b","c","none");
  store.Advance(run.run_id,run.generation,h.message_id,"WAIT_CURRENT_TURN_END","","");

  var waiting=store.ScheduleRecovery(run.run_id,run.generation,h.message_id,"LOAD_RECOVERY","page_load_failed",0);
  DateTime due;
  Check(waiting.state=="LOAD_RECOVERY" && waiting.recovery_attempt==1 &&
        waiting.recovery_return_state=="WAIT_CURRENT_TURN_END" &&
        DateTime.TryParse(waiting.next_attempt_utc,out due) && due.ToUniversalTime()>DateTime.UtcNow,
        "source-turn load recovery is durable and returns to the source gate");
  Check(!store.CanAutoDispatch(run.run_id,run.generation,h.message_id),"recovery cannot dispatch before durable due time");
  ExpectError(delegate {
   store.ActivateRecovery(run.run_id,run.generation,h.message_id,"doc-new");
  },"recovery_not_due","early recovery activation rejected");

  var rate=store.ScheduleRecovery(run.run_id,run.generation,h.message_id,"RATE_LIMITED","429",900);
  Check(rate.state=="RATE_LIMITED" && rate.recovery_attempt==2 &&
        DateTime.TryParse(rate.next_attempt_utc,out due) &&
        due.ToUniversalTime()>=DateTime.UtcNow.AddSeconds(890),
        "provider retry hint extends exponential rate-limit backoff");

  AutomationHandoff exhausted=rate;
  for(int i=0;i<7;i++)
   exhausted=store.ScheduleRecovery(run.run_id,run.generation,h.message_id,"RATE_LIMITED","still_limited",0);
  Check(exhausted.state=="FAILED_TERMINAL" && store.GetRun(run.run_id).status=="WAITING_HUMAN",
        "bounded recovery escalates after eight retry attempts instead of looping forever");
 }

 static void TestAuthBlockBeforeDispatch() {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("authblock"));
  var h=store.QueueHandoff(run.run_id,run.generation,"a","b","c","none");
  store.Advance(run.run_id,run.generation,h.message_id,"WAIT_CURRENT_TURN_END","","");
  store.Advance(run.run_id,run.generation,h.message_id,"TARGET_READY","","");
  var blocked=store.BlockAuth(run.run_id,run.generation,h.message_id,"sign-in required");
  var restored=store.GetRun(run.run_id);
  Check(blocked.state=="BLOCKED_AUTH" && restored.status=="BLOCKED_AUTH" &&
        restored.attention.IndexOf("sign-in",StringComparison.OrdinalIgnoreCase)>=0,
        "authentication requirement blocks safely before Send");
 }

 static void TestInvalidTargetsAndTransitions() {
  var store=new AutomationStateStore(root);
  ExpectError(delegate {
   store.CreateRun(new AutomationTarget{provider="claude",provider_profile_id="profile123",conversation_id="conversation123"});
  },"M1 provider","provider boundary enforced");

  var run=store.CreateRun(Target("transition"));
  var h=store.QueueHandoff(run.run_id,run.generation,"a","b","c","none");
  ExpectError(delegate {
   store.Advance(run.run_id,run.generation,h.message_id,"SEND_DISPATCHED","","");
  },"invalid_handoff_transition","invalid transition rejected");
 }

 public static int Main(string[] args) {
  try {
   root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"M1TestArtifacts","run-"+Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(root);
   TestCreatePersistAndIdempotentHandoff();
   TestConflictAndGenerationFence();
   TestCompletionAndBlocking();
   TestAmbiguityFenceAndTransitions();
   TestProviderOwnershipEvidence();
   TestHumanSupersessionAndTerminalFailure();
   TestDurableRecoveryScheduling();
   TestAuthBlockBeforeDispatch();
   TestInvalidTargetsAndTransitions();
   Console.WriteLine("M1 coordinator tests PASS: "+passed);
   Console.WriteLine("state_root="+root);
   return 0;
  } catch(Exception e) {
   Console.Error.WriteLine(e.ToString());
   return 1;
  }
 }
}
