using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

public sealed class DesktopRequest {
 public string id,tokenHash,tool,args,reason,state="pending",result="",error="",kind="call",leaseId="",candidateLeaseId="",candidateLeaseTokenHash="",scopeJson="",policyNote="",resultFile="";
 public DateTime createdUtc=DateTime.UtcNow;
 public int grade=4,resultLength=0;
 public DateTime expiresUtc=DateTime.UtcNow.AddMinutes(30);
}
public sealed class DesktopLease {
 public string id,tokenHash,name,reason,state="pending",scopeJson="{}",bootId="";
 public int grade=3;
 public DateTime expiresUtc=DateTime.UtcNow.AddHours(8);
}
public sealed class DesktopLeaseScope {
 public string[] tools=new string[0];
 public string[] paths=new string[0];
 public string[] command_prefixes=new string[0];
 public string[] process_names=new string[0];
 public bool network=false;
}
public sealed class DesktopRelayBinding {
 public long hwnd;
 public int pid;
 public string processName="",title="";
 public DateTime boundUtc=DateTime.UtcNow;
}
public sealed class DesktopRelayPayload {
 public string id="",message="",state="scheduled",error="";
 public int delaySeconds;
 public DateTime createdUtc=DateTime.UtcNow,dueUtc,sentUtc=DateTime.MinValue;
 public DesktopRelayBinding binding;
}
static class DesktopIntegration {
 static Process engine; static ChildJob childJob; static readonly object rpcLock=new object(); static int sequence;
 static System.Threading.Timer pump; static int pumping;
 [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
 [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd,int nCmdShow);
 [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
 [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd,out RelayRect rect);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd,out uint pid);
 [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach,uint idAttachTo,bool attach);
 [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
 [StructLayout(LayoutKind.Sequential)] struct RelayRect { public int Left,Top,Right,Bottom; }
 static string Dir {get{return Path.Combine(ScopeStore.Base,"desktop-requests");}}
 static string ResultDir {get{return Path.Combine(Dir,"results");}}
 static System.Web.Script.Serialization.JavaScriptSerializer StorageJson(){return new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=256000000};}
 static string Db {get{return Path.Combine(Dir,"requests.dpapi");}}
 static string LeaseDb {get{return Path.Combine(Dir,"leases.dpapi");}}
 static string RelayDir {get{return Path.Combine(Dir,"relay");}}
 static string RelayBindingDb {get{return Path.Combine(RelayDir,"binding.dpapi");}}
 static void LaunchApprovalPopup(string requestId){try{if(Core.Instance.StartsWith("PCBridge-Integrated-Test-"))return;Process.Start(new ProcessStartInfo(Core.Self,"--desktop-approval "+requestId){UseShellExecute=true});}catch(Exception e){try{ScopeStore.Audit("desktop_approval_popup_failed",requestId,"",new{error=e.Message});}catch{}}}
 static void RelaySaveBinding(DesktopRelayBinding b){Directory.CreateDirectory(RelayDir);var raw=Encoding.UTF8.GetBytes(StorageJson().Serialize(b));try{Core.Atomic(RelayBindingDb,ProtectedData.Protect(raw,null,DataProtectionScope.CurrentUser));}finally{Array.Clear(raw,0,raw.Length);}}
 static DesktopRelayBinding RelayLoadBinding(){if(!File.Exists(RelayBindingDb))return null;var raw=ProtectedData.Unprotect(File.ReadAllBytes(RelayBindingDb),null,DataProtectionScope.CurrentUser);try{return StorageJson().Deserialize<DesktopRelayBinding>(Encoding.UTF8.GetString(raw));}finally{Array.Clear(raw,0,raw.Length);}}
 static bool RelaySupportedProcess(string name){return new[]{"chrome","msedge","ChatGPT"}.Contains(name??"",StringComparer.OrdinalIgnoreCase);}
 static DesktopRelayBinding RelayCapture(){
  IntPtr h=GetForegroundWindow();uint pid=0;Process p=null;
  if(h!=IntPtr.Zero){
   try{GetWindowThreadProcessId(h,out pid);p=Process.GetProcessById((int)pid);}catch{p=null;}
   if(p!=null&&RelaySupportedProcess(p.ProcessName)&&p.MainWindowHandle!=IntPtr.Zero){
    var direct=new DesktopRelayBinding{hwnd=h.ToInt64(),pid=(int)pid,processName=p.ProcessName,title=p.MainWindowTitle??"",boundUtc=DateTime.UtcNow};RelaySaveBinding(direct);ScopeStore.Audit("desktop_relay_bound","","",new{pid=direct.pid,process=direct.processName,title=direct.title,source="foreground"});return direct;
   }
  }
  var candidates=new List<Process>();
  foreach(var name in new[]{"chrome","msedge","ChatGPT"})try{candidates.AddRange(Process.GetProcessesByName(name).Where(x=>x.MainWindowHandle!=IntPtr.Zero&&!String.IsNullOrWhiteSpace(x.MainWindowTitle)));}catch{}
  candidates=candidates.GroupBy(x=>x.Id).Select(g=>g.First()).ToList();
  if(candidates.Count==0)throw new Exception("No visible Chrome/Edge/ChatGPT window is available for relay binding.");
  Process chosen=candidates.Count==1?candidates[0]:candidates.FirstOrDefault(x=>(x.MainWindowTitle??"").IndexOf("ChatGPT",StringComparison.OrdinalIgnoreCase)>=0);
  if(chosen==null)throw new Exception("Multiple browser windows are open. Bring the target ChatGPT window to foreground once, then bind.");
  var b=new DesktopRelayBinding{hwnd=chosen.MainWindowHandle.ToInt64(),pid=chosen.Id,processName=chosen.ProcessName,title=chosen.MainWindowTitle??"",boundUtc=DateTime.UtcNow};RelaySaveBinding(b);ScopeStore.Audit("desktop_relay_bound","","",new{pid=b.pid,process=b.processName,title=b.title,source="auto-visible-window"});return b;
 }
 static IntPtr RelayResolve(DesktopRelayBinding b){
  if(b==null)return IntPtr.Zero;IntPtr h=new IntPtr(b.hwnd);uint pid;
  if(h!=IntPtr.Zero&&IsWindow(h)){GetWindowThreadProcessId(h,out pid);if((int)pid==b.pid)return h;}
  try{
   var ps=Process.GetProcessesByName(b.processName??"").Where(x=>x.MainWindowHandle!=IntPtr.Zero).ToList();
   if(ps.Count==1)return ps[0].MainWindowHandle;
   var exact=ps.FirstOrDefault(x=>String.Equals(x.MainWindowTitle,b.title,StringComparison.Ordinal));if(exact!=null)return exact.MainWindowHandle;
   var titled=ps.FirstOrDefault(x=>!String.IsNullOrWhiteSpace(x.MainWindowTitle));if(titled!=null)return titled.MainWindowHandle;
  }catch{}
  return IntPtr.Zero;
 }
 static string RelayPayloadPath(string id){return Path.Combine(RelayDir,id+".dpapi");}
 static void RelaySavePayload(DesktopRelayPayload p){Directory.CreateDirectory(RelayDir);var raw=Encoding.UTF8.GetBytes(StorageJson().Serialize(p));try{Core.Atomic(RelayPayloadPath(p.id),ProtectedData.Protect(raw,null,DataProtectionScope.CurrentUser));}finally{Array.Clear(raw,0,raw.Length);}}
 static DesktopRelayPayload RelayLoadPayload(string path){var raw=ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);try{return StorageJson().Deserialize<DesktopRelayPayload>(Encoding.UTF8.GetString(raw));}finally{Array.Clear(raw,0,raw.Length);}}
 static object RelayView(DesktopRelayPayload p){return new{relay_id=p.id,state=p.state,created_utc=p.createdUtc,due_utc=p.dueUtc,sent_utc=p.sentUtc,error=p.error,target=p.binding==null?null:new{pid=p.binding.pid,process=p.binding.processName,title=p.binding.title,bound_utc=p.binding.boundUtc}};}
 static object RelayStatus(){
  Directory.CreateDirectory(RelayDir);var b=RelayLoadBinding();DesktopRelayPayload latest=null;
  foreach(var f in Directory.GetFiles(RelayDir,"*.dpapi").Where(x=>!x.EndsWith("binding.dpapi",StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).Take(20)){try{latest=RelayLoadPayload(f);break;}catch{}}
  return new{binding=b==null?null:new{pid=b.pid,process=b.processName,title=b.title,bound_utc=b.boundUtc,window_alive=RelayResolve(b)!=IntPtr.Zero},latest=latest==null?null:RelayView(latest)};
 }
 static bool RelayActivate(IntPtr h){
  IntPtr fg=GetForegroundWindow();uint fgPid=0,targetPid=0;uint fgThread=0,targetThread=0,current=GetCurrentThreadId();
  try{
   if(fg!=IntPtr.Zero)fgThread=GetWindowThreadProcessId(fg,out fgPid);
   targetThread=GetWindowThreadProcessId(h,out targetPid);
   if(fgThread!=0&&fgThread!=current)AttachThreadInput(current,fgThread,true);
   if(targetThread!=0&&targetThread!=current)AttachThreadInput(current,targetThread,true);
   ShowWindowAsync(h,9);BringWindowToTop(h);SetForegroundWindow(h);Thread.Sleep(350);
   return GetForegroundWindow()==h;
  }finally{
   try{if(targetThread!=0&&targetThread!=current)AttachThreadInput(current,targetThread,false);}catch{}
   try{if(fgThread!=0&&fgThread!=current)AttachThreadInput(current,fgThread,false);}catch{}
  }
 }
 static void RelayRestoreForeground(IntPtr old){
  if(old==IntPtr.Zero||!IsWindow(old))return;
  uint oldPid=0,oldThread=GetWindowThreadProcessId(old,out oldPid),current=GetCurrentThreadId();
  try{if(oldThread!=0&&oldThread!=current)AttachThreadInput(current,oldThread,true);ShowWindowAsync(old,9);BringWindowToTop(old);SetForegroundWindow(old);}catch{}finally{try{if(oldThread!=0&&oldThread!=current)AttachThreadInput(current,oldThread,false);}catch{}}
 }
 public static int RunRelay(string path){
  DesktopRelayPayload p=null;IntPtr oldForeground=IntPtr.Zero;try{
   p=RelayLoadPayload(path);
   if(p.delaySeconds<=0){
    Thread.Sleep(700);
    IDataObject prior=null;try{prior=Clipboard.GetDataObject();}catch{}
    Clipboard.SetText(p.message,TextDataFormat.UnicodeText);SendKeys.SendWait("^v");Thread.Sleep(80);SendKeys.SendWait("{ENTER}");Thread.Sleep(120);
    try{if(prior!=null)Clipboard.SetDataObject(prior,true);}catch{}
    p.state="sent";p.sentUtc=DateTime.UtcNow;p.error="";RelaySavePayload(p);return 0;
   }
   TimeSpan wait=p.dueUtc-DateTime.UtcNow;if(wait.TotalMilliseconds>0)Thread.Sleep((int)Math.Min(Int32.MaxValue,wait.TotalMilliseconds));
   IntPtr h=RelayResolve(p.binding);if(h==IntPtr.Zero)throw new Exception("Bound ChatGPT window is no longer available.");
   oldForeground=GetForegroundWindow();if(!RelayActivate(h))throw new Exception("Could not bring the bound ChatGPT window to foreground.");
   RelayRect r;if(!GetWindowRect(h,out r))throw new Exception("Could not read target window bounds.");
   int x=(r.Left+r.Right)/2,y=Math.Max(r.Top+120,r.Bottom-105);SetCursorPos(x,y);mouse_event(0x0002,0,0,0,UIntPtr.Zero);mouse_event(0x0004,0,0,0,UIntPtr.Zero);Thread.Sleep(350);
   IDataObject old=null;try{old=Clipboard.GetDataObject();}catch{}
   Clipboard.SetText(p.message,TextDataFormat.UnicodeText);SendKeys.SendWait("^v");Thread.Sleep(120);SendKeys.SendWait("{ENTER}");Thread.Sleep(300);
   try{if(old!=null)Clipboard.SetDataObject(old,true);}catch{}
   p.state="sent";p.sentUtc=DateTime.UtcNow;p.error="";RelaySavePayload(p);RelayRestoreForeground(oldForeground);return 0;
  }catch(Exception e){try{RelayRestoreForeground(oldForeground);}catch{}try{if(p!=null){p.state="failed";p.error=e.Message;RelaySavePayload(p);}}catch{}return 1;}
 }
 static object RelayCall(IDictionary<string,object> args){
  string action=ScopeEngine.S(args,"action","status").ToLowerInvariant();
  if(action=="bind"){var b=RelayCapture();return new{ok=true,binding=new{pid=b.pid,process=b.processName,title=b.title,bound_utc=b.boundUtc}};}
  if(action=="status")return RelayStatus();
  if(action!="send")throw new Exception("action must be bind, send, or status.");
  string message=ScopeEngine.S(args,"message");if(String.IsNullOrWhiteSpace(message)||message.Length>4000)throw new Exception("message is required (maximum 4000 characters).");
  int delay; if(!Int32.TryParse(ScopeEngine.S(args,"delay_seconds","0"),out delay))delay=0;delay=Math.Max(0,Math.Min(3600,delay));
  var binding=RelayLoadBinding();if(delay>0&&(binding==null||RelayResolve(binding)==IntPtr.Zero))binding=RelayCapture();
  var p=new DesktopRelayPayload{id=Guid.NewGuid().ToString("N"),message=message,delaySeconds=delay,createdUtc=DateTime.UtcNow,dueUtc=DateTime.UtcNow.AddSeconds(delay),binding=binding};RelaySavePayload(p);
  var psi=new ProcessStartInfo(Core.Self,"--chat-relay "+Core.Q(RelayPayloadPath(p.id))){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(Core.Self)};Process.Start(psi);
  ScopeStore.Audit("desktop_relay_scheduled","","",new{relay_id=p.id,delay_seconds=delay,target_process=binding.processName,target_pid=binding.pid});return RelayView(p);
 }

