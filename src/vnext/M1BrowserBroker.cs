using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Security.Cryptography;

public sealed class M1BrowserCommand {
 public string id="";
 public string run_id="";
 public long generation;
 public int seq;
 public string conversation_id="";
 public string payload_hash="";
 public string text="";
}

public sealed class M1BrowserEvent {
 public string command_id="";
 public string kind="";
 public string conversation_id="";
 public string document_epoch="";
 public string payload_hash="";
 public string user_message_id="";
 public string error="";
 public bool rebound;
}

sealed class M1BrokerRecord {
 public M1BrowserCommand command;
 public DateTime leaseUntilUtc=DateTime.MinValue;
 public bool authorized;
 public bool retired;
}

public sealed class M1BrowserBroker : IDisposable {
 public const string Protocol="m1-browser-v1";
 readonly AutomationStateStore state;
 readonly int[] candidates;
 readonly object gate=new object();
 readonly Dictionary<string,M1BrokerRecord> records=new Dictionary<string,M1BrokerRecord>(StringComparer.Ordinal);
 readonly List<M1BrowserEvent> events=new List<M1BrowserEvent>();
 readonly AutoResetEvent changed=new AutoResetEvent(false);
 TcpListener listener;
 Thread thread;
 volatile bool stopping;
 string token;
 string pinnedOrigin;
 int port;

 public M1BrowserBroker(AutomationStateStore automationState,params int[] ports) {
  if(automationState==null)throw new ArgumentNullException("automationState");
  state=automationState;
  candidates=ports!=null&&ports.Length>0?ports:new[]{8795,8796,8797,8798,8799};
  token=RandomToken();
 }

 public int Port { get { return port; } }
 public string Endpoint { get { return port>0?"http://127.0.0.1:"+port:""; } }

 public void Start() {
  lock(gate) {
   if(listener!=null)return;
   Exception last=null;
   foreach(int candidate in candidates) {
    try {
     var l=new TcpListener(IPAddress.Loopback,candidate);
     l.Start(32);
     listener=l;
     port=((IPEndPoint)l.LocalEndpoint).Port;
     break;
    } catch(Exception e) { last=e; }
   }
   if(listener==null)throw new InvalidOperationException("m1_browser_broker_bind_failed",last);
   stopping=false;
   thread=new Thread(AcceptLoop){IsBackground=true,Name="PCBridge M1 browser broker"};
   thread.Start();
  }
 }

 public void Publish(string runId,long generation,string messageId) {
  var run=state.GetRun(runId);
  if(run.generation!=generation)throw new InvalidOperationException("stale_generation");
  var handoff=run.handoffs.Find(delegate(AutomationHandoff h){return h.message_id==messageId;});
  if(handoff==null)throw new InvalidOperationException("handoff_not_found");
  if(handoff.state!="TARGET_READY" && handoff.state!="COMPOSER_CLAIMED")
   throw new InvalidOperationException("handoff_not_ready_for_browser");
  var command=new M1BrowserCommand {
   id=handoff.message_id,run_id=run.run_id,generation=run.generation,seq=handoff.seq,
   conversation_id=run.target.conversation_id,payload_hash=handoff.payload_hash,text=handoff.payload
  };
  lock(gate) {
   M1BrokerRecord existing;
   if(records.TryGetValue(command.id,out existing)) {
    if(existing.command.payload_hash!=command.payload_hash || existing.command.conversation_id!=command.conversation_id)
     throw new InvalidOperationException("browser_command_identity_conflict");
    if(existing.retired) { existing.retired=false;existing.authorized=false;existing.leaseUntilUtc=DateTime.MinValue; }
   } else records.Add(command.id,new M1BrokerRecord{command=command});
  }
  changed.Set();
 }

 public M1BrowserEvent[] DrainEvents() {
  lock(gate) {
   var copy=events.ToArray();
   events.Clear();
   return copy;
  }
 }

 public void Dispose() {
  stopping=true;
  changed.Set();
  TcpListener l;
  Thread t;
  lock(gate){l=listener;t=thread;listener=null;thread=null;}
  try{if(l!=null)l.Stop();}catch{}
  try{if(t!=null&&t.IsAlive)t.Join(3000);}catch{}
  changed.Dispose();
 }

 void AcceptLoop() {
  while(!stopping) {
   try {
    var client=listener.AcceptTcpClient();
    ThreadPool.QueueUserWorkItem(delegate { using(client) { try{Handle(client);}catch{} } });
   } catch(SocketException) { if(!stopping)Thread.Sleep(50); }
   catch(ObjectDisposedException) { break; }
   catch { if(!stopping)Thread.Sleep(50); }
  }
 }

