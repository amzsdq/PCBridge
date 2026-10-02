using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Windows.Forms;
using System.Drawing;

// All MCP filesystem access enters ScopeEngine. No shell/process/network tool is exposed.
// This is a capability boundary for this server, not a sandbox against another process
// already running as the same Windows user or an administrator.
public sealed class Grant { public string path; public string mode; }
public sealed class WorkPermit {
 public string id, secretHash, name, reason, state="pending", extensionReason="";
 public DateTime expiresUtc=DateTime.UtcNow.AddHours(2);
 public List<Grant> grants=new List<Grant>(), pending=new List<Grant>();
 public string extensionPolicy="ask";
}
public sealed class RecycleEntry {
 public string id,taskId,original,stored,reason,purpose,recommendation,sha256;
 public string state="prepared",createdUtc=DateTime.UtcNow.ToString("o");
 public bool directory;
}
public sealed class ScopeDatabase {
 public List<WorkPermit> tasks=new List<WorkPermit>();
 public List<RecycleEntry> recycle=new List<RecycleEntry>();
}
public sealed class ManagedWork {
 public string id,taskId,kind,source,destination,state="queued",message="",sha256="";
 public long bytes,total;
 public DateTime startedUtc=DateTime.UtcNow,deadlineUtc;
 public bool cancel;
}

static class ScopeStore {
 public static string Base { get { return Path.Combine(Core.Root,"permissions"); } }
 public static string Db { get { return Path.Combine(Base,"permissions.dpapi"); } }
 public static string AuditPath { get {return Path.Combine(Base,"audit.jsonl");} }
 public static readonly object InProcess=new object();
 public static T Locked<T>(Func<T> operation) {
  lock(InProcess) using(var m=new Mutex(false,"Local\\"+Core.Instance+"-permissions")) {
   bool owned=false;
   try {try{owned=m.WaitOne(15000);}catch(AbandonedMutexException){owned=true;}
    if(!owned)throw new Exception("Permission store busy. Retry later.");return operation();
   } finally {if(owned)m.ReleaseMutex();}
  }
 }
 public static void Init(){Directory.CreateDirectory(Base);Directory.CreateDirectory(Path.Combine(Base,"recycle"));Directory.CreateDirectory(Path.Combine(Base,"revisions"));Directory.CreateDirectory(Path.Combine(Base,"jobs"));if(!File.Exists(Db))Save(new ScopeDatabase());}
 public static ScopeDatabase Load(){if(!File.Exists(Db))throw new Exception("Permission store unavailable.");byte[] b=ProtectedData.Unprotect(File.ReadAllBytes(Db),null,DataProtectionScope.CurrentUser);try{return Core.Json().Deserialize<ScopeDatabase>(Encoding.UTF8.GetString(b));}finally{Array.Clear(b,0,b.Length);}}
 public static void Save(ScopeDatabase db){byte[] raw=Encoding.UTF8.GetBytes(Core.Json().Serialize(db));try{Core.Atomic(Db,ProtectedData.Protect(raw,null,DataProtectionScope.CurrentUser));}finally{Array.Clear(raw,0,raw.Length);}}
 public static void Audit(string action,string task,string path,object detail){
  string line=Core.Json().Serialize(new {utc=DateTime.UtcNow.ToString("o"),action=action,task_id=task,path=path,detail=detail})+"\n";
  using(var f=new FileStream(AuditPath,FileMode.Append,FileAccess.Write,FileShare.Read)) {var b=Encoding.UTF8.GetBytes(line);f.Write(b,0,b.Length);f.Flush(true);}
 }
 public static string Digest(string value){using(var h=SHA256.Create())return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(value)));}
 public static bool FixedEqual(string a,string b){if(a==null||b==null||a.Length!=b.Length)return false;int d=0;for(int i=0;i<a.Length;i++)d|=a[i]^b[i];return d==0;}
 public static bool Live(WorkPermit t){return t.state=="active"&&DateTime.UtcNow<t.expiresUtc;}
 public static WorkPermit Authenticate(ScopeDatabase db,string token,bool live){var hash=Digest(token??"");var t=db.tasks.FirstOrDefault(x=>FixedEqual(x.secretHash,hash));if(t==null)throw new Exception("Invalid task capability.");if(live&&!Live(t))throw new Exception("Task is pending, closed, revoked, or expired.");return t;}
 public static int Rank(string mode){switch(mode){case "read":return 1;case "write":return 2;case "recycle":return 3;default:throw new Exception("Access must be read, write, or recycle.");}}
 public static bool Within(string p,string root){return String.Equals(p,root,StringComparison.OrdinalIgnoreCase)||p.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}
 public static bool Overlap(string a,string b){return Within(a,b)||Within(b,a);}
 public static string Check(ScopeDatabase db,WorkPermit t,string raw,int required){string p=SafePaths.Canonical(raw);if(!Live(t))throw new Exception("Task inactive.");if(!t.grants.Any(g=>Rank(g.mode)>=required&&Within(p,g.path)))throw new Exception("Outside this task's approved scope. Request an extension first.");return p;}
 public static void NoConflict(ScopeDatabase db,WorkPermit task,IEnumerable<Grant> candidate){foreach(var other in db.tasks.Where(t=>t.id!=task.id&&Live(t)))foreach(var a in candidate)foreach(var b in other.grants)if(Overlap(a.path,b.path)&&(Rank(a.mode)>1||Rank(b.mode)>1))throw new Exception("Another active task owns an overlapping writable path. Close/revoke that task first.");}
 // Called only by local UI, never by an MCP method.
 public static void Approve(string id,string policy,int hours){Locked(()=>{var db=Load();var t=db.tasks.Single(x=>x.id==id);if(policy!="ask"&&policy!="log")throw new Exception("Invalid expansion policy");if(t.state!="pending"&&!Live(t))throw new Exception("Task is no longer eligible.");foreach(var g in t.pending)SafePaths.Canonical(g.path);NoConflict(db,t,t.grants.Concat(t.pending));Audit("local_approval",id,"",new{scopes=t.pending,extension_policy=policy,hours=hours});t.grants.AddRange(t.pending);t.pending.Clear();t.state="active";t.extensionPolicy=policy;t.expiresUtc=DateTime.UtcNow.AddHours(Math.Max(1,Math.Min(24,hours)));Save(db);return 0;});}
 public static void Revoke(string id){Locked(()=>{var db=Load();var t=db.tasks.Single(x=>x.id==id);Audit("local_revoke",id,"",null);t.state="revoked";t.pending.Clear();Save(db);return 0;});}
 public static void Restore(string id){Locked(()=>{var db=Load();var r=db.recycle.Single(x=>x.id==id);if(r.state!="recycled"&&r.state!="recovery_required")throw new Exception("Not available for restore.");if(File.Exists(r.original)||Directory.Exists(r.original))throw new Exception("Original path already exists; restore never overwrites.");
   var path=SafePaths.Canonical(r.original);using(var pins=SafePaths.PinParents(path)){
    if(!r.directory&&Core.Hash(r.stored)!=r.sha256)throw new Exception("Recovery copy hash changed; restore stopped.");
    Audit("local_restore_requested",r.taskId,path,new{id=r.id});
    if(r.directory)Directory.Move(r.stored,path);else File.Move(r.stored,path);
    r.state="restored";Save(db);Audit("local_restored",r.taskId,path,new{id=r.id});
   }return 0;});}
}

