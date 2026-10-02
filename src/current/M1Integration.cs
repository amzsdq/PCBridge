using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Drawing;

static class M1McpIntegration {
 static readonly object Gate=new object();
 static M1AutomationRuntime runtime;

 static object Prop(string type,string description) {
  return new {type=type,description=description};
 }
 static object Tool(string name,string description,Dictionary<string,object> properties,string[] required) {
  return new {name=name,description=description,inputSchema=new {
   type="object",properties=properties,required=required,additionalProperties=false
  }};
 }
 static string S(IDictionary<string,object> a,string name,string fallback="") {
  object value;
  return a!=null&&a.TryGetValue(name,out value)&&value!=null?Convert.ToString(value):fallback;
 }
 static string Required(IDictionary<string,object> a,string name,int max) {
  string value=S(a,name).Trim();
  if(value.Length<1||value.Length>max)throw new Exception(name+" is required (maximum "+max+" characters).");
  return value;
 }
 static long L(IDictionary<string,object> a,string name) {
  long value;
  if(!Int64.TryParse(Required(a,name,32),out value)||value<1)throw new Exception(name+" must be a positive integer.");
  return value;
 }

 public static M1AutomationRuntime Runtime {
  get {
   lock(Gate) {
    if(runtime==null) {
     string configured=Environment.GetEnvironmentVariable("PCBRIDGE_M1_BROWSER_EXE");
     string companion=Path.Combine(Core.Root,"provider","chatgpt-extension");
     int[] ports=Core.Instance.StartsWith("PCBridge-Integrated-Test-",StringComparison.Ordinal)?new[]{0}:null;
     runtime=new M1AutomationRuntime(Core.Root,configured,companion,ports);
     runtime.Start();
    }
    return runtime;
   }
  }
 }

 public static void Stop() {
  lock(Gate) {
   if(runtime!=null) {
    try{runtime.Dispose();}catch{}
    runtime=null;
   }
  }
 }

 public static object[] Tools() {
  var tools=new List<object>();
  tools.Add(Tool(
   "automation_prepare",
   "Prepare durable self-handoff for this ChatGPT conversation. Generate one stable non-secret session_id for the initial setup and reuse it until a run_id is returned. Target binding is confirmed locally; this tool cannot retarget a conversation.",
   new Dictionary<string,object>{
    {"session_id",Prop("string","Stable non-secret id generated once for this conversation, 8-512 characters.")}
   },
   new[]{"session_id"}));

  tools.Add(Tool(
   "automation_status",
   "Read M1 autonomous-handoff status. Supply run_id after a run exists, or the initial session_id while target binding is being prepared.",
   new Dictionary<string,object>{
    {"run_id",Prop("string","Durable run_id returned by automation_handoff.")},
    {"session_id",Prop("string","Initial non-secret session id used before a run exists.")}
   },
   new string[0]));

  tools.Add(Tool(
   "automation_handoff",
   "Persist exactly one continuation baton when useful work remains beyond this assistant turn. After the first queued baton, prefer run_id + generation from the handoff payload. The Core waits for the exact current response to finish before native Send. Do not call for progress-only chatter.",
   new Dictionary<string,object>{
    {"run_id",Prop("string","Existing durable run id. Preferred on successor turns.")},
    {"generation",Prop("integer","Current fencing generation from the handoff payload.")},
    {"session_id",Prop("string","Initial setup id; needed only before a run_id exists.")},
    {"summary",Prop("string","Concise verified work completed this turn.")},
    {"next_action",Prop("string","Concrete next useful work unit.")},
    {"verification",Prop("string","What was actually checked or verified.")},
    {"blocking_state",new {type="string",@enum=new[]{"none","hardlock","human","external"},description="Use a blocking state instead of creating endless Continue turns."}}
   },
   new[]{"summary","next_action","verification"}));