 void Handle(TcpClient client) {
  client.ReceiveTimeout=35000;
  client.SendTimeout=10000;
  using(var stream=client.GetStream()) {
   HttpRequestData req=ReadRequest(stream);
   if(req==null)return;
   if(req.method=="OPTIONS") { Write(stream,204,new{},req.origin);return; }
   if(req.path=="/hello") {
    if(req.method!="GET"){WriteError(stream,405,"method_not_allowed",req.origin);return;}
    Write(stream,200,new{app="pcbridge",protocol=Protocol,version="m1"},req.origin);return;
   }
   if(req.path=="/pair") {
    if(req.method!="POST" || req.protocol!=Protocol){WriteError(stream,400,"pair_protocol_invalid",req.origin);return;}
    if(!ValidExtensionOrigin(req.origin)){WriteError(stream,403,"extension_origin_required",req.origin);return;}
    lock(gate) {
     if(pinnedOrigin==null)pinnedOrigin=req.origin;
     else if(!String.Equals(pinnedOrigin,req.origin,StringComparison.Ordinal)){WriteError(stream,403,"extension_origin_mismatch",req.origin);return;}
    }
    Write(stream,200,new{token=token,protocol=Protocol},req.origin);return;
   }
   if(!Authorized(req)){WriteError(stream,401,"broker_auth_required",req.origin);return;}
   if(req.path=="/v1/presence") { Write(stream,200,new{ok=true,port=port},req.origin);return; }
   if(req.path=="/v1/command") { ServeCommand(stream,req);return; }
   if(req.path=="/v1/authorize") { Authorize(stream,req);return; }
   if(req.path=="/v1/event") { Event(stream,req);return; }
   WriteError(stream,404,"not_found",req.origin);
  }
 }

 void ServeCommand(NetworkStream stream,HttpRequestData req) {
  int wait=QueryInt(req.query,"wait_ms",0,0,25000);
  M1BrowserCommand command=NextCommand();
  if(command==null && wait>0) {
   changed.WaitOne(wait);
   command=NextCommand();
  }
  Write(stream,200,new{command=command},req.origin);
 }

 M1BrowserCommand NextCommand() {
  lock(gate) {
   DateTime now=DateTime.UtcNow;
   foreach(var pair in records) {
    var rec=pair.Value;
    if(rec.retired || rec.authorized)continue;
    if(rec.leaseUntilUtc>now)continue;
    rec.leaseUntilUtc=now.AddSeconds(20);
    return rec.command;
   }
   return null;
  }
 }

 void Authorize(NetworkStream stream,HttpRequestData req) {
  if(req.method!="POST"){WriteError(stream,405,"method_not_allowed",req.origin);return;}
  var body=JsonObject(req.body);
  string id=Get(body,"command_id"),conversation=Get(body,"conversation_id"),hash=Get(body,"payload_hash");
  string epoch=Get(body,"document_epoch");
  if(String.IsNullOrWhiteSpace(epoch)||epoch.Length>160){WriteError(stream,400,"document_epoch_invalid",req.origin);return;}
  M1BrokerRecord rec;
  lock(gate) {
   if(!records.TryGetValue(id,out rec)||rec.retired){WriteError(stream,409,"command_not_active",req.origin);return;}
   if(rec.authorized){Write(stream,200,new{ok=true,existing=true},req.origin);return;}
   if(rec.command.conversation_id!=conversation||rec.command.payload_hash!=hash){WriteError(stream,409,"command_identity_mismatch",req.origin);return;}
  }
  // Persist the conservative dispatch intent before the browser is allowed to click Send.
  var run=state.GetRun(rec.command.run_id);
  var h=run.handoffs.Find(delegate(AutomationHandoff x){return x.message_id==id;});
  if(h==null){WriteError(stream,409,"handoff_not_found",req.origin);return;}
  if(h.state=="TARGET_READY")state.Advance(run.run_id,run.generation,id,"COMPOSER_CLAIMED","","");
  run=state.GetRun(rec.command.run_id);
  h=run.handoffs.Find(delegate(AutomationHandoff x){return x.message_id==id;});
  if(h.state=="COMPOSER_CLAIMED")state.Advance(run.run_id,run.generation,id,"SEND_AUTHORIZED","","");
  run=state.GetRun(rec.command.run_id);
  h=run.handoffs.Find(delegate(AutomationHandoff x){return x.message_id==id;});
  if(h.state=="SEND_AUTHORIZED")state.Advance(run.run_id,run.generation,id,"SEND_DISPATCHED","","");
  lock(gate){rec.authorized=true;rec.leaseUntilUtc=DateTime.MaxValue;}
  Write(stream,200,new{ok=true,dispatch_intent_durable=true},req.origin);
 }