static class SafePaths {
 const uint READ=0x80000000,WRITE=0x40000000,OPEN=3,NEW=1,REPARSE=0x00200000,BACKUP=0x02000000;
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string p,uint access,uint share,IntPtr sa,uint creation,uint flags,IntPtr tpl);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle h,StringBuilder s,uint n,uint flags);
 [StructLayout(LayoutKind.Sequential)]struct Info{public uint attributes;public System.Runtime.InteropServices.ComTypes.FILETIME created,accessed,written;public uint volume,high,low,links,indexHigh,indexLow;}
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle h,out Info i);
 public static string Canonical(string value){
  if(String.IsNullOrWhiteSpace(value)||!System.Text.RegularExpressions.Regex.IsMatch(value,@"\A[A-Za-z]:\\"))throw new Exception("Use an absolute local drive path. UNC/device/relative paths are denied.");
  if(value.IndexOf(':',2)>=0||value.Contains("/")||value.IndexOfAny(new[]{'\0','*','?'})>=0)throw new Exception("Alternate streams, wildcards and ambiguous paths are denied.");
  foreach(var part in value.Substring(3).Split('\\')) {if(part=="."||part==".."||part.EndsWith(".")||part.EndsWith(" "))throw new Exception("Ambiguous path component.");if(System.Text.RegularExpressions.Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new Exception("Device name denied.");}
  string p=Path.GetFullPath(value).TrimEnd('\\');if(p.Length==2)p+="\\";
  var drive=new DriveInfo(Path.GetPathRoot(p));if(drive.DriveType!=DriveType.Fixed)throw new Exception("Only local fixed drives are supported in this preview.");
  var forbidden=new[]{Core.Root,Core.Self,Environment.GetFolderPath(Environment.SpecialFolder.Windows),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)};
  foreach(var root in forbidden.Where(x=>!String.IsNullOrEmpty(x)))if(ScopeStore.Within(p,Path.GetFullPath(root).TrimEnd('\\')))throw new Exception("Protected application/system/credential location denied.");
  foreach(var part in p.Substring(3).Split('\\'))if(part.StartsWith(".")||new[]{"AppData","WindowsPowerShell","PowerShell","$Recycle.Bin","System Volume Information"}.Contains(part,StringComparer.OrdinalIgnoreCase))throw new Exception("Hidden, credential, profile, and system locations are excluded.");
  string cursor=p;while(!String.IsNullOrEmpty(cursor)){if(File.Exists(cursor)||Directory.Exists(cursor)){var a=File.GetAttributes(cursor);if((a&FileAttributes.ReparsePoint)!=0||(cursor!=Path.GetPathRoot(cursor)&&(a&(FileAttributes.System|FileAttributes.Hidden))!=0))throw new Exception("Links/hidden/system paths denied.");}cursor=Path.GetDirectoryName(cursor);}
  return p;
 }
 static void VerifyHandle(SafeFileHandle h,string expected,bool file){
  if(h.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());Info info;if(!GetFileInformationByHandle(h,out info))throw new Exception("Cannot verify file handle.");if((info.attributes&(uint)FileAttributes.ReparsePoint)!=0||file&&info.links!=1)throw new Exception("Reparse points and multiply-linked files are denied.");
  var b=new StringBuilder(32768);uint n=GetFinalPathNameByHandle(h,b,(uint)b.Capacity,0);if(n==0||n>=b.Capacity)throw new Exception("Cannot resolve opened path.");string actual=b.ToString();if(actual.StartsWith(@"\\?\"))actual=actual.Substring(4);if(!String.Equals(actual.TrimEnd('\\'),expected.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new Exception("Opened path differs from approved path.");
 }
 public sealed class Pins:IDisposable{public List<SafeFileHandle> handles=new List<SafeFileHandle>();public void Dispose(){foreach(var h in handles)h.Dispose();}}
 public static Pins PinParents(string path){var pins=new Pins();try{var dirs=new Stack<string>();string d=Path.GetDirectoryName(path);while(d!=null){dirs.Push(d);d=Path.GetDirectoryName(d);}foreach(var dir in dirs){var h=CreateFile(dir,0,3,IntPtr.Zero,OPEN,BACKUP|REPARSE,IntPtr.Zero);pins.handles.Add(h);VerifyHandle(h,dir,false);}return pins;}catch{pins.Dispose();throw;}}
 public static FileStream Open(string path,bool write,bool create){var h=CreateFile(path,READ|(write?WRITE:0),1,IntPtr.Zero,create?NEW:OPEN,REPARSE,IntPtr.Zero);try{VerifyHandle(h,path,true);return new FileStream(h,write?FileAccess.ReadWrite:FileAccess.Read);}catch{h.Dispose();throw;}}
 public static void Tree(string root){Canonical(root);foreach(var entry in Directory.GetFileSystemEntries(root)){Canonical(entry);if(Directory.Exists(entry))Tree(entry);else using(var pins=PinParents(entry))using(var f=Open(entry,false,false)){} }}
}

static class ScopeEngine {
 static readonly Dictionary<string,ManagedWork> Jobs=new Dictionary<string,ManagedWork>();
 public static string S(IDictionary<string,object> a,string name,string fallback=""){object v;return a.TryGetValue(name,out v)&&v!=null?Convert.ToString(v):fallback;}
 static int N(IDictionary<string,object> a,string name,int fallback,int low,int high){int n;return Int32.TryParse(S(a,name),out n)?Math.Max(low,Math.Min(high,n)):fallback;}
 static bool B(IDictionary<string,object> a,string name){return S(a,name).Equals("true",StringComparison.OrdinalIgnoreCase);}
 static string Required(IDictionary<string,object> a,string name,int max){string s=S(a,name);if(String.IsNullOrWhiteSpace(s)||s.Length>max)throw new Exception(name+" is required (maximum "+max+" characters).");return s;}
 static List<Grant> Grants(IDictionary<string,object> a){object value;if(!a.TryGetValue("scopes",out value)||!(value is IList))throw new Exception("scopes array required");var result=new List<Grant>();foreach(var item in (IList)value){var g=item as IDictionary<string,object>;if(g==null)throw new Exception("Invalid scope");string p=SafePaths.Canonical(Required(g,"path",2000)),mode=Required(g,"mode",20);ScopeStore.Rank(mode);if(p==Path.GetPathRoot(p)||String.Equals(p,Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),StringComparison.OrdinalIgnoreCase))throw new Exception("Choose a task folder or file, not a whole drive or user profile.");result.Add(new Grant{path=p,mode=mode});}if(result.Count<1||result.Count>12)throw new Exception("Choose 1 to 12 scopes.");return result;}
 static object TaskView(WorkPermit t){return new{task_id=t.id,name=t.name,state=t.state=="active"&&!ScopeStore.Live(t)?"expired":t.state,expires_utc=t.expiresUtc,scopes=t.grants,pending_scopes=t.pending,extension_policy=t.extensionPolicy};}
 public static object Call(string name,IDictionary<string,object> a){return ScopeStore.Locked(()=>CallLocked(name,a));}
 static object CallLocked(string name,IDictionary<string,object> a){
  if(name=="bridge_status"){ Core.Atomic(Path.Combine(Core.Root,"last-status-check.json"),Encoding.UTF8.GetBytes(Core.Json().Serialize(new{version=Core.Version,utc=DateTime.UtcNow}))); return new{ok=true,version=Core.Version,connection_ready=Core.State().Ready,cutover_state=File.Exists(Path.Combine(Core.Root,"cutover-state.json"))?Core.Json().DeserializeObject(File.ReadAllText(Path.Combine(Core.Root,"cutover-state.json"))):null,autostart=Core.Auto(),legacy_autostart=Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run","PCBridge-Portable",null)!=null,mode="non-hardlock-auto",desktop_commander="0.2.51",extended_execution="non-HARDLOCK automatic; HARDLOCK one-shot; explicit leases retained for compatibility; built-in one-shot chat relay available",shell_enabled=false,task_approval="local UI",permanent_delete=false,recycle="private recovery bin",long_jobs=new[]{"hash_file","copy_file"},root=Core.Root};}
  if(name=="discover_paths")return Discover(a);
  var db=ScopeStore.Load();
  if(name=="request_task"){
   if(db.tasks.Count(x=>x.state=="pending"||ScopeStore.Live(x))>=64)throw new Exception("Too many open requests. Close unused tasks.");
   string token;using(var rng=RandomNumberGenerator.Create()){var b=new byte[32];rng.GetBytes(b);token=Convert.ToBase64String(b);}
   var t=new WorkPermit{id=Guid.NewGuid().ToString("N"),secretHash=ScopeStore.Digest(token),name=Required(a,"name",120),reason=Required(a,"reason",1000),pending=Grants(a)};
   ScopeStore.Audit("task_requested",t.id,"",new{name=t.name,reason=t.reason,scopes=t.pending});db.tasks.Add(t);ScopeStore.Save(db);
   return new{task_token=token,task=TaskView(t),next="User must approve in PCBridge > 작업 권한. Do not ask another chat to share its token."};
  }
  var task=ScopeStore.Authenticate(db,Required(a,"task_token",200),false);
  if(name=="task_status")return TaskView(task);
  if(name=="close_task"){ScopeStore.Audit("task_closed",task.id,"",null);task.state="closed";task.pending.Clear();ScopeStore.Save(db);return TaskView(task);}
  if(!ScopeStore.Live(task)&&name!="job_status"&&name!="cancel_job")throw new Exception("Task not active. Ask the user to approve or create a new task.");
  if(name=="extend_task"){
   var proposed=Grants(a);string why=Required(a,"reason",1000);ScopeStore.NoConflict(db,task,proposed);
   if(task.extensionPolicy=="log"){ScopeStore.Audit("scope_auto_extended",task.id,"",new{reason=why,scopes=proposed,policy="user-selected log mode"});task.grants.AddRange(proposed);}
   else {ScopeStore.Audit("scope_extension_requested",task.id,"",new{reason=why,scopes=proposed});task.pending=proposed;task.extensionReason=why;}
   ScopeStore.Save(db);return TaskView(task);
  }
  if(name=="job_status"||name=="cancel_job"){
   string id=Required(a,"job_id",40);ManagedWork job;if(!Jobs.TryGetValue(id,out job)||job.taskId!=task.id)throw new Exception("Job not available for this task/server instance.");
   if(name=="cancel_job"){job.cancel=true;ScopeStore.Audit("job_cancel_requested",task.id,job.source,new{job_id=id});}
   return job;
  }
  if(name=="start_job")return StartJob(db,task,a);
  string path=ScopeStore.Check(db,task,Required(a,"path",2000),name=="write_text_file"?2:name=="recycle_path"?3:1);
  using(var pins=SafePaths.PinParents(path)){
   if(name=="list_directory")return List(path,N(a,"offset",0,0,1000000));
   if(name=="read_text_file"){
    using(var f=SafePaths.Open(path,false,false))using(var r=new StreamReader(f,new UTF8Encoding(false,true),true,4096)){
     int offset=N(a,"offset",0,0,10000000),count=N(a,"count",50000,1,50000);var skip=new char[4096];int remaining=offset;while(remaining>0){int got=r.Read(skip,0,Math.Min(skip.Length,remaining));if(got==0)break;remaining-=got;}
     var b=new char[count];int read=0;while(read<count){int n=r.Read(b,read,count-read);if(n==0)break;read+=n;}bool more=r.Peek()!=-1;
     ScopeStore.Audit("read_text",task.id,path,new{offset=offset,count=read});return new{content=new string(b,0,read),next_offset=offset+read,has_more=more};
    }
   }
   if(name=="write_text_file")return Write(task,path,a);
   if(name=="recycle_path")return Recycle(db,task,path,a);
  }
  throw new Exception("Unknown tool. Arbitrary shell and permanent deletion are not available.");
 }
 static object Discover(IDictionary<string,object> a){
  string raw=S(a,"path");if(raw==""){var roots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads")}.Where(Directory.Exists).ToArray();ScopeStore.Audit("discovery_roots","","",null);return new{roots=roots,notice="Metadata only. File contents require a locally approved task. Supply a known local directory for metadata browsing."};}
  string path=SafePaths.Canonical(raw);using(var pins=SafePaths.PinParents(path)){ScopeStore.Audit("metadata_discovery","",path,null);return List(path,N(a,"offset",0,0,1000000));}
 }
 static object List(string path,int offset){
  if(!Directory.Exists(path))throw new Exception("Directory not found.");var rows=new List<object>();int index=0;bool more=false;
  foreach(var p in Directory.EnumerateFileSystemEntries(path).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)){
   try{SafePaths.Canonical(p);}catch{continue;}if(index++<offset)continue;if(rows.Count==100){more=true;break;}var fi=new FileInfo(p);rows.Add(new{path=p,directory=Directory.Exists(p),bytes=fi.Exists?(long?)fi.Length:null});
  }return new{entries=rows,next_offset=offset+rows.Count,has_more=more};
 }
 static object Write(WorkPermit task,string path,IDictionary<string,object> a){
  string content=S(a,"content");if(content.Length>50000)throw new Exception("Write chunks are limited to 50,000 characters; append subsequent chunks.");bool exists=File.Exists(path),append=B(a,"append");
  ScopeStore.Audit("write_requested",task.id,path,new{append=append,characters=content.Length});
  using(var file=SafePaths.Open(path,true,!exists)){
   string backup="",before="";
   if(exists){if(file.Length>128*1024*1024)throw new Exception("Use a smaller text file (128 MiB maximum).");using(var h=SHA256.Create())before=BitConverter.ToString(h.ComputeHash(file)).Replace("-","");if(!String.Equals(S(a,"expected_sha256"),before,StringComparison.OrdinalIgnoreCase))throw new Exception("Existing file hash required/mismatched. Use hash_file job and retry with expected_sha256.");file.Position=0;backup=Path.Combine(ScopeStore.Base,"revisions",Guid.NewGuid().ToString("N")+".bin");using(var b=new FileStream(backup,FileMode.CreateNew,FileAccess.Write)){file.CopyTo(b);b.Flush(true);}ScopeStore.Audit("revision_saved",task.id,path,new{backup=backup,sha256=before});}
   var bytes=new UTF8Encoding(false).GetBytes(content);
   try{file.Position=append?file.Length:0;if(!append)file.SetLength(0);file.Write(bytes,0,bytes.Length);file.Flush(true);}
   catch{if(exists){file.Position=0;file.SetLength(0);using(var b=File.OpenRead(backup))b.CopyTo(file);file.Flush(true);}throw;}
   file.Position=0;string hash;using(var h=SHA256.Create())hash=BitConverter.ToString(h.ComputeHash(file)).Replace("-","");ScopeStore.Audit("write_completed",task.id,path,new{bytes=file.Length,sha256=hash});return new{ok=true,sha256=hash,revision_saved=exists};
  }
 }
 static object Recycle(ScopeDatabase db,WorkPermit task,string path,IDictionary<string,object> a){
  string reason=Required(a,"reason",1000),purpose=Required(a,"purpose",1000),rating=Required(a,"recommendation",30);if(!new[]{"keep","review","recommended"}.Contains(rating))throw new Exception("Recommendation must be keep, review, or recommended; it is only AI opinion.");
  bool dir=Directory.Exists(path);if(!dir&&!File.Exists(path))throw new Exception("Path not found.");
  // Directory moves across volumes cannot be made atomic. Fail without deleting anything.
  if(!String.Equals(Path.GetPathRoot(path),Path.GetPathRoot(ScopeStore.Base),StringComparison.OrdinalIgnoreCase))throw new Exception("This preview recycles only on the app-data drive. Cross-drive recycling is not implemented; nothing was moved.");
  if(dir)SafePaths.Tree(path);
  string hash="";if(!dir)using(var f=SafePaths.Open(path,false,false))using(var h=SHA256.Create())hash=BitConverter.ToString(h.ComputeHash(f)).Replace("-","");
  var r=new RecycleEntry{id=Guid.NewGuid().ToString("N"),taskId=task.id,original=path,directory=dir,reason=reason,purpose=purpose,recommendation=rating,sha256=hash};r.stored=Path.Combine(ScopeStore.Base,"recycle",r.id);
  db.recycle.Add(r);ScopeStore.Save(db);ScopeStore.Audit("recycle_requested",task.id,path,new{id=r.id,reason=reason,purpose=purpose,recommendation=rating,interpretation="AI opinion; user decides permanent deletion"});
  try{if(dir)Directory.Move(path,r.stored);else File.Move(path,r.stored);r.state="recycled";ScopeStore.Save(db);ScopeStore.Audit("recycled",task.id,path,new{id=r.id});}
  catch{r.state=(File.Exists(r.stored)||Directory.Exists(r.stored))?"recovery_required":"failed";ScopeStore.Save(db);throw;}
  return new{ok=true,recycle_id=r.id,permanent_delete=false,restore="PCBridge > 복구함 > 선택 항목 복원",recommendation=rating};
 }
 static object StartJob(ScopeDatabase db,WorkPermit task,IDictionary<string,object> a){
  string kind=Required(a,"kind",40);if(kind!="hash_file"&&kind!="copy_file")throw new Exception("Only hash_file and copy_file are supported. Shell commands require a separate OS sandbox and are not accepted.");
  if(Jobs.Values.Count(existing=>existing.state=="queued"||existing.state=="running")>=2)throw new Exception("Two jobs are already active. Query/cancel them before starting more.");
  if(Jobs.Values.Any(existing=>existing.taskId==task.id&&(existing.state=="queued"||existing.state=="running")))throw new Exception("One running job per task; use status/cancel.");
  string src=ScopeStore.Check(db,task,Required(a,"source",2000),1),dest=kind=="copy_file"?ScopeStore.Check(db,task,Required(a,"destination",2000),2):"";
  if(!File.Exists(src))throw new Exception("Source file missing.");if(dest!=""&&(File.Exists(dest)||Directory.Exists(dest)))throw new Exception("Copy destination must not exist.");
  var j=new ManagedWork{id=Guid.NewGuid().ToString("N"),taskId=task.id,kind=kind,source=src,destination=dest,total=new FileInfo(src).Length,deadlineUtc=DateTime.UtcNow.AddMinutes(N(a,"max_minutes",60,1,1440))};
  ScopeStore.Audit("job_started",task.id,src,new{job_id=j.id,kind=kind,destination=dest,deadline=j.deadlineUtc});Jobs.Add(j.id,j);PersistJob(j);Task.Run(()=>RunJob(j));return new{job_id=j.id,state=j.state,query="job_status",note="The job has its own deadline; tool calls do not wait for completion."};
 }
 static void PersistJob(ManagedWork j){Core.Atomic(Path.Combine(ScopeStore.Base,"jobs",j.id+".json"),Encoding.UTF8.GetBytes(Core.Json().Serialize(j)));}
 static void CheckJob(ManagedWork j){if(Core.Instance.StartsWith("PCBridge-Integrated-Test-")){int delay;if(Int32.TryParse(Environment.GetEnvironmentVariable("PCBRIDGE_TEST_CHUNK_DELAY_MS"),out delay)&&delay>0)Thread.Sleep(Math.Min(delay,1200));}ScopeStore.Locked(()=>{var t=ScopeStore.Load().tasks.Single(x=>x.id==j.taskId);if(j.cancel||!ScopeStore.Live(t))throw new OperationCanceledException("Cancelled or task expired/revoked.");if(DateTime.UtcNow>=j.deadlineUtc)throw new TimeoutException("Job deadline reached.");return 0;});}
 static void RunJob(ManagedWork j){
  try{
   ScopeStore.Locked(()=>{j.state="running";PersistJob(j);return 0;});CheckJob(j);
   using(var sourcePins=SafePaths.PinParents(j.source))using(var source=SafePaths.Open(j.source,false,false))using(var hash=SHA256.Create()){
    if(j.kind=="hash_file"){var buffer=new byte[1024*1024];int n;while((n=source.Read(buffer,0,buffer.Length))>0){CheckJob(j);hash.TransformBlock(buffer,0,n,buffer,0);ScopeStore.Locked(()=>{j.bytes+=n;return 0;});}hash.TransformFinalBlock(new byte[0],0,0);}
    else using(var destPins=SafePaths.PinParents(j.destination))using(var dest=SafePaths.Open(j.destination,true,true)){
     var buffer=new byte[1024*1024];int n;while((n=source.Read(buffer,0,buffer.Length))>0){CheckJob(j);dest.Write(buffer,0,n);hash.TransformBlock(buffer,0,n,buffer,0);ScopeStore.Locked(()=>{j.bytes+=n;return 0;});}dest.Flush(true);hash.TransformFinalBlock(new byte[0],0,0);
    }
    CheckJob(j);ScopeStore.Locked(()=>{j.sha256=BitConverter.ToString(hash.Hash).Replace("-","");j.state="completed";return 0;});
   }
  }catch(OperationCanceledException){ScopeStore.Locked(()=>{j.state="cancelled";j.message="Stopped. Any partial destination is preserved, never permanently deleted.";return 0;});}
  catch(Exception e){ScopeStore.Locked(()=>{j.state="failed";j.message=e is TimeoutException?"Job deadline exceeded; partial destination preserved.":"Job failed; inspect paths/permissions. Partial destination preserved.";return 0;});}
  finally{ScopeStore.Locked(()=>{PersistJob(j);ScopeStore.Audit("job_finished",j.taskId,j.source,new{job_id=j.id,state=j.state,bytes=j.bytes,message=j.message});return 0;});}
 }
 public static void ServerStart(){ScopeStore.Locked(()=>{ScopeStore.Init();foreach(var p in Directory.GetFiles(Path.Combine(ScopeStore.Base,"jobs"),"*.json")){var j=Core.Json().Deserialize<ManagedWork>(File.ReadAllText(p));if(j.state=="queued"||j.state=="running"){j.state="interrupted";j.message="Server restarted; this job is not running. Partial output may remain.";PersistJob(j);}Jobs[j.id]=j;}return 0;});}
 static object Prop(string type,string description){return new{type=type,description=description};}
 static object Tool(string name,string description,Dictionary<string,object> properties,string[] required){return new{name=name,description=description,inputSchema=new{type="object",properties=properties,required=required,additionalProperties=false}};}
 public static object[] Tools(){var tools=new List<object>();
  tools.Add(Tool("bridge_status","Show this task-scoped server status. Arbitrary PowerShell is intentionally unavailable.",new Dictionary<string,object>(),new string[0]));
  tools.Add(Tool("discover_paths","Read directory metadata only, to identify narrow task paths. Never returns file contents. Hidden/system/credential locations are excluded. Discovery is logged.",new Dictionary<string,object>{{"path",Prop("string","Optional absolute local directory")},{"offset",Prop("integer","Pagination offset")}},new string[0]));
  var scopes=new {type="array",minItems=1,maxItems=12,items=new{type="object",properties=new Dictionary<string,object>{{"path",Prop("string","Exact file or directory subtree")},{"mode",new{type="string",@enum=new[]{"read","write","recycle"}}}},required=new[]{"path","mode"},additionalProperties=false}};
  tools.Add(Tool("request_task","Request per-task permissions; a HUMAN must approve in the local manager. Keep the returned capability private to this chat. Start with least privilege; read < write < recycle. No task may share overlapping writable scope.",new Dictionary<string,object>{{"name",Prop("string","Task title")},{"reason",Prop("string","Why these locations and permissions are needed")},{"scopes",scopes}},new[]{"name","reason","scopes"}));
  foreach(string name in new[]{"task_status","close_task"})tools.Add(Tool(name,name=="close_task"?"Revoke this task's access and cancel its jobs.":"Read approval, expiry and scope status for this task.",Token(),new[]{"task_token"}));
  var extend=Token();extend.Add("scopes",scopes);extend.Add("reason",Prop("string","Why additional paths/permissions are required"));tools.Add(Tool("extend_task","Request scope expansion. Local policy determines user approval versus logged automatic expansion. Never bypass denied access.",extend,new[]{"task_token","scopes","reason"}));
  var list=PathProps();list.Add("offset",Prop("integer","Pagination offset"));tools.Add(Tool("list_directory","List approved folder entries (100 per page).",list,new[]{"task_token","path"}));
  var read=PathProps();read.Add("offset",Prop("integer","Character offset"));read.Add("count",Prop("integer","Up to 50000 characters"));tools.Add(Tool("read_text_file","Read a chunk from a file in this task's scope.",read,new[]{"task_token","path"}));
  var write=PathProps();write.Add("content",Prop("string","Up to 50000 characters per chunk"));write.Add("append",Prop("boolean","Append instead of replace"));write.Add("expected_sha256",Prop("string","Required for an existing file; obtain via hash_file job. Prior bytes are kept in revisions."));tools.Add(Tool("write_text_file","Write approved text, preserving prior bytes before overwrite/append. Cannot write credentials, startup profiles or this app's control files.",write,new[]{"task_token","path","content"}));
  var rec=PathProps();rec.Add("reason",Prop("string","Why removal is requested"));rec.Add("purpose",Prop("string","What this file/folder was used for"));rec.Add("recommendation",new{type="string",@enum=new[]{"keep","review","recommended"}});tools.Add(Tool("recycle_path","Move an approved path into PCBridge private recovery bin, with reasons. No permanent delete. This preview supports recycling only on the app-data drive. Recommendation is AI opinion, not proof that deletion is safe.",rec,new[]{"task_token","path","reason","purpose","recommendation"}));
  var job=Token();job.Add("kind",new{type="string",@enum=new[]{"hash_file","copy_file"}});job.Add("source",Prop("string","Approved source file"));job.Add("destination",Prop("string","New destination for copy_file"));job.Add("max_minutes",Prop("integer","Job deadline 1 to 1440 minutes; default 60"));tools.Add(Tool("start_job","Start a managed file job and return immediately. Query job_status. No PowerShell or arbitrary executable commands. At most 2 jobs total, 1 per task; no 30-second execution cutoff.",job,new[]{"task_token","kind","source"}));
  foreach(string name in new[]{"job_status","cancel_job"}){var props=Token();props.Add("job_id",Prop("string","Returned job ID"));tools.Add(Tool(name,name=="job_status"?"Read job status/progress/result.":"Request cancellation; partial output is retained.",props,new[]{"task_token","job_id"}));}
  return tools.Concat(DesktopIntegration.Tools()).Concat(M1McpIntegration.Tools()).ToArray();
 }
 static Dictionary<string,object> Token(){return new Dictionary<string,object>{{"task_token",Prop("string","Private task capability returned by request_task")}};}
 static Dictionary<string,object> PathProps(){var p=Token();p.Add("path",Prop("string","Absolute local path inside this task's scope"));return p;}
 public static int Serve(bool probe=false){
  using(var mutex=new Mutex(false,"Local\\"+Core.Instance+(probe?"-probe-server":"-scope-server"))){
   if(!mutex.WaitOne(0))return 4;try{if(!probe){ServerStart();DesktopIntegration.Start();}using(var reader=new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false),true))using(var writer=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true}){
    string line;while((line=reader.ReadLine())!=null){object id=null;try{
     if(line.Length>1000000)throw new Exception("Request too large");var req=Core.Json().Deserialize<Dictionary<string,object>>(line);if(!req.TryGetValue("id",out id))continue;string method=S(req,"method");object result;
     if(method=="initialize")result=new{protocolVersion="2024-11-05",capabilities=new{tools=new{listChanged=false}},serverInfo=new{name="PCBridge Integrated Preview",version=Core.Version},instructions="Identify paths with metadata discovery, request least-privilege task, wait for human local approval, pass task_token with every operation. Close task when finished. Never share capabilities across chats. Default scoped tools do not expose arbitrary shell or permanent deletion. Bundled Desktop Commander auto-runs non-HARDLOCK work by default; HARDLOCK operations still require one-shot local approval. Explicit restricted leases remain available for compatibility. Use desktop_relay for one-shot continuation messages to the bound ChatGPT conversation instead of ad-hoc SendKeys/PowerShell. Large results are paged from local DPAPI storage. Desktop process execution still runs with Windows user rights and is policy-constrained rather than an OS sandbox."};
     else if(method=="ping")result=new{};
     else if(method=="tools/list")result=new{tools=Tools()};
     else if(method=="tools/call") {var par=(Dictionary<string,object>)req["params"];var args=par.ContainsKey("arguments")?(Dictionary<string,object>)par["arguments"]:new Dictionary<string,object>();try{result=new{content=new[]{new{type="text",text=Core.Json().Serialize(probe?new{ok=true,mode="diagnostic-only"}:(object)(S(par,"name").StartsWith("desktop_")?DesktopIntegration.Call(S(par,"name"),args):S(par,"name").StartsWith("automation_")?M1McpIntegration.Call(S(par,"name"),args):Call(S(par,"name"),args)))}},isError=false};}catch(Exception e){result=new{content=new[]{new{type="text",text=e.Message}},isError=true};}}
     else {writer.WriteLine(Core.Json().Serialize(new{jsonrpc="2.0",id=id,error=new{code=-32601,message="Method not found"}}));continue;}
     writer.WriteLine(Core.Json().Serialize(new{jsonrpc="2.0",id=id,result=result}));
    }catch{writer.WriteLine(Core.Json().Serialize(new{jsonrpc="2.0",id=id,error=new{code=-32600,message="Invalid request"}}));}}
   }return 0;}finally{if(!probe){M1McpIntegration.Stop();DesktopIntegration.Stop();}mutex.ReleaseMutex();}
  }
 }
}

