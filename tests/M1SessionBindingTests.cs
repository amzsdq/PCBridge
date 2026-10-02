using System;
using System.IO;
using System.Text;

static class M1SessionBindingTests {
 static int passed;
 static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed++;}
 static void Expect(Action action,string fragment,string name) {
  bool hit=false;
  try{action();}catch(Exception e){hit=e.Message.IndexOf(fragment,StringComparison.OrdinalIgnoreCase)>=0;}
  Check(hit,name);
 }

 public static int Main(string[] args) {
  string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"M1BindingTestArtifacts","run-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(root);
  try {
   const string session="anon-session-1234567890";
   const string conversation="12345678-abcd-4abc-8abc-123456789012";
   var store=new M1SessionBindingStore(root);

   var pending=store.Begin(session);
   Check(pending.state=="pending" && pending.binding_id.Length==32,"begin creates pending binding");
   var same=store.Begin(session);
   Check(same.binding_id==pending.binding_id,"begin is idempotent per ChatGPT session");
   Check(pending.client_session_key==M1SessionBindingStore.SessionKey(session),"raw session converted to deterministic key");
   Check(pending.client_session_key.IndexOf(session,StringComparison.Ordinal)<0,"raw ChatGPT session id not stored as key");

   var candidate=store.ProposeCandidate(pending.binding_id,conversation,"doc-1");
   Check(candidate.state=="candidate" && candidate.candidate_conversation_id==conversation,"browser candidate recorded but not auto-confirmed");
   Check(candidate.conversation_id=="","candidate is not authority");

   Expect(delegate {
    store.ProposeCandidate(pending.binding_id,"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","doc-2");
   },"candidate_conflict","different candidate fails closed");

   var bound=store.ConfirmCandidateLocal(pending.binding_id,conversation,"chatgpt-default");
   Check(bound.state=="bound" && bound.conversation_id==conversation && bound.provider_profile_id=="chatgpt-default","local confirmation creates exact target binding");
   var reopened=new M1SessionBindingStore(root).GetForSession(session);
   Check(reopened!=null && reopened.state=="bound" && reopened.conversation_id==conversation,"binding survives restart");

   byte[] raw=File.ReadAllBytes(store.StatePath);
   string accidental=Encoding.UTF8.GetString(raw);
   Check(accidental.IndexOf(session,StringComparison.Ordinal)<0 && accidental.IndexOf(conversation,StringComparison.Ordinal)<0,"binding state DPAPI protected");

   var again=store.ConfirmCandidateLocal(pending.binding_id,conversation,"chatgpt-default");
   Check(again.state=="bound","same local confirmation idempotent");
   Expect(delegate {
    store.ConfirmCandidateLocal(pending.binding_id,"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee","chatgpt-default");
   },"already_confirmed","confirmed binding cannot silently retarget");

   var revoked=store.RevokeLocal(pending.binding_id);
   Check(revoked.state=="revoked","binding revocation persists");
   Check(store.GetForSession(session)==null,"revoked binding no longer resolves");

   var fresh=store.Begin(session);
   Check(fresh.binding_id!=pending.binding_id && fresh.state=="pending","revoked session can create fresh binding");

   Console.WriteLine("M1 session binding tests PASS: "+passed);
   Console.WriteLine("state_root="+root);
   return 0;
  } catch(Exception e) {
   Console.Error.WriteLine(e.ToString());return 1;
  }
 }
}
