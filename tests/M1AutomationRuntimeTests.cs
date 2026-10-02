using System;
using System.IO;

static class M1AutomationRuntimeTests {
 static int passed;
 static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed++;}
 static void Expect(Action action,string fragment,string name) {
  bool hit=false;
  try{action();}catch(Exception e){hit=e.Message.IndexOf(fragment,StringComparison.OrdinalIgnoreCase)>=0;}
  Check(hit,name);
 }

 public static int Main(string[] args) {
  string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"M1RuntimeTestArtifacts","run-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(root);
  try {
   string extension=Path.Combine(root,"provider","chatgpt-extension");
   Directory.CreateDirectory(extension);
   File.WriteAllText(Path.Combine(extension,"manifest.json"),"{\"manifest_version\":3,\"name\":\"PCBridge M1 ChatGPT Companion\"}");
   string browser=Path.Combine(root,"fake-chrome.exe");
   File.WriteAllBytes(browser,new byte[]{77,90});

   const string session="anonymous-openai-session-runtime-123";
   const string conversation="12345678-abcd-4abc-8abc-123456789012";

   using(var runtime=new M1AutomationRuntime(root,browser,extension)) {
    var status=runtime.Start();
    Check(status.started && status.broker_port>0,"runtime starts loopback broker");
    Check(status.browser_runtime_available && status.companion_ready,"runtime sees isolated provider prerequisites");

    var first=runtime.Handoff(session,"work","next","verified","none");
    Check(first.state=="BINDING_REQUIRED" && first.binding_id.Length==32,"unbound ChatGPT session fails closed into binding request");
    Check(runtime.Automation.Snapshot().runs.Count==0,"unbound session creates no runnable automation");

    var candidate=runtime.Bindings.ProposeCandidate(first.binding_id,conversation,"document-1");
    Check(candidate.state=="candidate","browser candidate remains pending human confirmation");
    var bound=runtime.ConfirmBindingLocal(first.binding_id,conversation,"chatgpt-default");
    Check(bound.state=="bound","local confirmation binds exact conversation");

    var queued=runtime.Handoff(session,"work","next","verified","none");
    Check(queued.state=="HANDOFF_QUEUED" && queued.message_id.Length==32,"bound session queues handoff");
    var run=runtime.ActiveRun(session);
    Check(run!=null && run.client_session_key==M1SessionBindingStore.SessionKey(session),"run is correlated by anonymized session hash");
    Check(run.target.conversation_id==conversation,"run target comes only from confirmed binding");

    var duplicate=runtime.Handoff(session,"work","next","verified","none");
    Check(duplicate.existing && duplicate.message_id==queued.message_id,"repeated tool call is idempotent");

    // Restart runtime: durable handoff should be republished, not duplicated.
    string runId=run.run_id;
    long generation=run.generation;
    runtime.Dispose();
    using(var reopened=new M1AutomationRuntime(root,browser,extension)) {
     var recovered=reopened.Start();
     Check(recovered.recoverable_published>=1,"restart republishes recoverable source gate");
     var sameRun=reopened.ActiveRun(session);
     Check(sameRun!=null && sameRun.run_id==runId && sameRun.generation==generation,"restart retains same run ownership");

     Expect(delegate{reopened.Complete("different-session-123456","verified");},"active_automation_run_not_found","different ChatGPT session cannot complete another run");
    }
   }

   Console.WriteLine("M1 automation runtime tests PASS: "+passed);
   Console.WriteLine("state_root="+root);
   return 0;
  } catch(Exception e) {
   Console.Error.WriteLine(e.ToString());return 1;
  }
 }
}
