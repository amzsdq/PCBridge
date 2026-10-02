using System;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

static class M1BrowserBrokerTests {
 static int passed;
 static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed++;}
 static string Json(object value){return new JavaScriptSerializer().Serialize(value);}
 static Tuple<int,string> Request(int port,string method,string path,string origin,string token,object body) {
  var req=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+port+path);
  req.Method=method;req.Proxy=null;req.Timeout=5000;req.ReadWriteTimeout=5000;
  req.Headers["Origin"]=origin;req.Headers["X-PCBridge-Protocol"]=M1BrowserBroker.Protocol;
  if(!String.IsNullOrEmpty(token))req.Headers["Authorization"]="Bearer "+token;
  if(body!=null){byte[] bytes=Encoding.UTF8.GetBytes(Json(body));req.ContentType="application/json";req.ContentLength=bytes.Length;using(var s=req.GetRequestStream())s.Write(bytes,0,bytes.Length);}
  try{using(var resp=(HttpWebResponse)req.GetResponse())using(var sr=new StreamReader(resp.GetResponseStream()))return Tuple.Create((int)resp.StatusCode,sr.ReadToEnd());}
  catch(WebException e){using(var resp=(HttpWebResponse)e.Response)using(var sr=new StreamReader(resp.GetResponseStream()))return Tuple.Create((int)resp.StatusCode,sr.ReadToEnd());}
 }
 static Dictionary<string,object> Obj(string json){return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);}
 static AutomationTarget Target(string suffix){return new AutomationTarget{provider="chatgpt",provider_profile_id="profile_"+suffix,conversation_id="12345678-abcd-4abc-8abc-"+suffix.PadRight(12,'0').Substring(0,12)};}

 public static int Main(string[] args){
  string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"M1BrokerTestArtifacts","run-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(root);
  try{
   var store=new AutomationStateStore(root);
   var run=store.CreateRun(Target("broker"));
   var h=store.QueueHandoff(run.run_id,run.generation,"phase one","phase two","verified","none");
   store.Advance(run.run_id,run.generation,h.message_id,"WAIT_CURRENT_TURN_END","","");
   store.Advance(run.run_id,run.generation,h.message_id,"TARGET_READY","","");

   using(var broker=new M1BrowserBroker(store,0)){
    broker.Start();Check(broker.Port>0,"broker bound ephemeral loopback port");
    const string origin="chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    var hello=Request(broker.Port,"GET","/hello",origin,"",null);
    Check(hello.Item1==200 && hello.Item2.Contains(M1BrowserBroker.Protocol),"hello protocol");

    var badPair=Request(broker.Port,"POST","/pair","https://evil.example","",new{protocol=M1BrowserBroker.Protocol});
    Check(badPair.Item1==403,"web origin cannot pair");

    var pair=Request(broker.Port,"POST","/pair",origin,"",new{protocol=M1BrowserBroker.Protocol});
    var pairObj=Obj(pair.Item2);string token=Convert.ToString(pairObj["token"]);
    Check(pair.Item1==200 && token.Length>20,"extension paired");

    broker.Publish(run.run_id,run.generation,h.message_id);
    var cmd=Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null);
    Check(cmd.Item1==200 && cmd.Item2.Contains(h.message_id),"ready handoff published");

    var claim=Request(broker.Port,"POST","/v1/event",origin,token,new{
      command_id=h.message_id,kind="composer_claimed",conversation_id=run.target.conversation_id,
      document_epoch="doc-one",payload_hash=h.payload_hash
    });
    Check(claim.Item1==200,"composer claim accepted");
    Check(store.GetRun(run.run_id).handoffs[0].state=="COMPOSER_CLAIMED","composer claim persisted");

    var auth=Request(broker.Port,"POST","/v1/authorize",origin,token,new{
      command_id=h.message_id,conversation_id=run.target.conversation_id,document_epoch="doc-one",payload_hash=h.payload_hash
    });
    Check(auth.Item1==200 && auth.Item2.Contains("dispatch_intent_durable"),"dispatch authorization durable");
    Check(store.GetRun(run.run_id).handoffs[0].state=="SEND_DISPATCHED","dispatch intent persisted before browser click");

    var amb=Request(broker.Port,"POST","/v1/event",origin,token,new{
      command_id=h.message_id,kind="ambiguous",conversation_id=run.target.conversation_id,
      document_epoch="doc-one",error="receipt_unconfirmed"
    });
    Check(amb.Item1==200,"ambiguous receipt accepted");
    Check(store.GetRun(run.run_id).handoffs[0].state=="AMBIGUOUS","ambiguity persisted and fenced");

    var after=Request(broker.Port,"GET","/v1/command?wait_ms=0",origin,token,null);
    Check(!after.Item2.Contains(h.message_id),"ambiguous command not reoffered");
   }

   // Pre-Send failure is retryable and never becomes SEND_DISPATCHED.
   var retryRun=store.CreateRun(Target("retry"));
   var retry=store.QueueHandoff(retryRun.run_id,retryRun.generation,"a","b","c","none");
   store.Advance(retryRun.run_id,retryRun.generation,retry.message_id,"WAIT_CURRENT_TURN_END","","");
   store.Advance(retryRun.run_id,retryRun.generation,retry.message_id,"TARGET_READY","","");
   using(var broker=new M1BrowserBroker(store,0)){
    broker.Start();const string origin="chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    var pair=Obj(Request(broker.Port,"POST","/pair",origin,"",new{protocol=M1BrowserBroker.Protocol}).Item2);
    string token=Convert.ToString(pair["token"]);broker.Publish(retryRun.run_id,retryRun.generation,retry.message_id);
    var failed=Request(broker.Port,"POST","/v1/event",origin,token,new{
      command_id=retry.message_id,kind="pre_send_failed",conversation_id=retryRun.target.conversation_id,
      document_epoch="doc-two",error="composer_not_ready"
    });
    Check(failed.Item1==200,"pre-send failure accepted");
    Check(store.GetRun(retryRun.run_id).handoffs[0].state=="PRE_SEND_RETRY","pre-send failure remains retryable");
   }

   Console.WriteLine("M1 browser broker tests PASS: "+passed);
   Console.WriteLine("state_root="+root);
   return 0;
  }catch(Exception e){Console.Error.WriteLine(e.ToString());return 1;}
 }
}
