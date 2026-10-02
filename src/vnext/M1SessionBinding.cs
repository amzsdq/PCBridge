using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading;
using System.Web.Script.Serialization;

public sealed class M1SessionBindingRecord {
 public string binding_id="";
 public string client_session_key="";
 public string state="pending";
 public string provider="chatgpt";
 public string provider_profile_id="";
 public string conversation_id="";
 public string candidate_conversation_id="";
 public string candidate_document_epoch="";
 public string created_utc="";
 public string candidate_utc="";
 public string confirmed_utc="";
}

public sealed class M1SessionBindingState {
 public int schema_version=1;
 public List<M1SessionBindingRecord> bindings=new List<M1SessionBindingRecord>();
}

public sealed class M1SessionBindingStore {
 readonly string root;
 readonly string path;
 readonly string mutexName;
 static readonly Regex IdRx=new Regex(@"\A[A-Za-z0-9_-]{8,160}\z",RegexOptions.CultureInvariant);
 static readonly Regex ConversationRx=new Regex(@"\A[0-9a-fA-F-]{8,64}\z",RegexOptions.CultureInvariant);

 public M1SessionBindingStore(string rootPath) {
  if(String.IsNullOrWhiteSpace(rootPath))throw new ArgumentException("rootPath");
  root=Path.GetFullPath(rootPath);
  Directory.CreateDirectory(root);
  path=Path.Combine(root,"session-bindings.dpapi");
  mutexName="Local\\PCBridge-M1-Bind-"+Hash(root).Substring(0,24);
 }

 public string StatePath { get { return path; } }

 public static string SessionKey(string rawSession) {
  string value=(rawSession??"").Trim();
  if(value.Length<8 || value.Length>512 || value.IndexOfAny(new[]{'\r','\n','\0'})>=0)
   throw new ArgumentException("openai_session_invalid");
  return Hash(value);
 }

 public M1SessionBindingRecord Begin(string rawSession) {
  string key=SessionKey(rawSession);
  return Locked<M1SessionBindingRecord>(delegate {
   var state=LoadUnsafe();
   for(int i=state.bindings.Count-1;i>=0;i--) {
    var existing=state.bindings[i];
    if(existing.client_session_key==key &&
       (existing.state=="pending"||existing.state=="candidate"||existing.state=="bound"))
      return Clone(existing);
   }
   var row=new M1SessionBindingRecord {
    binding_id=Guid.NewGuid().ToString("N"),
    client_session_key=key,
    state="pending",
    created_utc=Utc()
   };
   state.bindings.Add(row);
   Trim(state);
   SaveUnsafe(state);
   return Clone(row);
  });
 }

 public M1SessionBindingRecord GetForSession(string rawSession) {
  string key=SessionKey(rawSession);
  return Locked<M1SessionBindingRecord>(delegate {
   var state=LoadUnsafe();
   for(int i=state.bindings.Count-1;i>=0;i--)
    if(state.bindings[i].client_session_key==key &&
       state.bindings[i].state!="revoked")
      return Clone(state.bindings[i]);
   return null;
  });
 }

 public M1SessionBindingRecord GetById(string bindingId) {
  return Locked<M1SessionBindingRecord>(delegate {
   var row=Find(LoadUnsafe(),bindingId);
   return Clone(row);
  });
 }

 public M1SessionBindingRecord ProposeCandidate(
  string bindingId,string conversationId,string documentEpoch) {
  return Locked<M1SessionBindingRecord>(delegate {
   var state=LoadUnsafe();
   var row=Find(state,bindingId);
   if(row.state=="bound") {
    if(row.conversation_id==ValidateConversation(conversationId))return Clone(row);
    throw new InvalidOperationException("binding_already_confirmed");
   }
   if(row.state!="pending" && row.state!="candidate")
    throw new InvalidOperationException("binding_not_candidate_eligible");
   string conversation=ValidateConversation(conversationId);
   string epoch=ValidateOpaque(documentEpoch,"document_epoch",200);
   if(row.state=="candidate" && row.candidate_conversation_id.Length>0 &&
      row.candidate_conversation_id!=conversation)
    throw new InvalidOperationException("binding_candidate_conflict");
   row.candidate_conversation_id=conversation;
   row.candidate_document_epoch=epoch;
   row.candidate_utc=Utc();
   row.state="candidate";
   SaveUnsafe(state);
   return Clone(row);
  });
 }

 // Local trusted UI only. Never expose this method directly as a model-callable MCP tool.
 public M1SessionBindingRecord ConfirmCandidateLocal(
  string bindingId,string expectedConversationId,string providerProfileId) {
  return Locked<M1SessionBindingRecord>(delegate {
   var state=LoadUnsafe();
   var row=Find(state,bindingId);
   if(row.state=="bound") {
    if(row.conversation_id==ValidateConversation(expectedConversationId) &&
       row.provider_profile_id==ValidateOpaque(providerProfileId,"provider_profile_id",160))
      return Clone(row);
    throw new InvalidOperationException("binding_already_confirmed");
   }
   if(row.state!="candidate")throw new InvalidOperationException("binding_candidate_required");
   string expected=ValidateConversation(expectedConversationId);
   if(row.candidate_conversation_id!=expected)throw new InvalidOperationException("binding_candidate_changed");
   row.provider_profile_id=ValidateOpaque(providerProfileId,"provider_profile_id",160);
   row.conversation_id=expected;
   row.state="bound";
   row.confirmed_utc=Utc();
   SaveUnsafe(state);
   return Clone(row);
  });
 }