sealed class PermissionWindow:Form {
 ListBox tasks=new ListBox();TextBox detail=new TextBox();ComboBox policy=new ComboBox();NumericUpDown hours=new NumericUpDown();List<WorkPermit> snapshot=new List<WorkPermit>();System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
 public PermissionWindow(){Text="작업 권한 · PCBridge";Font=new Font("Malgun Gothic",10);ClientSize=new Size(880,590);StartPosition=FormStartPosition.CenterParent;
  Controls.Add(new Label{Text="AI가 요청한 경로와 권한을 확인하세요. 폴더는 하위 전체에 적용됩니다. 승인 전에는 파일 내용을 읽을 수 없습니다.",Bounds=new Rectangle(15,12,850,48)});
  tasks.SetBounds(15,65,285,420);detail.SetBounds(315,65,545,310);detail.Multiline=true;detail.ReadOnly=true;detail.ScrollBars=ScrollBars.Both;
  policy.DropDownStyle=ComboBoxStyle.DropDownList;policy.Items.AddRange(new object[]{"추가 접근: 매번 사용자 승인 (기본)","추가 접근: 기록 후 허용 (범위가 자동 확대됨)"});policy.SelectedIndex=0;policy.SetBounds(315,385,545,30);
  Controls.Add(new Label{Text="권한 유효 시간 (만료 후 접근 차단)",Bounds=new Rectangle(315,427,350,25)});hours.Minimum=1;hours.Maximum=24;hours.Value=2;hours.SetBounds(695,422,75,30);
  Button approve=new Button{Text="선택 작업 / 추가 범위 승인",Bounds=new Rectangle(315,465,310,36)},revoke=new Button{Text="권한 회수 / 거절",Bounds=new Rectangle(640,465,220,36)},refresh=new Button{Text="새로 고침",Bounds=new Rectangle(15,500,285,36)};
  Controls.Add(new Label{Text="read: 읽기 · write: 읽기/쓰기 · recycle: 읽기/쓰기/복구함 이동\r\n기록 후 허용은 최초 경로에 고정하는 모드가 아닙니다. AI 요청으로 추가 경로가 열립니다.",Bounds=new Rectangle(315,513,550,64)});
  Controls.AddRange(new Control[]{tasks,detail,policy,hours,approve,revoke,refresh});tasks.SelectedIndexChanged+=(a,b)=>ShowTask();refresh.Click+=(a,b)=>RefreshTasks();approve.Click+=(a,b)=>{if(policy.SelectedIndex==1&&MessageBox.Show(this,"이 작업은 AI가 추가 경로를 요청하면 별도 승인 없이 권한이 넓어집니다. 감사 로그는 남습니다. 허용하시겠습니까?","자동 범위 확대",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;Act(()=>ScopeStore.Approve(Selected().id,policy.SelectedIndex==1?"log":"ask",(int)hours.Value));};revoke.Click+=(a,b)=>Act(()=>ScopeStore.Revoke(Selected().id));
  timer.Interval=3000;timer.Tick+=(a,b)=>RefreshTasks();timer.Start();FormClosed+=(a,b)=>timer.Dispose();RefreshTasks();
 }
 WorkPermit Selected(){if(tasks.SelectedIndex<0)throw new Exception("작업을 선택하세요.");return snapshot[tasks.SelectedIndex];}
 void Act(Action a){try{a();RefreshTasks();}catch(Exception e){MessageBox.Show(this,e.Message);}}
 void RefreshTasks(){string selected=tasks.SelectedIndex>=0&&tasks.SelectedIndex<snapshot.Count?snapshot[tasks.SelectedIndex].id:null;try{snapshot=ScopeStore.Locked(()=>ScopeStore.Load().tasks.ToList());tasks.BeginUpdate();tasks.Items.Clear();foreach(var t in snapshot)tasks.Items.Add((t.state=="active"&&!ScopeStore.Live(t)?"expired":t.state)+" · "+t.name+(t.pending.Count>0?" [승인 대기]":""));int index=snapshot.FindIndex(t=>t.id==selected);tasks.SelectedIndex=index;tasks.EndUpdate();}catch{}}
 void ShowTask(){if(tasks.SelectedIndex<0){detail.Clear();return;}var t=Selected();detail.Text="[AI 제공 설명 — 경로를 직접 확인하세요]\r\n"+t.name+"\r\n"+t.reason+"\r\n\r\n현재 범위:\r\n"+String.Join("\r\n",t.grants.Select(g=>g.mode+" : "+g.path))+"\r\n\r\n요청 범위:\r\n"+String.Join("\r\n",t.pending.Select(g=>g.mode+" : "+g.path))+"\r\n추가 사유: "+t.extensionReason+"\r\n만료: "+t.expiresUtc.ToLocalTime()+"\r\n정책: "+t.extensionPolicy;}
}

sealed class RecoveryWindow:Form {
 ListBox list=new ListBox();TextBox detail=new TextBox();List<RecycleEntry> items;
 public RecoveryWindow(){Text="복구함 / 삭제 검토 기록 · PCBridge";Font=new Font("Malgun Gothic",10);ClientSize=new Size(850,550);StartPosition=FormStartPosition.CenterParent;
  Controls.Add(new Label{Text="Windows 휴지통과 별개인 PCBridge 복구함입니다. 자동 비우기·AI 완전 삭제 기능은 없습니다.",Bounds=new Rectangle(15,10,820,42)});list.SetBounds(15,60,310,385);detail.SetBounds(340,60,490,385);detail.Multiline=true;detail.ReadOnly=true;detail.ScrollBars=ScrollBars.Both;
  var restore=new Button{Text="선택 항목 원래 위치로 복원",Bounds=new Rectangle(340,465,285,36)};var logs=new Button{Text="감사 로그 열기",Bounds=new Rectangle(15,465,200,36)};Controls.AddRange(new Control[]{list,detail,restore,logs});list.SelectedIndexChanged+=(a,b)=>{if(list.SelectedIndex<0)return;var r=items[list.SelectedIndex];detail.Text="상태: "+r.state+"\r\n원래 위치: "+r.original+"\r\n복구 위치: "+r.stored+"\r\n시각: "+r.createdUtc+"\r\n작업: "+r.taskId+"\r\n\r\nAI가 적은 사유: "+r.reason+"\r\n용도: "+r.purpose+"\r\n삭제 추천: "+r.recommendation+"\r\n\r\n추천은 AI 의견이며 삭제 안전성을 보증하지 않습니다. 영구 삭제는 사용자가 직접 판단합니다.";};restore.Click+=(a,b)=>{try{if(list.SelectedIndex<0)return;ScopeStore.Restore(items[list.SelectedIndex].id);Reload();}catch(Exception e){MessageBox.Show(this,e.Message);}};logs.Click+=(a,b)=>{if(File.Exists(ScopeStore.AuditPath))System.Diagnostics.Process.Start("notepad.exe",Core.Q(ScopeStore.AuditPath));};Reload();
 }
 void Reload(){items=ScopeStore.Locked(()=>ScopeStore.Load().recycle.ToList());list.Items.Clear();foreach(var r in items)list.Items.Add(r.state+" · "+Path.GetFileName(r.original));detail.Clear();}
}