  tools.Add(Tool(
   "automation_complete",
   "Mark the whole autonomous run complete after the requested work is actually verified. This cancels any unsent continuation and does not create a new ChatGPT turn.",
   new Dictionary<string,object>{
    {"run_id",Prop("string","Existing durable run id. Preferred.")},
    {"generation",Prop("integer","Current fencing generation.")},
    {"session_id",Prop("string","Initial session id only when no run_id is available.")},
    {"verification",Prop("string","Evidence that the requested task is complete.")}
   },
   new[]{"verification"}));
  return tools.ToArray();
 }

 static object RuntimeView(M1RuntimeStatus status) {
  return new {
   started=status.started,
   broker_port=status.broker_port,
   recoverable_published=status.recoverable_published,
   active_runs=status.active_runs,
   pending_bindings=status.pending_bindings,
   browser_runtime_available=status.browser_runtime_available,
   companion_ready=status.companion_ready,
   provider_running=status.provider_running
  };
 }

 static object BindingView(M1SessionBindingRecord row) {
  return row==null?null:new {
   binding_id=row.binding_id,
   state=row.state,
   provider=row.provider,
   provider_profile_id=row.provider_profile_id,
   conversation_id=row.conversation_id,
   candidate_conversation_id=row.candidate_conversation_id,
   created_utc=row.created_utc,
   candidate_utc=row.candidate_utc,
   confirmed_utc=row.confirmed_utc
  };
 }

 static object RunView(AutomationRun run) {
  if(run==null)return null;
  var active=run.handoffs==null?null:run.handoffs.LastOrDefault(h=>h.state!="TERMINAL_CONFIRMED"&&h.state!="CANCELLED"&&h.state!="FAILED_TERMINAL");
  return new {
   run_id=run.run_id,
   generation=run.generation,
   seq=run.seq,
   status=run.status,
   attention=run.attention,
   target=run.target==null?null:new {
    provider=run.target.provider,
    provider_profile_id=run.target.provider_profile_id,
    conversation_id=run.target.conversation_id
   },
   active_handoff=active==null?null:new {
    message_id=active.message_id,
    seq=active.seq,
    state=active.state,
    response_turn_id=active.response_turn_id,
    error_class=active.error_class
   }
  };
 }

 public static object Call(string name,IDictionary<string,object> a) {
  if(name=="automation_prepare") {
   string session=Required(a,"session_id",512);
   var row=Runtime.PrepareSession(session);
   return new {
    state=row.state=="bound"?"READY":"BINDING_REQUIRED",
    binding=BindingView(row),
    runtime=RuntimeView(Runtime.Status()),
    next=row.state=="bound"
     ?"Target already confirmed. automation_handoff may queue the baton."
     :"Use PCBridge > 자동화 채팅 연결 once to confirm this exact ChatGPT conversation locally."
   };
  }

  if(name=="automation_status") {
   string runId=S(a,"run_id").Trim(),session=S(a,"session_id").Trim();
   AutomationRun run=null;
   M1SessionBindingRecord binding=null;
   if(runId.Length>0)run=Runtime.Run(runId);
   if(session.Length>0) {
    binding=Runtime.Bindings.GetForSession(session);
    if(run==null)run=Runtime.ActiveRun(session);
   }
   return new {runtime=RuntimeView(Runtime.Status()),binding=BindingView(binding),run=RunView(run)};
  }

  if(name=="automation_handoff") {
   string summary=Required(a,"summary",4000);
   string next=Required(a,"next_action",4000);
   string verification=Required(a,"verification",4000);
   string blocking=S(a,"blocking_state","none").Trim().ToLowerInvariant();
   string runId=S(a,"run_id").Trim();
   if(runId.Length>0)
    return Runtime.HandoffByRun(runId,L(a,"generation"),summary,next,verification,blocking);
   string session=Required(a,"session_id",512);
   return Runtime.Handoff(session,summary,next,verification,blocking);
  }

  if(name=="automation_complete") {
   string verification=Required(a,"verification",4000);
   string runId=S(a,"run_id").Trim();
   if(runId.Length>0)return Runtime.CompleteByRun(runId,L(a,"generation"),verification);
   return Runtime.Complete(Required(a,"session_id",512),verification);
  }

  throw new Exception("Unknown M1 automation tool.");
 }
}

