using System;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Threading;

static class M1BrowserBrokerTests {
 static int passed;
 static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed++;}
 static string Json(object value){return new JavaScriptSerializer().Serialize(value);}
 static Tuple<int,string> Request(int port,string method,string path,string origin,string token,object body) {
  var req=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+port+path);
  req.Method=method;req.Proxy=null;req.Timeout=5000;req.ReadWriteTimeout=5000;
  req.Headers["Origin"]=origin;req.Headers["X-PCBridge-Protocol"]=M1BrowserBroker.Protocol;
  if(!String.IsNullOrEmpty(token))req.Headers["Authorization"]="Bearer "+token;
  if(body!=null){
   byte[] bytes=Encoding.UTF8.GetBytes(Json(body));
   req.ContentType="application/json";req.ContentLength=bytes.Length;
   using(var s=req.GetRequestStream())s.Write(bytes,0,bytes.Length);
  }
  try{
   using(var resp=(HttpWebResponse)req.GetResponse())
   using(var sr=new StreamReader(resp.GetResponseStream()))
    return Tuple.Create((int)resp.StatusCode,sr.ReadToEnd());
  } catch(WebException e) {
   using(var resp=(HttpWebResponse)e.Response)
   using(var sr=new StreamReader(resp.GetResponseStream()))
    return Tuple.Create((int)resp.StatusCode,sr.ReadToEnd());
  }
 }
 static Dictionary<string,object> Obj(string json){return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);}
 static Dictionary<string,object> Command(string json){
  var outer=Obj(json);object raw;
  if(!outer.TryGetValue("command",out raw)||raw==null)return null;
  return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Json(raw));
 }
 static string S(IDictionary<string,object> obj,string key){object v;return obj!=null&&obj.TryGetValue(key,out v)&&v!=null?Convert.ToString(v):"";}
 static AutomationTarget Target(string suffix){return new AutomationTarget{provider="chatgpt",provider_profile_id="profile_"+suffix,conversation_id="12345678-abcd-4abc-8abc-"+suffix.PadRight(12,'0').Substring(0,12)};}
 static string Pair(int port,string origin) {
  var pair=Request(port,"POST","/pair",origin,"",new{protocol=M1BrowserBroker.Protocol});
  Check(pair.Item1==200,"extension paired");
  string token=S(Obj(pair.Item2),"token");
  Check(token.Length>20,"pair token length");
  return token;
 }

 static void TestFullLifecycle(string root) {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("fullflow"));
  var h=store.QueueHandoff(run.run_id,run.generation,"phase one","phase two","verified","none");

  using(var broker=new M1BrowserBroker(store,0)) {
   broker.Start();
   Check(broker.Port>0,"broker bound ephemeral loopback port");
   const string origin="chrome-extension://abcdefghijklmnopabcdefghijklmnop";

   var hello=Request(broker.Port,"GET","/hello",origin,"",null);
   Check(hello.Item1==200 && hello.Item2.Contains(M1BrowserBroker.Protocol),"hello protocol");
   var badPair=Request(broker.Port,"POST","/pair","https://evil.example","",new{protocol=M1BrowserBroker.Protocol});
   Check(badPair.Item1==403,"web origin cannot pair");
   string token=Pair(broker.Port,origin);

   broker.Publish(run.run_id,run.generation,h.message_id);
   var gate=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(S(gate,"kind")=="gate_current_turn","first command gates current turn");
   string gateId=S(gate,"id");
   Check(gateId==h.message_id+":gate","gate command stage identity");

   var bound=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=gateId,kind="source_bound",conversation_id=run.target.conversation_id,
    document_epoch="doc-source",user_message_id="user-source-1",response_turn_id="turn-source-1"
   });
   Check(bound.Item1==200,"source turn bound");
   var state=store.GetRun(run.run_id).handoffs[0];
   Check(state.state=="WAIT_CURRENT_TURN_END" && state.source_user_message_id=="user-source-1","source ownership persisted");

   var ended=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=gateId,kind="source_terminal",conversation_id=run.target.conversation_id,
    document_epoch="doc-source",user_message_id="user-source-1",response_turn_id="turn-source-1"
   });
   Check(ended.Item1==200,"source terminal accepted");
   Check(store.GetRun(run.run_id).handoffs[0].state=="TARGET_READY","source terminal opens target");

   var send=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(S(send,"kind")=="send_handoff" && S(send,"id")==h.message_id+":send","send stage auto-published");
   string sendId=S(send,"id");

   var claim=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=sendId,kind="composer_claimed",conversation_id=run.target.conversation_id,
    document_epoch="doc-send",payload_hash=h.payload_hash,rebound=false
   });
   Check(claim.Item1==200 && store.GetRun(run.run_id).handoffs[0].state=="COMPOSER_CLAIMED","composer claim persisted");

   var auth=Request(broker.Port,"POST","/v1/authorize",origin,token,new{
    command_id=sendId,conversation_id=run.target.conversation_id,document_epoch="doc-send",payload_hash=h.payload_hash
   });
   Check(auth.Item1==200 && auth.Item2.Contains("dispatch_intent_durable"),"dispatch authorization durable");
   Check(store.GetRun(run.run_id).handoffs[0].state=="SEND_DISPATCHED","dispatch intent persisted before click");

   var delivered=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=sendId,kind="delivered",conversation_id=run.target.conversation_id,
    document_epoch="doc-send",payload_hash=h.payload_hash,user_message_id="user-handoff-1"
   });
   Check(delivered.Item1==200,"delivery receipt accepted");
   state=store.GetRun(run.run_id).handoffs[0];
   Check(state.state=="USER_RECEIPT_CONFIRMED" && state.provider_user_message_id=="user-handoff-1","provider receipt ownership persisted");

   var observe=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(S(observe,"kind")=="observe_response" && S(observe,"user_message_id")=="user-handoff-1","response observer auto-published");
   string observeId=S(observe,"id");

   var started=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=observeId,kind="response_started",conversation_id=run.target.conversation_id,
    document_epoch="doc-send",user_message_id="user-handoff-1",response_turn_id="turn-response-1"
   });
   Check(started.Item1==200,"response ownership bound");
   state=store.GetRun(run.run_id).handoffs[0];
   Check(state.state=="TURN_RUNNING" && state.response_turn_id=="turn-response-1","response turn persisted");

   var observed=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=observeId,kind="terminal_observed",conversation_id=run.target.conversation_id,
    document_epoch="doc-send",user_message_id="user-handoff-1",response_turn_id="turn-response-1"
   });
   Check(observed.Item1==200 && store.GetRun(run.run_id).handoffs[0].state=="TERMINAL_OBSERVED","terminal observation provisional");

   var resumed=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=observeId,kind="turn_running",conversation_id=run.target.conversation_id,
    document_epoch="doc-send",user_message_id="user-handoff-1",response_turn_id="turn-response-1"
   });
   Check(resumed.Item1==200 && store.GetRun(run.run_id).handoffs[0].state=="TURN_RUNNING","reopened response revokes provisional terminal");

   Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=observeId,kind="terminal_observed",conversation_id=run.target.conversation_id,
    document_epoch="doc-send",user_message_id="user-handoff-1",response_turn_id="turn-response-1"
   });
   var confirmed=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=observeId,kind="terminal_confirmed",conversation_id=run.target.conversation_id,
    document_epoch="doc-send",user_message_id="user-handoff-1",response_turn_id="turn-response-1"
   });
   state=store.GetRun(run.run_id).handoffs[0];
   Check(confirmed.Item1==200 && state.state=="TERMINAL_CONFIRMED" && state.terminal_utc.Length>0,"terminal confirmed for exact response");

   var after=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(after==null,"terminal command not reoffered");
  }
 }

 static void TestAmbiguityAndPreSend(string root) {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("ambiguity"));
  var h=store.QueueHandoff(run.run_id,run.generation,"a","b","c","none");
  store.BindSourceTurn(run.run_id,run.generation,h.message_id,"source-a","source-turn-a","doc-a");
  store.MarkSourceTerminal(run.run_id,run.generation,h.message_id,"source-a","source-turn-a","doc-a");

  using(var broker=new M1BrowserBroker(store,0)) {
   broker.Start();const string origin="chrome-extension://abcdefghijklmnopabcdefghijklmnop";
   string token=Pair(broker.Port,origin);broker.Publish(run.run_id,run.generation,h.message_id);
   var send=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   string id=S(send,"id");
   Request(broker.Port,"POST","/v1/event",origin,token,new{command_id=id,kind="composer_claimed",conversation_id=run.target.conversation_id,document_epoch="doc-b",payload_hash=h.payload_hash});
   Request(broker.Port,"POST","/v1/authorize",origin,token,new{command_id=id,conversation_id=run.target.conversation_id,document_epoch="doc-b",payload_hash=h.payload_hash});
   var amb=Request(broker.Port,"POST","/v1/event",origin,token,new{command_id=id,kind="ambiguous",conversation_id=run.target.conversation_id,document_epoch="doc-b",payload_hash=h.payload_hash,error="receipt_unconfirmed"});
   Check(amb.Item1==200 && store.GetRun(run.run_id).handoffs[0].state=="AMBIGUOUS","ambiguous send fenced");
   Check(!store.CanAutoDispatch(run.run_id,run.generation,h.message_id),"ambiguous send never enters auto-resend path");

   var reconcile=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(S(reconcile,"kind")=="reconcile_ambiguous" && S(reconcile,"text").Contains(h.message_id),
     "ambiguous send produces receipt reconciliation command, not send command");
   var late=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=S(reconcile,"id"),kind="ambiguous_delivered",conversation_id=run.target.conversation_id,
    document_epoch="doc-reconcile",user_message_id="late-user-row"
   });
   var recovered=store.GetRun(run.run_id).handoffs[0];
   Check(late.Item1==200 && recovered.state=="USER_RECEIPT_CONFIRMED" &&
         recovered.provider_user_message_id=="late-user-row","late exact receipt resolves ambiguity without duplicate send");
   var observe=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(S(observe,"kind")=="observe_response","resolved ambiguity advances only to response observation");
  }

  var unresolvedRun=store.CreateRun(Target("unresolved"));
  var unresolved=store.QueueHandoff(unresolvedRun.run_id,unresolvedRun.generation,"a","b","c","none");
  store.BindSourceTurn(unresolvedRun.run_id,unresolvedRun.generation,unresolved.message_id,"src-u","turn-u","doc-u");
  store.MarkSourceTerminal(unresolvedRun.run_id,unresolvedRun.generation,unresolved.message_id,"src-u","turn-u","doc-u");
  using(var broker=new M1BrowserBroker(store,0)) {
   broker.Start();const string origin="chrome-extension://abcdefghijklmnopabcdefghijklmnop";
   string token=Pair(broker.Port,origin);broker.Publish(unresolvedRun.run_id,unresolvedRun.generation,unresolved.message_id);
   var send=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   string id=S(send,"id");
   Request(broker.Port,"POST","/v1/event",origin,token,new{command_id=id,kind="composer_claimed",conversation_id=unresolvedRun.target.conversation_id,document_epoch="doc-u2",payload_hash=unresolved.payload_hash});
   Request(broker.Port,"POST","/v1/authorize",origin,token,new{command_id=id,conversation_id=unresolvedRun.target.conversation_id,document_epoch="doc-u2",payload_hash=unresolved.payload_hash});
   Request(broker.Port,"POST","/v1/event",origin,token,new{command_id=id,kind="ambiguous",conversation_id=unresolvedRun.target.conversation_id,document_epoch="doc-u2",payload_hash=unresolved.payload_hash,error="receipt_unconfirmed"});
   var reconcile=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   var unresolvedResult=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=S(reconcile,"id"),kind="ambiguous_unresolved",conversation_id=unresolvedRun.target.conversation_id,
    document_epoch="doc-u3",error="exact_receipt_not_observed_after_reload"
   });
   var state=store.GetRun(unresolvedRun.run_id);
   Check(unresolvedResult.Item1==200 && state.handoffs[0].state=="FAILED_TERMINAL" &&
         state.status=="WAITING_HUMAN","unresolved ambiguity escalates instead of auto-resending");
  }

  var retryRun=store.CreateRun(Target("retry"));
  var retry=store.QueueHandoff(retryRun.run_id,retryRun.generation,"a","b","c","none");
  store.BindSourceTurn(retryRun.run_id,retryRun.generation,retry.message_id,"source-r","source-turn-r","doc-r");
  store.MarkSourceTerminal(retryRun.run_id,retryRun.generation,retry.message_id,"source-r","source-turn-r","doc-r");
  using(var broker=new M1BrowserBroker(store,0)) {
   broker.Start();const string origin="chrome-extension://abcdefghijklmnopabcdefghijklmnop";
   string token=Pair(broker.Port,origin);broker.Publish(retryRun.run_id,retryRun.generation,retry.message_id);
   var send=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   var failed=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=S(send,"id"),kind="pre_send_failed",conversation_id=retryRun.target.conversation_id,
    document_epoch="doc-r2",payload_hash=retry.payload_hash,error="composer_not_ready"
   });
   var retryState=store.GetRun(retryRun.run_id).handoffs[0];
   Check(failed.Item1==200 && retryState.state=="PRE_SEND_RETRY" &&
         retryState.recovery_attempt==1 && retryState.next_attempt_utc.Length>0,
         "pre-send failure schedules durable bounded retry");
   var early=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(early==null,"recovery command is not offered before next_attempt_utc");
   Thread.Sleep(5400);
   var recover=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(S(recover,"kind")=="recover_target","due pre-send retry becomes exact-target recovery, not direct Send");
   var ready=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=S(recover,"id"),kind="target_recovered",conversation_id=retryRun.target.conversation_id,
    document_epoch="doc-r3"
   });
   Check(ready.Item1==200 && store.GetRun(retryRun.run_id).handoffs[0].state=="TARGET_READY",
     "successful target recovery returns to pre-Send target-ready stage");
   var resend=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(S(resend,"kind")=="send_handoff","only a proven pre-Send failure may return to native Send");
  }

  var rateRun=store.CreateRun(Target("ratelimit"));
  var rate=store.QueueHandoff(rateRun.run_id,rateRun.generation,"a","b","c","none");
  store.BindSourceTurn(rateRun.run_id,rateRun.generation,rate.message_id,"src-rate","turn-rate","doc-rate");
  store.MarkSourceTerminal(rateRun.run_id,rateRun.generation,rate.message_id,"src-rate","turn-rate","doc-rate");
  using(var broker=new M1BrowserBroker(store,0)) {
   broker.Start();const string origin="chrome-extension://abcdefghijklmnopabcdefghijklmnop";
   string token=Pair(broker.Port,origin);broker.Publish(rateRun.run_id,rateRun.generation,rate.message_id);
   var send=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   var limited=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=S(send,"id"),kind="provider_rate_limited",conversation_id=rateRun.target.conversation_id,
    document_epoch="doc-rate2",payload_hash=rate.payload_hash,error="too many requests",retry_after_seconds=900
   });
   var limitedState=store.GetRun(rateRun.run_id).handoffs[0];
   Check(limited.Item1==200 && limitedState.state=="RATE_LIMITED" &&
         limitedState.next_attempt_utc.Length>0 && limitedState.recovery_attempt==1,
         "provider rate limit is durably backed off before Send");
   var noHammer=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(noHammer==null,"rate-limited command is not hammered before durable due time");
  }

  var authRun=store.CreateRun(Target("auth"));
  var authH=store.QueueHandoff(authRun.run_id,authRun.generation,"a","b","c","none");
  store.BindSourceTurn(authRun.run_id,authRun.generation,authH.message_id,"src-auth","turn-auth","doc-auth");
  store.MarkSourceTerminal(authRun.run_id,authRun.generation,authH.message_id,"src-auth","turn-auth","doc-auth");
  using(var broker=new M1BrowserBroker(store,0)) {
   broker.Start();const string origin="chrome-extension://abcdefghijklmnopabcdefghijklmnop";
   string token=Pair(broker.Port,origin);broker.Publish(authRun.run_id,authRun.generation,authH.message_id);
   var send=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   var blocked=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=S(send,"id"),kind="provider_auth_required",conversation_id=authRun.target.conversation_id,
    document_epoch="doc-auth2",payload_hash=authH.payload_hash,error="sign-in required"
   });
   var blockedRun=store.GetRun(authRun.run_id);
   Check(blocked.Item1==200 && blockedRun.handoffs[0].state=="BLOCKED_AUTH" &&
         blockedRun.status=="BLOCKED_AUTH","authentication challenge stops automatic Send and requires human attention");
  }
 }

 static void TestSourceSupersessionAndRestart(string root) {
  var store=new AutomationStateStore(root);
  var run=store.CreateRun(Target("supersede"));
  var h=store.QueueHandoff(run.run_id,run.generation,"a","b","c","none");
  using(var broker=new M1BrowserBroker(store,0)) {
   broker.Start();const string origin="chrome-extension://abcdefghijklmnopabcdefghijklmnop";
   string token=Pair(broker.Port,origin);broker.Publish(run.run_id,run.generation,h.message_id);
   var gate=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   string id=S(gate,"id");
   Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=id,kind="source_bound",conversation_id=run.target.conversation_id,
    document_epoch="doc-s",user_message_id="source-s",response_turn_id="turn-s"
   });
   var superseded=Request(broker.Port,"POST","/v1/event",origin,token,new{
    command_id=id,kind="source_superseded",conversation_id=run.target.conversation_id,
    document_epoch="doc-s",user_message_id="source-s",response_turn_id="turn-s"
   });
   var state=store.GetRun(run.run_id);
   Check(superseded.Item1==200 && state.handoffs[0].state=="CANCELLED" && state.status=="WAITING_HUMAN","new human input supersedes pre-send baton");
  }

  var recover=store.CreateRun(Target("recover"));
  var rh=store.QueueHandoff(recover.run_id,recover.generation,"a","b","c","none");
  store.BindSourceTurn(recover.run_id,recover.generation,rh.message_id,"src-rec","turn-rec","doc-rec");
  // Simulate broker restart while waiting on the exact source turn.
  using(var broker=new M1BrowserBroker(new AutomationStateStore(root),0)) {
   broker.Start();int published=broker.RepublishRecoverable();
   Check(published>=1,"recoverable durable handoff republished after broker restart");
   const string origin="chrome-extension://abcdefghijklmnopabcdefghijklmnop";
   string token=Pair(broker.Port,origin);
   var cmd=Command(Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null).Item2);
   Check(S(cmd,"kind")=="gate_current_turn" && S(cmd,"user_message_id")=="src-rec","restart reoffers exact source gate");
  }
 }

 public static int Main(string[] args) {
  string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"M1BrokerTestArtifacts","run-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(root);
  try {
   TestFullLifecycle(root);
   TestAmbiguityAndPreSend(root);
   TestSourceSupersessionAndRestart(root);
   Console.WriteLine("M1 browser broker tests PASS: "+passed);
   Console.WriteLine("state_root="+root);
   return 0;
  } catch(Exception e) {
   Console.Error.WriteLine(e.ToString());
   return 1;
  }
 }
}