 void Event(NetworkStream stream,HttpRequestData req) {
  if(req.method!="POST"){WriteError(stream,405,"method_not_allowed",req.origin);return;}
  var obj=JsonObject(req.body);
  var ev=new M1BrowserEvent {
   command_id=Get(obj,"command_id"),kind=Get(obj,"kind"),conversation_id=Get(obj,"conversation_id"),
   document_epoch=Get(obj,"document_epoch"),payload_hash=Get(obj,"payload_hash"),
   user_message_id=Get(obj,"user_message_id"),error=Get(obj,"error"),rebound=GetBool(obj,"rebound")
  };
  if(ev.kind=="page_presence") { AddEvent(ev);Write(stream,200,new{ok=true},req.origin);return; }
  M1BrokerRecord rec;
  lock(gate) {
   if(!records.TryGetValue(ev.command_id,out rec)){WriteError(stream,409,"command_not_found",req.origin);return;}
   if(rec.command.conversation_id!=ev.conversation_id){WriteError(stream,409,"event_target_mismatch",req.origin);return;}
  }
  try {
   var run=state.GetRun(rec.command.run_id);
   var handoff=run.handoffs.Find(delegate(AutomationHandoff x){return x.message_id==rec.command.id;});
   if(handoff==null)throw new InvalidOperationException("handoff_not_found");
   if(ev.kind=="composer_claimed") {
    if(handoff.state=="TARGET_READY")state.Advance(run.run_id,run.generation,handoff.message_id,"COMPOSER_CLAIMED","","");
   } else if(ev.kind=="pre_send_failed") {
    if(handoff.state=="TARGET_READY"||handoff.state=="COMPOSER_CLAIMED")
     state.Advance(run.run_id,run.generation,handoff.message_id,"PRE_SEND_RETRY","",ev.error);
    lock(gate){rec.retired=true;}
   } else if(ev.kind=="delivered") {
    if(handoff.state!="SEND_DISPATCHED")throw new InvalidOperationException("delivery_without_dispatch_intent");
    state.Advance(run.run_id,run.generation,handoff.message_id,"USER_RECEIPT_CONFIRMED","",ev.error);
    lock(gate){rec.retired=true;}
   } else if(ev.kind=="ambiguous") {
    if(handoff.state!="SEND_DISPATCHED")throw new InvalidOperationException("ambiguity_without_dispatch_intent");
    state.Advance(run.run_id,run.generation,handoff.message_id,"AMBIGUOUS","",ev.error);
    lock(gate){rec.retired=true;}
   } else throw new InvalidOperationException("event_kind_invalid");
   AddEvent(ev);
   Write(stream,200,new{ok=true},req.origin);
  } catch(Exception e) { WriteError(stream,409,e.Message,req.origin); }
 }

 void AddEvent(M1BrowserEvent ev) {
  lock(gate) {
   events.Add(ev);
   if(events.Count>256)events.RemoveRange(0,events.Count-256);
  }
 }

 bool Authorized(HttpRequestData req) {
  if(req.protocol!=Protocol)return false;
  string origin;
  lock(gate){origin=pinnedOrigin;}
  if(origin==null || !String.Equals(req.origin,origin,StringComparison.Ordinal))return false;
  string expected="Bearer "+token;
  return String.Equals(req.authorization,expected,StringComparison.Ordinal);
 }

 static bool ValidExtensionOrigin(string origin) {
  if(String.IsNullOrWhiteSpace(origin)||origin.Length>200)return false;
  Uri uri;
  return Uri.TryCreate(origin,UriKind.Absolute,out uri)&&uri.Scheme=="chrome-extension"&&!String.IsNullOrWhiteSpace(uri.Host);
 }

 sealed class HttpRequestData {
  public string method,path,query,body,origin,authorization,protocol;
 }