sealed class M1BindingWindow : Form {
 readonly ListBox list=new ListBox();
 readonly TextBox url=new TextBox();
 readonly Label info=new Label();
 readonly Button confirm=new Button();
 List<M1SessionBindingRecord> pending=new List<M1SessionBindingRecord>();

 public M1BindingWindow() {
  Text="자동화 채팅 연결 · PCBridge";
  Font=new Font("Malgun Gothic",10);
  ClientSize=new Size(780,430);
  StartPosition=FormStartPosition.CenterParent;

  Controls.Add(new Label{
   Text="자동화가 이어서 메시지를 보낼 정확한 ChatGPT 대화를 한 번만 확인합니다. 아래 목록은 AI가 만든 대기 요청입니다.",
   Bounds=new Rectangle(15,15,745,45)
  });
  list.SetBounds(15,65,745,145);
  Controls.Add(list);

  Controls.Add(new Label{
   Text="현재 이 대화의 ChatGPT 주소를 붙여넣으세요. 예: https://chatgpt.com/c/...",
   Bounds=new Rectangle(15,225,745,25)
  });
  url.SetBounds(15,255,745,30);
  Controls.Add(url);

  confirm.Text="선택 요청을 이 채팅에 연결";
  confirm.SetBounds(515,305,245,38);
  confirm.Click+=(a,b)=>Confirm();
  var refresh=new Button{Text="새로 고침",Bounds=new Rectangle(15,305,150,38)};
  refresh.Click+=(a,b)=>Reload();
  Controls.Add(confirm);
  Controls.Add(refresh);

  info.SetBounds(15,355,745,60);
  Controls.Add(info);
  Reload();
 }

 M1SessionBindingStore Store() {
  return new M1SessionBindingStore(Path.Combine(Core.Root,"automation"));
 }

 void Reload() {
  try {
   pending=Store().Pending().OrderBy(x=>x.created_utc).ToList();
   list.BeginUpdate();list.Items.Clear();
   foreach(var row in pending)
    list.Items.Add(row.state+" · "+row.binding_id.Substring(0,8)+" · "+row.created_utc);
   list.EndUpdate();
   if(pending.Count>0&&list.SelectedIndex<0)list.SelectedIndex=0;
   info.Text=pending.Count==0
    ?"대기 중인 자동화 연결 요청이 없습니다. 먼저 ChatGPT에서 automation_prepare를 호출하세요."
    :"대기 요청 "+pending.Count+"개. 주소를 확인한 뒤 연결하세요.";
  } catch(Exception e) { info.Text=e.Message; }
 }

 static string ConversationId(string raw) {
  Uri uri;
  if(!Uri.TryCreate((raw??"").Trim(),UriKind.Absolute,out uri) ||
     uri.Scheme!="https" ||
     !(uri.Host.Equals("chatgpt.com",StringComparison.OrdinalIgnoreCase) ||
       uri.Host.Equals("chat.openai.com",StringComparison.OrdinalIgnoreCase)))
   throw new Exception("ChatGPT 대화 주소가 아닙니다.");
  var match=Regex.Match(uri.AbsolutePath,@"\A/(?:g/[^/]+/)?c/([0-9a-fA-F-]{8,64})(?:/|$)");
  if(!match.Success)throw new Exception("대화 ID가 있는 정확한 ChatGPT 채팅 주소를 넣으세요.");
  return match.Groups[1].Value;
 }

 void Confirm() {
  try {
   if(list.SelectedIndex<0||list.SelectedIndex>=pending.Count)throw new Exception("연결 요청을 선택하세요.");
   string conversation=ConversationId(url.Text);
   var store=Store();
   var row=pending[list.SelectedIndex];
   store.ProposeCandidate(row.binding_id,conversation,"local-ui-"+Guid.NewGuid().ToString("N"));
   var bound=store.ConfirmCandidateLocal(row.binding_id,conversation,"chatgpt-default");
   info.Text="연결 완료: "+bound.conversation_id+"\r\n이제 ChatGPT에서 automation_handoff를 다시 호출하면 됩니다.";
   Reload();
  } catch(Exception e) {
   info.Text=e.Message;
  }
 }
}