 public M1SessionBindingRecord RevokeLocal(string bindingId) {
  return Locked<M1SessionBindingRecord>(delegate {
   var state=LoadUnsafe();
   var row=Find(state,bindingId);
   row.state="revoked";
   row.confirmed_utc="";
   SaveUnsafe(state);
   return Clone(row);
  });
 }

 public M1SessionBindingRecord[] Pending() {
  return Locked<M1SessionBindingRecord[]>(delegate {
   var state=LoadUnsafe();
   return state.bindings.FindAll(delegate(M1SessionBindingRecord row) {
    return row.state=="pending"||row.state=="candidate";
   }).ConvertAll(Clone).ToArray();
  });
 }

 T Locked<T>(Func<T> work) {
  using(var mutex=new Mutex(false,mutexName)) {
   bool entered=false;
   try {
    try { entered=mutex.WaitOne(TimeSpan.FromSeconds(20)); }
    catch(AbandonedMutexException) { entered=true; }
    if(!entered)throw new TimeoutException("session_binding_lock_timeout");
    return work();
   } finally { if(entered)mutex.ReleaseMutex(); }
  }
 }

 M1SessionBindingState LoadUnsafe() {
  if(!File.Exists(path))return new M1SessionBindingState();
  byte[] sealedBytes=File.ReadAllBytes(path),plain=null;
  try {
   plain=ProtectedData.Unprotect(sealedBytes,null,DataProtectionScope.CurrentUser);
   var state=Json().Deserialize<M1SessionBindingState>(Encoding.UTF8.GetString(plain));
   if(state==null||state.schema_version!=1||state.bindings==null)
    throw new InvalidDataException("session_binding_schema_invalid");
   return state;
  } catch(CryptographicException) {
   throw new InvalidDataException("session_binding_unreadable");
  } finally {
   if(plain!=null)Array.Clear(plain,0,plain.Length);
   Array.Clear(sealedBytes,0,sealedBytes.Length);
  }
 }

 void SaveUnsafe(M1SessionBindingState state) {
  byte[] plain=Encoding.UTF8.GetBytes(Json().Serialize(state)),sealedBytes=null;
  try {
   sealedBytes=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);
   Atomic(path,sealedBytes);
  } finally {
   Array.Clear(plain,0,plain.Length);
   if(sealedBytes!=null)Array.Clear(sealedBytes,0,sealedBytes.Length);
  }
 }

 static M1SessionBindingRecord Find(M1SessionBindingState state,string bindingId) {
  string id=ValidateOpaque(bindingId,"binding_id",80);
  var row=state.bindings.Find(delegate(M1SessionBindingRecord x){return x.binding_id==id;});
  if(row==null)throw new InvalidOperationException("binding_not_found");
  return row;
 }

 static string ValidateConversation(string value) {
  string v=(value??"").Trim();
  if(!ConversationRx.IsMatch(v))throw new ArgumentException("conversation_id_invalid");
  return v;
 }

 static string ValidateOpaque(string value,string name,int max) {
  string v=(value??"").Trim();
  if(v.Length<1||v.Length>max||v.IndexOfAny(new[]{'\r','\n','\0'})>=0)
   throw new ArgumentException(name+"_invalid");
  return v;
 }

 static void Trim(M1SessionBindingState state) {
  if(state.bindings.Count<=256)return;
  state.bindings.RemoveAll(delegate(M1SessionBindingRecord row){
   return row.state=="revoked";
  });
  while(state.bindings.Count>256)state.bindings.RemoveAt(0);
 }

 static M1SessionBindingRecord Clone(M1SessionBindingRecord row) {
  return Json().Deserialize<M1SessionBindingRecord>(Json().Serialize(row));
 }

 static JavaScriptSerializer Json() {
  return new JavaScriptSerializer{MaxJsonLength=1000000,RecursionLimit=64};
 }

 static void Atomic(string file,byte[] bytes) {
  string temp=file+".new";
  using(var f=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {
   f.Write(bytes,0,bytes.Length);f.Flush(true);
  }
  if(File.Exists(file))File.Replace(temp,file,null);else File.Move(temp,file);
 }

 static string Hash(string value) {
  byte[] bytes=Encoding.UTF8.GetBytes(value??"");
  try {
   using(var sha=SHA256.Create())
    return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
  } finally { Array.Clear(bytes,0,bytes.Length); }
 }

 static string Utc(){return DateTime.UtcNow.ToString("o");}
}