 static HttpRequestData ReadRequest(NetworkStream stream) {
  byte[] header=ReadUntil(stream,new byte[]{13,10,13,10},16384);
  if(header==null)return null;
  string raw=Encoding.ASCII.GetString(header);
  string[] lines=raw.Split(new[]{"\r\n"},StringSplitOptions.None);
  if(lines.Length<1)return null;
  string[] first=lines[0].Split(' ');
  if(first.Length<2)return null;
  string target=first[1],path=target,query="";
  int q=target.IndexOf('?');if(q>=0){path=target.Substring(0,q);query=target.Substring(q+1);}
  var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  for(int i=1;i<lines.Length;i++){int colon=lines[i].IndexOf(':');if(colon>0)headers[lines[i].Substring(0,colon).Trim()]=lines[i].Substring(colon+1).Trim();}
  int length=0;string len;if(headers.TryGetValue("Content-Length",out len))Int32.TryParse(len,out length);
  if(length<0||length>65536)throw new InvalidDataException("http_body_too_large");
  byte[] bodyBytes=new byte[length];int read=0;
  while(read<length){int n=stream.Read(bodyBytes,read,length-read);if(n<=0)throw new EndOfStreamException();read+=n;}
  string value;return new HttpRequestData {
   method=first[0].ToUpperInvariant(),path=path,query=query,body=Encoding.UTF8.GetString(bodyBytes),
   origin=headers.TryGetValue("Origin",out value)?value:"",
   authorization=headers.TryGetValue("Authorization",out value)?value:"",
   protocol=headers.TryGetValue("X-PCBridge-Protocol",out value)?value:""
  };
 }

 static byte[] ReadUntil(Stream stream,byte[] marker,int max) {
  using(var ms=new MemoryStream()) {
   int matched=0;
   while(ms.Length<max) {
    int value=stream.ReadByte();if(value<0)return null;
    ms.WriteByte((byte)value);
    if((byte)value==marker[matched]){matched++;if(matched==marker.Length)return ms.ToArray();}
    else matched=(byte)value==marker[0]?1:0;
   }
   throw new InvalidDataException("http_headers_too_large");
  }
 }

 static void Write(NetworkStream stream,int status,object body,string origin) {
  string json=status==204?"":new JavaScriptSerializer{MaxJsonLength=1000000}.Serialize(body);
  byte[] bytes=Encoding.UTF8.GetBytes(json);
  var b=new StringBuilder();
  b.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
  b.Append("Content-Type: application/json; charset=utf-8\r\n");
  b.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
  b.Append("Cache-Control: no-store\r\n");
  b.Append("Connection: close\r\n");
  if(ValidExtensionOrigin(origin)) {
   b.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n");
   b.Append("Vary: Origin\r\n");
   b.Append("Access-Control-Allow-Headers: Authorization, Content-Type, X-PCBridge-Protocol\r\n");
   b.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
  }
  b.Append("\r\n");
  byte[] headers=Encoding.ASCII.GetBytes(b.ToString());
  stream.Write(headers,0,headers.Length);if(bytes.Length>0)stream.Write(bytes,0,bytes.Length);stream.Flush();
 }

 static void WriteError(NetworkStream stream,int status,string error,string origin) { Write(stream,status,new{error=error},origin); }
 static string StatusText(int status) {
  if(status==200)return "OK";if(status==204)return "No Content";if(status==400)return "Bad Request";
  if(status==401)return "Unauthorized";if(status==403)return "Forbidden";if(status==404)return "Not Found";
  if(status==405)return "Method Not Allowed";if(status==409)return "Conflict";return "Error";
 }

 static Dictionary<string,object> JsonObject(string body) {
  if(String.IsNullOrWhiteSpace(body))return new Dictionary<string,object>();
  var obj=new JavaScriptSerializer{MaxJsonLength=1000000}.Deserialize<Dictionary<string,object>>(body);
  if(obj==null)throw new InvalidDataException("json_object_required");return obj;
 }
 static string Get(IDictionary<string,object> obj,string key) { object v;return obj.TryGetValue(key,out v)&&v!=null?Convert.ToString(v):""; }
 static bool GetBool(IDictionary<string,object> obj,string key) { object v;return obj.TryGetValue(key,out v)&&v!=null&&Convert.ToBoolean(v); }
 static int QueryInt(string query,string key,int fallback,int low,int high) {
  foreach(string part in (query??"").Split('&')) {
   int i=part.IndexOf('=');string k=i>=0?part.Substring(0,i):part;
   if(k!=key)continue;int value;if(Int32.TryParse(i>=0?part.Substring(i+1):"",out value))return Math.Max(low,Math.Min(high,value));
  }
  return fallback;
 }
 static string RandomToken() {
  byte[] b=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(b);
  return Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
 }
}