 static string Token(){using(var rng=RandomNumberGenerator.Create()){var b=new byte[32];rng.GetBytes(b);return Convert.ToBase64String(b);}}
 static string BootId(){long ticks=DateTime.UtcNow.Ticks-(long)GetTickCount64()*TimeSpan.TicksPerMillisecond;ticks=(ticks/TimeSpan.TicksPerMinute)*TimeSpan.TicksPerMinute;return ticks.ToString();}
 public static List<DesktopRequest> Load(){if(!File.Exists(Db))return new List<DesktopRequest>();var b=ProtectedData.Unprotect(File.ReadAllBytes(Db),null,DataProtectionScope.CurrentUser);try{return StorageJson().Deserialize<List<DesktopRequest>>(Encoding.UTF8.GetString(b))??new List<DesktopRequest>();}finally{Array.Clear(b,0,b.Length);}}
 static void DeleteResult(DesktopRequest r){try{if(!String.IsNullOrEmpty(r.resultFile)){string p=Path.Combine(ResultDir,Path.GetFileName(r.resultFile));if(File.Exists(p))File.Delete(p);}}catch{}}
 static void Save(List<DesktopRequest> data){Directory.CreateDirectory(Dir);Directory.CreateDirectory(ResultDir);var terminal=data.Where(r=>r.state!="pending"&&r.state!="approved"&&r.state!="running").ToList();foreach(var old in terminal.Take(Math.Max(0,terminal.Count-96))){DeleteResult(old);data.Remove(old);}var b=Encoding.UTF8.GetBytes(StorageJson().Serialize(data));try{Core.Atomic(Db,ProtectedData.Protect(b,null,DataProtectionScope.CurrentUser));}finally{Array.Clear(b,0,b.Length);}}
 public static List<DesktopLease> LoadLeases(){if(!File.Exists(LeaseDb))return new List<DesktopLease>();var b=ProtectedData.Unprotect(File.ReadAllBytes(LeaseDb),null,DataProtectionScope.CurrentUser);try{return StorageJson().Deserialize<List<DesktopLease>>(Encoding.UTF8.GetString(b))??new List<DesktopLease>();}finally{Array.Clear(b,0,b.Length);}}
 static void SaveLeases(List<DesktopLease> data){Directory.CreateDirectory(Dir);var keep=data.OrderByDescending(x=>x.expiresUtc).Take(128).ToList();var b=Encoding.UTF8.GetBytes(StorageJson().Serialize(keep));try{Core.Atomic(LeaseDb,ProtectedData.Protect(b,null,DataProtectionScope.CurrentUser));}finally{Array.Clear(b,0,b.Length);}}
 static bool LeaseLive(DesktopLease l){return l!=null&&l.state=="active"&&DateTime.UtcNow<l.expiresUtc&&l.bootId==BootId();}
 static string LeaseState(DesktopLease l){if(l==null)return "missing";if(l.state!="active")return l.state;if(DateTime.UtcNow>=l.expiresUtc)return "expired";if(l.bootId!=BootId())return "expired_after_reboot";return "active";}
 public static void Start(){ScopeStore.Locked(()=>{Directory.CreateDirectory(Dir);Directory.CreateDirectory(ResultDir);var list=Load();foreach(var r in list)if(r.state=="running"||r.state=="approved"){r.state="interrupted";r.error="Server restarted; never automatically replayed.";}Save(list);var leases=LoadLeases();foreach(var l in leases)if(l.state=="active"&&!LeaseLive(l))l.state=DateTime.UtcNow>=l.expiresUtc?"expired":"expired_after_reboot";SaveLeases(leases);return 0;});pump=new System.Threading.Timer(x=>Pump(),null,250,250);}
 public static void Stop(){if(pump!=null)pump.Dispose();lock(rpcLock){if(childJob!=null)childJob.Dispose();if(engine!=null)engine.Dispose();engine=null;childJob=null;}}
 static void StartEngine(){if(engine!=null&&!engine.HasExited)return;
  string config=Path.Combine(Core.Root,"desktop-data");Directory.CreateDirectory(config);
  var pi=new ProcessStartInfo(Path.Combine(Core.Root,"engine","node.exe"),Core.Q(Path.Combine(Core.Root,"engine","launcher.mjs"))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.Combine(Core.Root,"engine"),StandardOutputEncoding=Encoding.UTF8};
  pi.EnvironmentVariables.Remove("CONTROL_PLANE_API_KEY");pi.EnvironmentVariables.Remove("OPENAI_ADMIN_KEY");pi.EnvironmentVariables.Remove("OPENAI_API_KEY");
  pi.EnvironmentVariables["PCBRIDGE_DC_DATA"]=config;pi.EnvironmentVariables["DESKTOP_COMMANDER_DISABLE_TELEMETRY"]="1";
  pi.EnvironmentVariables["PUPPETEER_SKIP_DOWNLOAD"]="true";pi.EnvironmentVariables["TEMP"]=Path.Combine(config,"temp");pi.EnvironmentVariables["TMP"]=pi.EnvironmentVariables["TEMP"];Directory.CreateDirectory(pi.EnvironmentVariables["TEMP"]);
  engine=new Process{StartInfo=pi};engine.ErrorDataReceived+=(a,b)=>{};engine.Start();childJob=new ChildJob();childJob.Attach(engine);engine.BeginErrorReadLine();
  RpcInner("initialize",new{protocolVersion="2024-11-05",capabilities=new{},clientInfo=new{name="PCBridge",version=Core.Version}});
  engine.StandardInput.WriteLine(Core.Json().Serialize(new{jsonrpc="2.0",method="notifications/initialized"}));engine.StandardInput.Flush();
 }
 static object RpcInner(string method,object args){int id=++sequence;engine.StandardInput.WriteLine(Core.Json().Serialize(new{jsonrpc="2.0",id=id,method=method,@params=args}));engine.StandardInput.Flush();var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<120000){var read=engine.StandardOutput.ReadLineAsync();if(!read.Wait(120000-(int)watch.ElapsedMilliseconds)){childJob.Dispose();throw new Exception("Desktop engine response deadline; engine stopped. A command may have partially executed. Do not replay without checking.");}string line=read.Result;if(line==null)throw new Exception("Desktop engine closed its output.");Dictionary<string,object> msg;try{msg=StorageJson().Deserialize<Dictionary<string,object>>(line);}catch{continue;}object got;if(!msg.TryGetValue("id",out got)||Convert.ToString(got)!=id.ToString())continue;if(msg.ContainsKey("error"))throw new Exception(StorageJson().Serialize(msg["error"]));return msg["result"];}throw new Exception("Desktop response deadline.");}
 public static object Rpc(string method,object args){lock(rpcLock){StartEngine();return RpcInner(method,args);}}
 static string GradeText(int grade){return grade==1?"1급 HARDLOCK":grade==2?"2급 시스템/프로세스":grade==3?"3급 변경":"4급 일반/읽기";}
 static int Grade(IDictionary<string,object> args){int n;return Int32.TryParse(ScopeEngine.S(args,"grade"),out n)?Math.Max(1,Math.Min(4,n)):4;}
 static int Duration(IDictionary<string,object> args){int n;return Int32.TryParse(ScopeEngine.S(args,"duration_minutes"),out n)?Math.Max(15,Math.Min(1440,n)):480;}
 static string ActionText(string tool,IDictionary<string,object> a){object v;if(a.TryGetValue("command",out v)&&v!=null)return Convert.ToString(v);if(a.TryGetValue("input",out v)&&v!=null)return Convert.ToString(v);return "";}
 static bool LocalPath(string value){return !String.IsNullOrWhiteSpace(value)&&Regex.IsMatch(value,@"\A[A-Za-z]:\\");}
 static string NormalizePath(string value){if(!LocalPath(value))return "";try{return Path.GetFullPath(value).TrimEnd('\\');}catch{return "";}}
 static void CollectPaths(object value,string key,List<string> result){
  var map=value as IDictionary<string,object>;if(map!=null){foreach(var kv in map){string k=(kv.Key??"").ToLowerInvariant();if(k=="paths"&&kv.Value is IList){foreach(var item in (IList)kv.Value){string p=NormalizePath(Convert.ToString(item));if(p.Length>0)result.Add(p);}}else if(k=="source"||k=="destination"||k=="cwd"||k=="workingdirectory"||k.EndsWith("path")){string p=NormalizePath(Convert.ToString(kv.Value));if(p.Length>0)result.Add(p);}else CollectPaths(kv.Value,k,result);}return;}
  var list=value as IList;if(list!=null)foreach(var item in list)CollectPaths(item,key,result);
 }
 static void CollectCommandPaths(string action,List<string> result){
  if(String.IsNullOrWhiteSpace(action))return;
  foreach(Match m in Regex.Matches(action,@"[""'](?<p>[A-Za-z]:\\[^""']+)[""']")){string p=NormalizePath(m.Groups["p"].Value.Trim());if(p.Length>0)result.Add(p);}
  foreach(Match m in Regex.Matches(action,@"(?<![A-Za-z0-9_])(?<p>[A-Za-z]:\\[^\s;&|""']+)")){string p=NormalizePath(m.Groups["p"].Value.TrimEnd(',',')',']'));if(p.Length>0)result.Add(p);}
 }
 static List<string> Paths(IDictionary<string,object> a){var p=new List<string>();CollectPaths(a,"",p);CollectCommandPaths(ActionText("",a),p);return p.Distinct(StringComparer.OrdinalIgnoreCase).ToList();}
 static bool Within(string path,string root){return String.Equals(path,root,StringComparison.OrdinalIgnoreCase)||path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}
 static bool NetworkUse(IDictionary<string,object> a){object v;if(a.TryGetValue("isUrl",out v)&&Convert.ToString(v).Equals("true",StringComparison.OrdinalIgnoreCase))return true;string s=ActionText("",a).ToLowerInvariant();return s.Contains("http://")||s.Contains("https://")||s.Contains("ftp://")||Regex.IsMatch(s,@"\b(curl|wget|invoke-webrequest|invoke-restmethod|ssh|scp)\b");}
 static bool DeleteCommand(string s){return Regex.IsMatch(s,@"(^|[\s;&|""'(])(remove-item|rm|del|erase|rmdir|rimraf)([\s;&|""')]|$)")||s.Contains("file.delete(")||s.Contains("directory.delete(")||s.Contains("os.remove(")||s.Contains("shutil.rmtree")||s.Contains("fs.unlink")||s.Contains("unlinksync");}
 static bool DangerousDelete(string s,List<string> paths){
  if(!DeleteCommand(s))return false;
  if(Regex.IsMatch(s,@"(?i)(\s|^)(-recurse|-r|/s|/q)(\s|$)")||s.Contains("shutil.rmtree")||s.Contains("directory.delete(")||s.Contains("rimraf"))return true;
  if(Regex.IsMatch(s,@"(?i)[A-Za-z]:\\[^\r\n;&|]*[\*\?]"))return true;
  foreach(var p in paths){
   string x=p.TrimEnd('\\');
   if(Regex.IsMatch(x,@"(?i)^[A-Za-z]:$"))return true;
   if(Regex.IsMatch(x,@"(?i)^[A-Za-z]:\\(Windows|Program Files(?: \(x86\))?|ProgramData)(\\|$)"))return true;
   if(Regex.IsMatch(x,@"(?i)^[A-Za-z]:\\Users$"))return true;
  }
  return false;
 }
 static string Hardlock(string tool,IDictionary<string,object> a){
  string t=(tool??"").ToLowerInvariant();string s=ActionText(tool,a).ToLowerInvariant();var paths=Paths(a);
  if(t=="set_config_value")return "bridge/security configuration change";
  if(t.Contains("delete")||t.Contains("remove"))return "permanent/removal-capable operation";
  if(DangerousDelete(s,paths))return "high-risk file deletion";
  if(Regex.IsMatch(s,@"(^|[\s;&|])(format|diskpart|clear-disk|remove-partition|bcdedit|cipher\s+/w)([\s;&|]|$)"))return "disk/boot destructive operation";
  if(Regex.IsMatch(s,@"(^|[\s;&|])(shutdown|restart-computer|stop-computer)([\s;&|]|$)"))return "machine power/restart operation";
  if(s.Contains("disableantispyware")||s.Contains("disablerealtimemonitoring")||Regex.IsMatch(s,@"set-netfirewallprofile.+enabled\s+\$?false"))return "security protection disablement";
  if(Regex.IsMatch(s,@"\b(cmdkey|set-localuser|set-adaccountpassword|net\s+user)\b"))return "credential/account change";
  if(Regex.IsMatch(s,@"\b(payment|checkout|purchase|paypal|stripe)\b")||s.Contains("송금")||s.Contains("결제"))return "payment/financial action";
  if(Regex.IsMatch(s,@"(?i)\bgit\s+(reflog\s+expire|gc\b[^\r\n;|]*--prune(?:=now)?|filter-repo|filter-branch)\b"))return "recovery/history destruction";
  if(DeleteCommand(s)&&Regex.IsMatch(s,@"(?i)(^|[\\/])\.git([\\/]|$)|\b(reflog|backup|backups|snapshot|snapshots|recovery|audit)\b"))return "recovery/history destruction";
  if(Regex.IsMatch(s,@"(?i)\b(reg\s+(add|delete)|set-itemproperty|new-itemproperty|remove-itemproperty)\b[^\r\n]*(hklm|hkey_local_machine)"))return "machine-wide registry change";
  return "";
 }
 static int RequiredGrade(string tool,IDictionary<string,object> a,out string note){
  note=Hardlock(tool,a);if(note.Length>0)return 1;string t=(tool??"").ToLowerInvariant();
  if(new[]{"read_file","read_multiple_files","list_directory","get_file_info","start_search","get_more_search_results","stop_search","read_process_output","list_sessions","list_processes","get_config","get_usage_stats","get_recent_tool_calls"}.Contains(t))return 4;
  if(new[]{"start_process","interact_with_process","force_terminate","kill_process"}.Contains(t))return 2;
  if(new[]{"write_file","write_pdf","create_directory","move_file","edit_block","give_feedback_to_desktop_commander"}.Contains(t))return 3;
  return 3;
 }
 static DesktopLeaseScope ParseScope(object value){
  var s=StorageJson().Deserialize<DesktopLeaseScope>(StorageJson().Serialize(value))??new DesktopLeaseScope();
  s.tools=(s.tools??new string[0]).Where(x=>!String.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
  s.paths=(s.paths??new string[0]).Select(NormalizePath).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
  s.command_prefixes=(s.command_prefixes??new string[0]).Where(x=>!String.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
  s.process_names=(s.process_names??new string[0]).Where(x=>!String.IsNullOrWhiteSpace(x)).Select(x=>Path.GetFileNameWithoutExtension(x.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
  return s;
 }
 static bool IsFileWriteTool(string tool){return new[]{"write_file","write_pdf","create_directory","move_file","edit_block"}.Contains((tool??"").ToLowerInvariant());}
 static bool IsProcessTool(string tool){return new[]{"start_process","interact_with_process","force_terminate","kill_process"}.Contains((tool??"").ToLowerInvariant());}
 static bool BroadShellPrefix(string p){string x=(p??"").Trim().Trim('"').ToLowerInvariant();return new[]{"powershell","powershell.exe","pwsh","pwsh.exe","cmd","cmd.exe","bash","sh","python","python.exe","node","node.exe"}.Contains(x);}
 static void ValidateScope(DesktopLeaseScope scope,int grade){
  if(grade<2||grade>3)throw new Exception("Reusable desktop leases are only for restricted grade 2 or grade 3 work. Grade 4 is automatic; grade 1 is one-shot only.");
  if(scope.tools.Length<1)throw new Exception("At least one tool capability is required.");
  foreach(var tool in scope.tools){string note;int needed=RequiredGrade(tool,new Dictionary<string,object>(),out note);if(needed==1||needed<grade)throw new Exception("Requested grade is not sufficient for tool: "+tool);}
  if(scope.tools.Any(IsFileWriteTool)&&scope.paths.Length<1)throw new Exception("Grade 3 file-changing capabilities require at least one exact path scope.");
  if(scope.tools.Any(x=>x.Equals("start_process",StringComparison.OrdinalIgnoreCase)||x.Equals("interact_with_process",StringComparison.OrdinalIgnoreCase))){
   if(scope.command_prefixes.Length<1)throw new Exception("Restricted grade 2 process capability requires command_prefixes.");
   if(scope.command_prefixes.Any(BroadShellPrefix))throw new Exception("A bare shell/runtime prefix is too broad. Request a script, executable, or materially narrower command prefix.");
  }
  if(scope.tools.Any(x=>x.Equals("kill_process",StringComparison.OrdinalIgnoreCase)||x.Equals("force_terminate",StringComparison.OrdinalIgnoreCase))&&scope.process_names.Length<1)throw new Exception("Process termination capability requires process_names.");
 }
 static string ProcessName(IDictionary<string,object> a){int pid;object v;if(!a.TryGetValue("pid",out v)||!Int32.TryParse(Convert.ToString(v),out pid))return "";try{using(var p=Process.GetProcessById(pid))return p.ProcessName;}catch{return "";}}
 static bool LeaseAllows(DesktopLease l,string tool,IDictionary<string,object> a,int callGrade){
  if(!LeaseLive(l)||callGrade==1||callGrade<l.grade)return false;var s=ParseScope(StorageJson().DeserializeObject(l.scopeJson));
  if(!s.tools.Any(x=>x=="*"||x.Equals(tool,StringComparison.OrdinalIgnoreCase)))return false;
  var paths=Paths(a);if(paths.Count>0&&paths.Any(p=>!s.paths.Any(root=>Within(p,root))))return false;
  if(IsFileWriteTool(tool)&&paths.Count==0)return false;
  string action=ActionText(tool,a);
  if((tool.Equals("start_process",StringComparison.OrdinalIgnoreCase)||tool.Equals("interact_with_process",StringComparison.OrdinalIgnoreCase))&&!s.command_prefixes.Any(p=>action.StartsWith(p,StringComparison.OrdinalIgnoreCase)))return false;
  if((tool.Equals("kill_process",StringComparison.OrdinalIgnoreCase)||tool.Equals("force_terminate",StringComparison.OrdinalIgnoreCase))&&!s.process_names.Any(x=>x.Equals(ProcessName(a),StringComparison.OrdinalIgnoreCase)))return false;
  if(NetworkUse(a)&&!s.network)return false;
  return true;
 }
 static DesktopLeaseScope Delta(string tool,IDictionary<string,object> a){
  var s=new DesktopLeaseScope();s.tools=new[]{tool};s.paths=Paths(a).ToArray();string action=ActionText(tool,a);if(action.Length>0)s.command_prefixes=new[]{action};string pn=ProcessName(a);if(pn.Length>0)s.process_names=new[]{pn};s.network=NetworkUse(a);return s;
 }
 static string[] Merge(string[] a,string[] b){return (a??new string[0]).Concat(b??new string[0]).Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}
 static DesktopLeaseScope Merge(DesktopLeaseScope a,DesktopLeaseScope b){a.tools=Merge(a.tools,b.tools);a.paths=Merge(a.paths,b.paths);a.command_prefixes=Merge(a.command_prefixes,b.command_prefixes);a.process_names=Merge(a.process_names,b.process_names);a.network=a.network||b.network;return a;}
 static DesktopLease FindLease(List<DesktopLease> leases,string token){if(String.IsNullOrEmpty(token))return null;string h=ScopeStore.Digest(token);return leases.FirstOrDefault(x=>ScopeStore.FixedEqual(x.tokenHash,h));}
 static object LeaseView(DesktopLease l){return new{lease_id=l.id,name=l.name,state=LeaseState(l),grade=l.grade,grade_name=GradeText(l.grade),expires_utc=l.expiresUtc,scope=StorageJson().DeserializeObject(l.scopeJson),reboot_resets=true};}
 static DesktopRequest NewRequest(string kind,string tool,string args,string reason,int grade,string scope,string leaseId,string candidateId,string candidateHash){
  return new DesktopRequest{id=Guid.NewGuid().ToString("N"),kind=kind,tool=tool,args=args,reason=reason,grade=grade,scopeJson=scope,leaseId=leaseId??"",candidateLeaseId=candidateId??"",candidateLeaseTokenHash=candidateHash??"",policyNote=grade==1?"HARDLOCK: reusable lease cannot bypass this call.":GradeText(grade)};
 }
 static object RequestResult(DesktopRequest r,string requestToken,string candidateToken){
  return new{request_id=r.id,request_token=requestToken,state=r.state,grade=r.grade,grade_name=GradeText(r.grade),candidate_lease_token=String.IsNullOrEmpty(candidateToken)?null:candidateToken,next=r.state=="approved"?"Auto-approved by grade/policy; poll desktop_result.":"Approval window opened automatically."};
 }
 public static object Call(string name,Dictionary<string,object> args){
  if(name=="desktop_tools")return Rpc("tools/list",new{});
  if(name=="desktop_relay")return RelayCall(args);
  if(name=="desktop_authorize"){
   string taskName=ScopeEngine.S(args,"name"),reason=ScopeEngine.S(args,"reason");if(taskName.Length<1||taskName.Length>120||reason.Length<1||reason.Length>1000)throw new Exception("Name and reason are required.");
   int grade=Grade(args),minutes=Duration(args);object cap;if(!args.TryGetValue("capabilities",out cap))throw new Exception("capabilities object required.");var scope=ParseScope(cap);ValidateScope(scope,grade);
   string requestToken=Token(),leaseToken=Token();var lease=new DesktopLease{id=Guid.NewGuid().ToString("N"),tokenHash=ScopeStore.Digest(leaseToken),name=taskName,reason=reason,grade=grade,scopeJson=StorageJson().Serialize(scope),state="pending",bootId=BootId(),expiresUtc=DateTime.UtcNow.AddMinutes(minutes)};
   return ScopeStore.Locked(()=>{var leases=LoadLeases();leases.Add(lease);SaveLeases(leases);var list=Load();var r=NewRequest("lease","desktop_authorize","{}",reason,grade,lease.scopeJson,lease.id,"","");r.tokenHash=ScopeStore.Digest(requestToken);r.expiresUtc=DateTime.UtcNow.AddMinutes(30);list.Add(r);Save(list);ScopeStore.Audit("desktop_lease_requested",r.id,"",new{lease_id=lease.id,name=taskName,grade=grade,scope=scope,minutes=minutes});LaunchApprovalPopup(r.id);return (object)new{request_id=r.id,request_token=requestToken,lease_token=leaseToken,state="pending",grade=grade,grade_name=GradeText(grade),scope=scope,next="Approval window opened automatically."};});
  }
  if(name=="desktop_lease_status"||name=="desktop_revoke"){
   return ScopeStore.Locked(()=>{var leases=LoadLeases();var lease=FindLease(leases,ScopeEngine.S(args,"lease_token"));if(lease==null)throw new Exception("Invalid desktop lease capability.");if(name=="desktop_revoke"){lease.state="revoked";SaveLeases(leases);ScopeStore.Audit("desktop_lease_revoked",lease.id,"",null);}return LeaseView(lease);});
  }
  if(name=="desktop_request"){
   string tool=ScopeEngine.S(args,"tool"),reason=ScopeEngine.S(args,"reason"),raw=ScopeEngine.S(args,"arguments_json");if(tool.Length<1||tool.Length>100||reason.Length<1||reason.Length>1000||raw.Length>500000)throw new Exception("Tool, reason and JSON arguments required (maximum 500000 characters).");
   var parsed=StorageJson().Deserialize<Dictionary<string,object>>(raw);if(parsed==null)throw new Exception("arguments_json must be an object.");
   string note;int grade=RequiredGrade(tool,parsed,out note);string leaseToken=ScopeEngine.S(args,"lease_token");
   return ScopeStore.Locked(()=>{var list=Load();if(list.Count(entry=>entry.state=="pending"||entry.state=="approved"||entry.state=="running")>=128)throw new Exception("Too many unresolved desktop requests. Review or reject old requests.");var leases=LoadLeases();var lease=FindLease(leases,leaseToken);if(lease!=null&&!LeaseLive(lease))lease=null;bool allowed=grade>1||(lease!=null&&LeaseAllows(lease,tool,parsed,grade));string requestToken=Token(),candidateToken="",candidateId="",candidateHash="";
    if(!allowed&&grade>1&&lease==null){candidateToken=Token();candidateId=Guid.NewGuid().ToString("N");candidateHash=ScopeStore.Digest(candidateToken);}
    var delta=Delta(tool,parsed);var r=NewRequest("call",tool,StorageJson().Serialize(parsed),reason,grade,StorageJson().Serialize(delta),lease==null?"":lease.id,candidateId,candidateHash);r.tokenHash=ScopeStore.Digest(requestToken);r.state=allowed?"approved":"pending";r.policyNote=grade==1?"HARDLOCK · "+note:(lease!=null&&LeaseAllows(lease,tool,parsed,grade)?"기존 작업 lease 범위 내 자동 허용":"비-HARDLOCK 기본 자동 허용");list.Add(r);Save(list);ScopeStore.Audit("desktop_requested",r.id,"",new{tool=tool,grade=grade,state=r.state,lease_id=r.leaseId,delta=delta,args_sha256=ScopeStore.Digest(r.args),policy=r.policyNote});if(!allowed)LaunchApprovalPopup(r.id);return RequestResult(r,requestToken,candidateToken);});
  }
  if(name=="desktop_result"){
   return ScopeStore.Locked(()=>{var list=Load();var r=list.FirstOrDefault(v=>ScopeStore.FixedEqual(v.tokenHash,ScopeStore.Digest(ScopeEngine.S(args,"request_token"))));if(r==null)throw new Exception("Invalid desktop request capability.");if(r.state=="pending"&&DateTime.UtcNow>r.expiresUtc){r.state="expired";Save(list);}int offset=0,count=100000,tmp;if(Int32.TryParse(ScopeEngine.S(args,"offset"),out tmp))offset=tmp;if(Int32.TryParse(ScopeEngine.S(args,"count"),out tmp))count=tmp;offset=Math.Max(0,offset);count=Math.Max(1000,Math.Min(120000,count));string all=ReadStoredResult(r);if(offset>all.Length)offset=all.Length;int take=Math.Min(count,all.Length-offset);string chunk=take>0?all.Substring(offset,take):"";int next=offset+take;return (object)new{request_id=r.id,state=r.state,grade=r.grade,grade_name=GradeText(r.grade),result_json=chunk,error=r.error,result_length=all.Length,offset=offset,next_offset=next,has_more=next<all.Length,lease_id=r.leaseId,policy=r.policyNote};});
  }
  throw new Exception("Unknown desktop integration tool.");
 }
 static void StoreResult(DesktopRequest r,string result){
  r.resultLength=result==null?0:result.Length;r.result="";r.resultFile="";if(String.IsNullOrEmpty(result))return;
  if(result.Length<=120000){r.result=result;return;}Directory.CreateDirectory(ResultDir);string name=r.id+".dpapi",path=Path.Combine(ResultDir,name);byte[] raw=Encoding.UTF8.GetBytes(result);try{Core.Atomic(path,ProtectedData.Protect(raw,null,DataProtectionScope.CurrentUser));r.resultFile=name;}finally{Array.Clear(raw,0,raw.Length);}
 }
 static string ReadStoredResult(DesktopRequest r){if(String.IsNullOrEmpty(r.resultFile))return r.result??"";string path=Path.Combine(ResultDir,Path.GetFileName(r.resultFile));if(!File.Exists(path))return "";byte[] raw=ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);try{return Encoding.UTF8.GetString(raw);}finally{Array.Clear(raw,0,raw.Length);}}
 public static void Decide(string id,bool approve){ScopeStore.Locked(()=>{var list=Load();var r=list.Single(x=>x.id==id);if(r.state!="pending"||DateTime.UtcNow>r.expiresUtc)throw new Exception("Request is no longer pending.");if(r.kind=="lease"){var leases=LoadLeases();var lease=leases.Single(x=>x.id==r.leaseId);lease.state=approve?"active":"denied";if(approve){lease.bootId=BootId();r.state="completed";StoreResult(r,StorageJson().Serialize(LeaseView(lease)));}else r.state="denied";SaveLeases(leases);ScopeStore.Audit(approve?"desktop_lease_locally_approved":"desktop_lease_locally_denied",id,"",new{lease_id=lease.id,grade=lease.grade,scope=StorageJson().DeserializeObject(lease.scopeJson)});}else{r.state=approve?"approved":"denied";ScopeStore.Audit(approve?"desktop_locally_approved":"desktop_locally_denied",id,"",new{tool=r.tool,grade=r.grade,args_sha256=ScopeStore.Digest(r.args)});}Save(list);return 0;});}
 public static void ExtendLeaseAndApprove(string id){ScopeStore.Locked(()=>{var list=Load();var r=list.Single(x=>x.id==id);if(r.kind!="call"||r.state!="pending"||r.grade==1)throw new Exception("This request cannot be added to a reusable lease.");var leases=LoadLeases();DesktopLease lease=null;if(!String.IsNullOrEmpty(r.leaseId))lease=leases.FirstOrDefault(x=>x.id==r.leaseId&&LeaseLive(x));var delta=ParseScope(StorageJson().DeserializeObject(r.scopeJson));if(lease==null){if(String.IsNullOrEmpty(r.candidateLeaseId)||String.IsNullOrEmpty(r.candidateLeaseTokenHash))throw new Exception("No candidate lease is available.");lease=new DesktopLease{id=r.candidateLeaseId,tokenHash=r.candidateLeaseTokenHash,name="자동 작업 권한 · "+r.tool,reason=r.reason,state="active",grade=r.grade,scopeJson=StorageJson().Serialize(delta),bootId=BootId(),expiresUtc=DateTime.UtcNow.AddHours(8)};ValidateScope(delta,lease.grade);leases.Add(lease);r.leaseId=lease.id;}else{var merged=Merge(ParseScope(StorageJson().DeserializeObject(lease.scopeJson)),delta);lease.grade=Math.Min(lease.grade,r.grade);ValidateScope(merged,lease.grade);lease.scopeJson=StorageJson().Serialize(merged);lease.expiresUtc=DateTime.UtcNow.AddHours(8);}SaveLeases(leases);r.state="approved";r.policyNote="사용자가 작업 lease에 최소 delta를 추가 승인함";Save(list);ScopeStore.Audit("desktop_lease_extended",lease.id,"",new{request_id=r.id,grade=lease.grade,delta=delta});return 0;});}
 public static void RevokeLeaseById(string id){ScopeStore.Locked(()=>{var leases=LoadLeases();var l=leases.Single(x=>x.id==id);l.state="revoked";SaveLeases(leases);ScopeStore.Audit("desktop_lease_locally_revoked",id,"",null);return 0;});}
 static void Pump(){if(Interlocked.Exchange(ref pumping,1)!=0)return;try{DesktopRequest request=ScopeStore.Locked(()=>{var list=Load();foreach(var expired in list.Where(x=>(x.state=="pending"||x.state=="approved")&&DateTime.UtcNow>x.expiresUtc))expired.state="expired";var r=list.FirstOrDefault(x=>x.kind=="call"&&x.state=="approved");if(r!=null){r.state="running";Save(list);}return r;});if(request==null)return;string result="",error="";try{result=StorageJson().Serialize(Rpc("tools/call",new{name=request.tool,arguments=StorageJson().Deserialize<Dictionary<string,object>>(request.args)}));}catch(Exception e){error=e.Message;}ScopeStore.Locked(()=>{var list=Load();var r=list.Single(x=>x.id==request.id);r.state=error.Length==0?"completed":"failed";StoreResult(r,result);r.error=error;Save(list);ScopeStore.Audit("desktop_call_finished",r.id,"",new{tool=r.tool,state=r.state,grade=r.grade,result_length=r.resultLength,stored=!String.IsNullOrEmpty(r.resultFile)});return 0;});}catch{}finally{Interlocked.Exchange(ref pumping,0);}}
 static object Prop(string type,string description){return new{type=type,description=description};}
 public static object[] Tools(){
  var capabilities=new{type="object",properties=new Dictionary<string,object>{{"tools",new{type="array",items=Prop("string","Desktop Commander tool name")}},{"paths",new{type="array",items=Prop("string","Exact local file or directory scope")}},{"command_prefixes",new{type="array",items=Prop("string","Narrow executable/script command prefixes for grade 2 process work")}},{"process_names",new{type="array",items=Prop("string","Process names allowed for termination")}},{"network",Prop("boolean","Allow network use inside the approved process capability")}},required=new[]{"tools"},additionalProperties=false};
  return new object[]{
   new{name="desktop_tools",description="Discover bundled Desktop Commander tool schemas. Tool discovery itself is open.",inputSchema=new{type="object",properties=new{},additionalProperties=false}},
   new{name="desktop_relay",description="Built-in one-shot continuation relay for this ChatGPT conversation. action=bind captures the currently focused ChatGPT browser/app window. action=send with omitted delay_seconds uses immediate last-task mode: after a short UI settle it types into the currently focused control and submits. Positive delay_seconds uses the bound ChatGPT window path. action=status reports binding and latest relay state. Use this instead of ad-hoc SendKeys/PowerShell. The relay is one-shot and does not create an autonomous infinite loop.",inputSchema=new{type="object",properties=new{action=new{type="string",@enum=new[]{"bind","send","status"}},message=Prop("string","Message to type and submit for action=send (max 4000 chars)"),delay_seconds=new{type="integer",minimum=1,maximum=3600}},required=new[]{"action"},additionalProperties=false}},
   new{name="desktop_authorize",description="Preflight a multi-step desktop task once. Request the minimum restricted grade and scope for the whole task: grade 3 for scoped file changes, grade 2 for narrowly-scoped process/system work. Grade 4 read/general calls are automatic. Grade 1 hardlocks are never leaseable and remain one-shot.",inputSchema=new{type="object",properties=new{name=Prop("string","Task title"),reason=Prop("string","Why the task needs these capabilities"),grade=new{type="integer",minimum=2,maximum=3},duration_minutes=new{type="integer",minimum=15,maximum=1440},capabilities=capabilities},required=new[]{"name","reason","grade","capabilities"},additionalProperties=false}},
   new{name="desktop_request",description="Call a Desktop Commander tool. Non-HARDLOCK calls auto-run by default. Only HARDLOCK operations (payment/financial actions, credential/security changes, machine-critical changes, recovery/history destruction, and high-risk destructive actions) require local approval. Reusable leases remain supported for compatibility but are not required for ordinary work.",inputSchema=new{type="object",properties=new{tool=Prop("string","Upstream tool name"),arguments_json=Prop("string","Exact JSON object for the upstream tool"),reason=Prop("string","Why this call is needed"),lease_token=Prop("string","Optional private task lease from desktop_authorize or a locally-added delta")},required=new[]{"tool","arguments_json","reason"},additionalProperties=false}},
   new{name="desktop_result",description="Read request status/result. Large results are retained locally and returned in chunks instead of failing. Follow next_offset while has_more is true.",inputSchema=new{type="object",properties=new{request_token=Prop("string","Private request capability"),offset=Prop("integer","Character offset for paged result"),count=Prop("integer","Chunk size up to 120000 characters")},required=new[]{"request_token"},additionalProperties=false}},
   new{name="desktop_lease_status",description="Read the status, grade and exact scope of a private desktop task lease. Leases survive bridge restarts but are invalidated by PC reboot or expiry.",inputSchema=new{type="object",properties=new{lease_token=Prop("string","Private desktop lease capability")},required=new[]{"lease_token"},additionalProperties=false}},
   new{name="desktop_revoke",description="Revoke a private desktop task lease immediately.",inputSchema=new{type="object",properties=new{lease_token=Prop("string","Private desktop lease capability")},required=new[]{"lease_token"},additionalProperties=false}}
  };
 }
}
sealed class DesktopWindow:Form {
 ListBox list=new ListBox();TextBox detail=new TextBox();Label summary=new Label(),hint=new Label();List<DesktopRequest> snapshot=new List<DesktopRequest>();System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
 Button yes,extend,no,leases,history;string initialFocus;bool popupMode;
 public DesktopWindow(string focusId=null){initialFocus=focusId;popupMode=!String.IsNullOrEmpty(focusId);Text="권한 요청 · PCBridge";Font=new Font("Malgun Gothic",10);ClientSize=new Size(940,620);StartPosition=FormStartPosition.CenterScreen;TopMost=popupMode;
  summary.Font=new Font("Malgun Gothic",14,FontStyle.Bold);summary.SetBounds(20,16,900,34);Controls.Add(summary);
  hint.Text="권장: 작업 권한으로 승인하면 같은 작업 범위에서는 다시 묻지 않습니다. 1급 HARDLOCK만 항상 별도 승인합니다.";hint.ForeColor=Color.DimGray;hint.SetBounds(20,50,900,28);Controls.Add(hint);
  list.SetBounds(20,88,285,420);list.IntegralHeight=false;detail.SetBounds(325,88,595,420);detail.Multiline=true;detail.ReadOnly=true;detail.ScrollBars=ScrollBars.Vertical;detail.BorderStyle=BorderStyle.FixedSingle;
  yes=new Button{Text="이 작업 동안 허용",Bounds=new Rectangle(325,530,205,40)};extend=new Button{Text="이번만 승인",Bounds=new Rectangle(540,530,145,40)};no=new Button{Text="거절",Bounds=new Rectangle(695,530,100,40)};leases=new Button{Text="활성 권한",Bounds=new Rectangle(20,530,125,40)};history=new Button{Text="최근 활동",Bounds=new Rectangle(155,530,125,40)};
  Controls.AddRange(new Control[]{list,detail,yes,extend,no,leases,history});
  list.SelectedIndexChanged+=(a,b)=>ShowSelected();yes.Click+=(a,b)=>ActPrimary();extend.Click+=(a,b)=>ActOnce();no.Click+=(a,b)=>ActNo();leases.Click+=(a,b)=>new DesktopLeaseWindow().ShowDialog(this);history.Click+=(a,b)=>new DesktopHistoryWindow().ShowDialog(this);AcceptButton=yes;
  timer.Interval=750;timer.Tick+=(a,b)=>RefreshList();timer.Start();Shown+=(a,b)=>{if(popupMode){Activate();BringToFront();}};FormClosed+=(a,b)=>timer.Dispose();RefreshList();
 }
 string GradeName(int g){return g==1?"1급 HARDLOCK":g==2?"2급 시스템/프로세스":g==3?"3급 변경":"4급 일반";}
 string ItemText(DesktopRequest r){string kind=r.kind=="lease"?"작업 권한":r.tool;return GradeName(r.grade)+" · "+kind;}
 void ShowSelected(){
  if(list.SelectedIndex<0||list.SelectedIndex>=snapshot.Count){detail.Text="대기 중인 승인 요청이 없습니다.";yes.Enabled=extend.Enabled=no.Enabled=false;return;}
  var r=snapshot[list.SelectedIndex];yes.Enabled=no.Enabled=r.state=="pending";extend.Enabled=r.kind=="call"&&r.state=="pending"&&r.grade>1;
  yes.Text=r.kind=="lease"?"작업 권한 승인":(r.grade==1?"이번 1급 요청 승인":"이 작업 동안 허용");extend.Text="이번만 승인";
  string scope=String.IsNullOrWhiteSpace(r.scopeJson)?"없음":r.scopeJson;
  detail.Text="등급  "+GradeName(r.grade)+"\r\n종류  "+(r.kind=="lease"?"작업 권한":"도구 호출")+"\r\n도구  "+r.tool+"\r\n\r\n왜 필요한가\r\n"+r.reason+"\r\n\r\n허용될 최소 범위\r\n"+scope+"\r\n\r\n정책 판단\r\n"+r.policyNote+"\r\n\r\n실행 인수\r\n"+r.args;
 }
 void ActPrimary(){try{if(list.SelectedIndex<0)return;var r=snapshot[list.SelectedIndex];if(r.kind=="lease"||r.grade==1){ActOnce();return;}ActExtend();}catch(Exception e){MessageBox.Show(this,e.Message);}}
 void ActOnce(){try{if(list.SelectedIndex<0)return;var r=snapshot[list.SelectedIndex];string msg=r.kind=="lease"?"표시한 제한적 작업 권한을 승인하시겠습니까?":(r.grade==1?"1급 HARDLOCK입니다. 이 요청 한 번만 실행하시겠습니까?":"이번 요청만 실행합니다. 같은 작업에서 다시 승인이 필요할 수 있습니다. 계속하시겠습니까?");if(MessageBox.Show(this,msg,"PCBridge 승인",MessageBoxButtons.YesNo,r.grade==1?MessageBoxIcon.Warning:MessageBoxIcon.Question)!=DialogResult.Yes)return;DesktopIntegration.Decide(r.id,true);RefreshList();CloseIfDone();}catch(Exception e){MessageBox.Show(this,e.Message);}}
 void ActExtend(){try{if(list.SelectedIndex<0)return;var r=snapshot[list.SelectedIndex];if(MessageBox.Show(this,"현재 작업에 필요한 최소 범위를 작업 권한으로 허용합니다.\r\n이 범위 안의 후속 호출은 다시 묻지 않습니다. 상세 범위를 확인하셨습니까?","이 작업 동안 허용",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;DesktopIntegration.ExtendLeaseAndApprove(r.id);RefreshList();CloseIfDone();}catch(Exception e){MessageBox.Show(this,e.Message);}}
 void ActNo(){try{if(list.SelectedIndex<0)return;DesktopIntegration.Decide(snapshot[list.SelectedIndex].id,false);RefreshList();CloseIfDone();}catch(Exception e){MessageBox.Show(this,e.Message);}}
 void CloseIfDone(){if(!popupMode)return;try{if(!ScopeStore.Locked(()=>DesktopIntegration.Load().Any(x=>x.state=="pending")))Close();}catch{}}
 void RefreshList(){
  string selected=list.SelectedIndex>=0&&list.SelectedIndex<snapshot.Count?snapshot[list.SelectedIndex].id:null;
  var all=ScopeStore.Locked(()=>DesktopIntegration.Load().OrderByDescending(x=>x.createdUtc).ToList());
  int pending=all.Count(x=>x.state=="pending"),hard=all.Count(x=>x.state=="pending"&&x.grade==1),active=ScopeStore.Locked(()=>DesktopIntegration.LoadLeases().Count(x=>x.state=="active"&&DateTime.UtcNow<x.expiresUtc));
  summary.Text=pending==0?"승인 대기 없음":("승인 대기 "+pending+(hard>0?"  ·  HARDLOCK "+hard:"")+"  ·  활성 작업 권한 "+active);
  snapshot=all.Where(x=>x.state=="pending").ToList();
  if(popupMode&&snapshot.Count==0){BeginInvoke(new Action(Close));return;}
  list.BeginUpdate();list.Items.Clear();foreach(var r in snapshot)list.Items.Add(ItemText(r));
  if(selected==null&&initialFocus!=null){selected=initialFocus;initialFocus=null;}
  list.SelectedIndex=snapshot.FindIndex(r=>r.id==selected);if(list.SelectedIndex<0&&snapshot.Count>0)list.SelectedIndex=0;list.EndUpdate();ShowSelected();
 }
}
sealed class DesktopHistoryWindow:Form {
 ListBox list=new ListBox();TextBox detail=new TextBox();ComboBox filter=new ComboBox();List<DesktopRequest> snapshot=new List<DesktopRequest>();
 public DesktopHistoryWindow(){Text="최근 활동 · PCBridge";Font=new Font("Malgun Gothic",10);ClientSize=new Size(900,590);StartPosition=FormStartPosition.CenterParent;
  Controls.Add(new Label{Text="완료된 호출은 기본 승인 화면에서 숨깁니다. 필요할 때만 여기서 확인하세요.",Bounds=new Rectangle(15,15,650,28)});
  filter.DropDownStyle=ComboBoxStyle.DropDownList;filter.Items.AddRange(new object[]{"전체","완료","거절","실패/중단"});filter.SelectedIndex=0;filter.SetBounds(690,12,190,30);
  list.SetBounds(15,55,330,470);detail.SetBounds(360,55,520,470);detail.Multiline=true;detail.ReadOnly=true;detail.ScrollBars=ScrollBars.Vertical;
  var close=new Button{Text="닫기",Bounds=new Rectangle(735,535,145,36)};Controls.AddRange(new Control[]{filter,list,detail,close});filter.SelectedIndexChanged+=(a,b)=>Reload();list.SelectedIndexChanged+=(a,b)=>ShowSelected();close.Click+=(a,b)=>Close();Reload();
 }
 string Status(DesktopRequest r){if(r.state=="completed")return "완료";if(r.state=="denied")return "거절";if(r.state=="failed")return "실패";if(r.state=="interrupted")return "중단";if(r.state=="expired")return "만료";return r.state;}
 bool Match(DesktopRequest r){if(r.state=="pending")return false;if(filter.SelectedIndex==0)return true;if(filter.SelectedIndex==1)return r.state=="completed";if(filter.SelectedIndex==2)return r.state=="denied";return r.state=="failed"||r.state=="interrupted"||r.state=="expired";}
 void Reload(){snapshot=ScopeStore.Locked(()=>DesktopIntegration.Load().Where(Match).OrderByDescending(x=>x.createdUtc).Take(100).ToList());list.BeginUpdate();list.Items.Clear();foreach(var r in snapshot){string when=r.createdUtc==DateTime.MinValue?"":r.createdUtc.ToLocalTime().ToString("MM-dd HH:mm");list.Items.Add(Status(r)+" · "+r.tool+(when==""?"":" · "+when));}list.EndUpdate();if(snapshot.Count>0)list.SelectedIndex=0;else detail.Text="표시할 최근 활동이 없습니다.";}
 void ShowSelected(){if(list.SelectedIndex<0||list.SelectedIndex>=snapshot.Count)return;var r=snapshot[list.SelectedIndex];detail.Text="상태: "+Status(r)+"\r\n등급: "+r.grade+"급\r\n도구: "+r.tool+"\r\n사유: "+r.reason+"\r\n정책: "+r.policyNote+"\r\n\r\n범위\r\n"+r.scopeJson+"\r\n\r\n오류\r\n"+r.error;}
}
sealed class DesktopLeaseWindow:Form {
 ListBox list=new ListBox();TextBox detail=new TextBox();List<DesktopLease> snapshot=new List<DesktopLease>();
 public DesktopLeaseWindow(){Text="활성 도구 권한 · PCBridge";Font=new Font("Malgun Gothic",10);ClientSize=new Size(860,560);StartPosition=FormStartPosition.CenterParent;list.SetBounds(15,20,290,450);detail.SetBounds(320,20,525,450);detail.Multiline=true;detail.ReadOnly=true;detail.ScrollBars=ScrollBars.Both;var revoke=new Button{Text="선택 권한 회수",Bounds=new Rectangle(600,490,245,36)};var refresh=new Button{Text="새로 고침",Bounds=new Rectangle(15,490,180,36)};Controls.AddRange(new Control[]{list,detail,revoke,refresh});list.SelectedIndexChanged+=(a,b)=>ShowSelected();refresh.Click+=(a,b)=>Reload();revoke.Click+=(a,b)=>{try{if(list.SelectedIndex<0)return;if(MessageBox.Show(this,"선택한 작업 권한을 즉시 회수하시겠습니까?","권한 회수",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;DesktopIntegration.RevokeLeaseById(snapshot[list.SelectedIndex].id);Reload();}catch(Exception e){MessageBox.Show(this,e.Message);}};Reload();}
 string State(DesktopLease l){if(l.state!="active")return l.state;if(DateTime.UtcNow>=l.expiresUtc)return "expired";return "active";}
 void Reload(){string id=list.SelectedIndex>=0&&list.SelectedIndex<snapshot.Count?snapshot[list.SelectedIndex].id:null;snapshot=ScopeStore.Locked(()=>DesktopIntegration.LoadLeases().OrderByDescending(x=>x.expiresUtc).ToList());list.Items.Clear();foreach(var l in snapshot)list.Items.Add(State(l)+" · "+l.grade+"급 · "+l.name);list.SelectedIndex=snapshot.FindIndex(x=>x.id==id);ShowSelected();}
 void ShowSelected(){if(list.SelectedIndex<0){detail.Clear();return;}var l=snapshot[list.SelectedIndex];detail.Text="이름: "+l.name+"\r\n상태: "+State(l)+"\r\n등급: "+l.grade+"급\r\n사유: "+l.reason+"\r\n만료: "+l.expiresUtc.ToLocalTime()+"\r\nPC 재부팅 시 초기화: 예\r\n\r\n범위:\r\n"+l.scopeJson;}
}