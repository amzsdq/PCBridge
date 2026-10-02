using System;
using System.IO;
using System.Linq;

public sealed class M1RuntimeStatus {
 public bool started;
 public int broker_port;
 public int recoverable_published;
 public int active_runs;
 public int pending_bindings;
 public bool browser_runtime_available;
 public bool companion_ready;
 public string state_root="";
}

public sealed class M1RuntimeHandoffResult {
 public string state="";
 public string binding_id="";
 public string binding_state="";
 public string run_id="";
 public long generation;
 public int seq;
 public string message_id="";
 public string handoff_state="";
 public bool existing;
 public bool blocked;
 public string blocking_state="";
}

public sealed class M1AutomationRuntime : IDisposable {
 readonly string root;
 readonly AutomationStateStore automation;
 readonly M1SessionBindingStore bindings;
 readonly M1BrowserProfileManager browser;
 readonly object gate=new object();
 M1BrowserBroker broker;
 bool started;
 int recovered;

 public M1AutomationRuntime(string rootPath)
  : this(rootPath,null,null) {}

 public M1AutomationRuntime(string rootPath,string browserExecutable,string companionExtensionRoot) {
  if(String.IsNullOrWhiteSpace(rootPath))throw new ArgumentException("rootPath");
  root=Path.GetFullPath(rootPath);
  Directory.CreateDirectory(root);
  automation=new AutomationStateStore(Path.Combine(root,"automation"));
  bindings=new M1SessionBindingStore(Path.Combine(root,"automation"));
  browser=new M1BrowserProfileManager(root,browserExecutable,companionExtensionRoot);
 }

 public AutomationStateStore Automation { get { return automation; } }
 public M1SessionBindingStore Bindings { get { return bindings; } }
 public M1BrowserProfileManager Browser { get { return browser; } }

 public M1RuntimeStatus Start() {
  lock(gate) {
   if(!started) {
    broker=new M1BrowserBroker(automation,0);
    broker.Start();
    recovered=broker.RepublishRecoverable();
    started=true;
   }
   return StatusUnsafe();
  }
 }

 public M1RuntimeStatus Status() {
  lock(gate) { return StatusUnsafe(); }
 }

 public M1SessionBindingRecord PrepareSession(string openAiSession) {
  EnsureStarted();
  return bindings.Begin(openAiSession);
 }

 // Local trusted UI path only. Never expose this directly to the model.
 public M1SessionBindingRecord ConfirmBindingLocal(
  string bindingId,string expectedConversationId,string providerProfileId) {
  EnsureStarted();
  return bindings.ConfirmCandidateLocal(bindingId,expectedConversationId,providerProfileId);
 }

 public M1RuntimeHandoffResult Handoff(
  string openAiSession,string summary,string nextAction,string verification,string blockingState) {
  EnsureStarted();
  var binding=bindings.GetForSession(openAiSession);
  if(binding==null || binding.state!="bound") {
   if(binding==null)binding=bindings.Begin(openAiSession);
   return new M1RuntimeHandoffResult {
    state="BINDING_REQUIRED",
    binding_id=binding.binding_id,
    binding_state=binding.state
   };
  }

  string sessionKey=M1SessionBindingStore.SessionKey(openAiSession);
  var run=automation.GetActiveRunByClientSession(sessionKey);
  if(run==null) {
   run=automation.CreateRun(new AutomationTarget {
    provider="chatgpt",
    provider_profile_id=binding.provider_profile_id,
    conversation_id=binding.conversation_id
   },sessionKey);
  } else {
   if(run.target==null ||
      !String.Equals(run.target.provider,"chatgpt",StringComparison.OrdinalIgnoreCase) ||
      run.target.provider_profile_id!=binding.provider_profile_id ||
      run.target.conversation_id!=binding.conversation_id)
    throw new InvalidOperationException("active_run_binding_conflict");
  }

  var queued=automation.QueueHandoff(
   run.run_id,run.generation,summary,nextAction,verification,blockingState);

  if(!queued.blocked && !String.IsNullOrEmpty(queued.message_id))
   broker.Publish(run.run_id,run.generation,queued.message_id);

  return new M1RuntimeHandoffResult {
   state=queued.blocked?"BLOCKED":"HANDOFF_QUEUED",
   binding_id=binding.binding_id,
   binding_state=binding.state,
   run_id=queued.run_id,
   generation=queued.generation,
   seq=queued.seq,
   message_id=queued.message_id,
   handoff_state=queued.state,
   existing=queued.existing,
   blocked=queued.blocked,
   blocking_state=queued.blocking_state
  };
 }

 public AutomationCompleteResult Complete(string openAiSession,string verification) {
  EnsureStarted();
  string sessionKey=M1SessionBindingStore.SessionKey(openAiSession);
  var run=automation.GetActiveRunByClientSession(sessionKey);
  if(run==null)throw new InvalidOperationException("active_automation_run_not_found");
  return automation.Complete(run.run_id,run.generation,verification);
 }

 public AutomationRun ActiveRun(string openAiSession) {
  EnsureStarted();
  return automation.GetActiveRunByClientSession(M1SessionBindingStore.SessionKey(openAiSession));
 }

 public M1BrowserLaunchPlan ProviderLaunchPlan(string initialUrl) {
  EnsureStarted();
  return browser.Plan(initialUrl);
 }

 public M1BrowserEvent[] DrainBrowserEvents() {
  EnsureStarted();
  return broker.DrainEvents();
 }

 public void Dispose() {
  lock(gate) {
   if(broker!=null){broker.Dispose();broker=null;}
   started=false;
  }
 }

 void EnsureStarted() {
  lock(gate) {
   if(!started)Start();
  }
 }

 M1RuntimeStatus StatusUnsafe() {
  var snapshot=automation.Snapshot();
  bool runtime=false,companion=false;
  try { runtime=browser.Discover().Any(x=>x.companion_capable); } catch {}
  try { browser.ValidateCompanion();companion=true; } catch {}
  return new M1RuntimeStatus {
   started=started,
   broker_port=broker==null?0:broker.Port,
   recoverable_published=recovered,
   active_runs=snapshot.runs.Count(run=>run.status!="COMPLETED"&&run.status!="CANCELLED"&&run.status!="FAILED_TERMINAL"),
   pending_bindings=bindings.Pending().Length,
   browser_runtime_available=runtime,
   companion_ready=companion,
   state_root=root
  };
 }
}
