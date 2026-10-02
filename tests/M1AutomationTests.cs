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
  store.Advance(run.run_id,run.generation,h.message_id,"USER_RECEIPT_CONFIRMED","","");
  store.Advance(run.run_id,run.generation,h.message_id,"RESPONSE_BINDING","turn-2","");
  store.Advance(run.run_id,run.generation,h.message_id,"TURN_RUNNING","","");
  store.Advance(run.run_id,run.generation,h.message_id,"TERMINAL_OBSERVED","","");
  var terminal=store.Advance(run.run_id,run.generation,h.message_id,"TERMINAL_CONFIRMED","","");
  Check(terminal.response_turn_id=="turn-2" && terminal.terminal_utc.Length>0,"response lease reaches terminal");
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
