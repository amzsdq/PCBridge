using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Reflection;
using System.Windows.Forms;
using System.Drawing;
using System.Web.Script.Serialization;
using Microsoft.Win32;
[assembly: AssemblyVersion("1.3.6.0")]
[assembly: AssemblyFileVersion("1.3.6.0")]
[assembly: AssemblyProduct("PCBridge Integrated Preview")]

public sealed class Settings {
 public string Tunnel="";
 public string Key="";
 public string Folder="";
 public bool Shell=false;
}
public sealed class ViewState {
 public string Phase="off";
 public string Message="연결 꺼짐";
 public bool Ready=false;
 public int Pid;
 public long StartTicks;
 public string Request="";
 public bool Applied=false;
 public string Updated=DateTimeOffset.Now.ToString("o");
}
public sealed class CliResult { public int Code; public string Text; }

static class Core {
 public const string Version="1.3.6-integrated-preview.1";
 public const string Marker="PCBridge Portable owned data v1";
 public static readonly string Instance=GetInstance();
 public static readonly string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Instance);
 public static readonly string Exe=Path.Combine(Root,"PCBridge-Portable.exe");
 public static readonly string Bin=Path.Combine(Root,"runtime","tunnel-client.exe");
 public static readonly string Bridge=Path.Combine(Root,"bridge","PCBridge.exe");
 public static readonly string Config=Path.Combine(Root,"settings.dpapi");
 public static readonly string EventName="Local\\"+Instance+"-stop";
 public static readonly string RunName=Instance;
 public static readonly string Self=Assembly.GetExecutingAssembly().Location;
 public const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
 public static string GetInstance() {
  string t=Environment.GetEnvironmentVariable("PCBRIDGE_TEST_INSTANCE");
  if(String.IsNullOrEmpty(t)) return "PCBridge-Integrated-Preview";
  if(!Regex.IsMatch(t,@"\A[a-f0-9]{32}\z")) throw new Exception("Invalid test instance.");
  return "PCBridge-Integrated-Test-"+t;
 }
 public static JavaScriptSerializer Json() {return new JavaScriptSerializer{MaxJsonLength=2000000};}
 public static string Q(string s) {return "\""+s.Replace("\"","\\\"")+"\"";}
 public static string Hash(string path) {using(var h=SHA256.Create()) using(var f=File.OpenRead(path)) return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
 public static void NoLinks(string path) {
  var p=new DirectoryInfo(Path.GetFullPath(path));
  while(p!=null) {if(p.Exists && (p.Attributes&FileAttributes.ReparsePoint)!=0) throw new Exception("링크 폴더는 지원하지 않습니다: 안전을 위해 중단했습니다."); p=p.Parent;}
 }
 public static void Prepare() {
  NoLinks(Root);
  bool existed=Directory.Exists(Root);
  if(existed && !File.Exists(Path.Combine(Root,".pcbridge-owner"))) throw new Exception("전용 폴더에 다른 파일이 있습니다. 자동 변경하지 않습니다.");
  Directory.CreateDirectory(Root);
  var acl=new DirectorySecurity(); acl.SetAccessRuleProtection(true,false);
  var user=WindowsIdentity.GetCurrent().User;
  acl.SetOwner(user);
  foreach(var sid in new[]{user,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
   acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
  Directory.SetAccessControl(Root,acl);
  File.WriteAllText(Path.Combine(Root,".pcbridge-owner"),Marker);
  using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("PCBridge.Payload"))
  using(var zip=new ZipArchive(input,ZipArchiveMode.Read)) {
   foreach(var e in zip.Entries) {
    if(String.IsNullOrEmpty(e.Name)) continue;
    string dest=Path.GetFullPath(Path.Combine(Root,e.FullName));
    if(!dest.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid payload.");
    NoLinks(Path.GetDirectoryName(dest)); Directory.CreateDirectory(Path.GetDirectoryName(dest));
    if(File.Exists(dest)) {
     if((File.GetAttributes(dest)&FileAttributes.ReparsePoint)!=0)throw new Exception("실행 파일 링크를 허용하지 않습니다.");
     using(var h=SHA256.Create()) using(var embedded=e.Open()) using(var disk=File.OpenRead(dest))
      if(Convert.ToBase64String(h.ComputeHash(embedded))!=Convert.ToBase64String(h.ComputeHash(disk)))throw new Exception("실행 파일 무결성 확인 실패. 연결을 중지하고 전용 폴더를 제거한 뒤 다시 실행하세요.");
     continue;
    }
    using(var a=e.Open()) using(var b=new FileStream(dest,FileMode.CreateNew)) a.CopyTo(b);
   }
  }
  if(!String.Equals(Self,Exe,StringComparison.OrdinalIgnoreCase) && (!File.Exists(Exe)||Hash(Self)!=Hash(Exe))) {
   if(Worker()!=null) throw new Exception("다른 버전이 실행 중입니다. 먼저 연결을 중지하세요.");
   File.Copy(Self,Exe,true);
  }
  if(File.Exists(Config+".new")) File.Delete(Config+".new");
 }
 public static void Atomic(string path,byte[] bytes) {
  string temp=path+".new";
  using(var f=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {f.Write(bytes,0,bytes.Length); f.Flush(true);}
  if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
 }
 public static byte[] Seal(Settings s) {
  byte[] b=Encoding.UTF8.GetBytes(Json().Serialize(s));
  try{return ProtectedData.Protect(b,null,DataProtectionScope.CurrentUser);}finally{Array.Clear(b,0,b.Length);}
 }
 public static Settings Unseal(byte[] data) {
  byte[] b=ProtectedData.Unprotect(data,null,DataProtectionScope.CurrentUser);
  try{return Json().Deserialize<Settings>(Encoding.UTF8.GetString(b));}finally{Array.Clear(b,0,b.Length);}
 }
 public static Settings Load() {return File.Exists(Config)?Unseal(File.ReadAllBytes(Config)):null;}
 public static void Save(Settings s) {var b=Seal(s); try{Atomic(Config,b);}finally{Array.Clear(b,0,b.Length);}}
 public static void Validate(Settings s) {
  if(s==null || !Regex.IsMatch(s.Tunnel??"",@"\Atunnel_[A-Za-z0-9_-]{8,160}\z")) throw new Exception("터널 ID는 tunnel_ 로 시작하는 값을 입력하세요.");
  if(String.IsNullOrWhiteSpace(s.Key) || s.Key.Length<16 || s.Key.Any(Char.IsWhiteSpace)) throw new Exception("API 키를 확인하세요. 공백·줄바꿈 없이 입력해야 합니다.");
  s.Shell=false; // This build exposes no unsandboxed shell.

 }
 public static Process Worker() {
  try {
   var s=Json().Deserialize<ViewState>(File.ReadAllText(Path.Combine(Root,"state.json")));
   var p=Process.GetProcessById(s.Pid);
   if(p.HasExited || p.StartTime.ToUniversalTime().Ticks!=s.StartTicks || !String.Equals(p.MainModule.FileName,Exe,StringComparison.OrdinalIgnoreCase)) {p.Dispose();return null;}
   return p;
  } catch{return null;}
 }
 public static ViewState State() {
  try {var s=Json().Deserialize<ViewState>(File.ReadAllText(Path.Combine(Root,"state.json"))); using(var p=Worker()){if(p!=null) return s;} return new ViewState{Message=File.Exists(Config)?"연결 꺼짐":"처음 설정이 필요합니다."};}
  catch{return new ViewState{Message=File.Exists(Config)?"연결 꺼짐":"처음 설정이 필요합니다."};}
 }
 public static void Record(string phase,string message,bool ready,string request,bool applied) {
  using(var p=Process.GetCurrentProcess()) {
   var s=new ViewState{Phase=phase,Message=message,Ready=ready,Pid=p.Id,StartTicks=p.StartTime.ToUniversalTime().Ticks,Request=request,Applied=applied};
   Atomic(Path.Combine(Root,"state.json"),Encoding.UTF8.GetBytes(Json().Serialize(s)));
  }
 }
 public static bool Auto() {using(var k=Registry.CurrentUser.OpenSubKey(RunKey)) return k!=null && (k.GetValue(RunName) as string)==Q(Exe)+" --agent";}
 public static void SetAuto(bool value) {
  using(var k=Registry.CurrentUser.CreateSubKey(RunKey)){if(value)k.SetValue(RunName,Q(Exe)+" --agent");else k.DeleteValue(RunName,false);}
  if(Auto()!=value)throw new Exception("자동 시작 설정을 저장하지 못했습니다.");
 }
 public static void Stop() {
  using(var p=Worker()) {
   if(p==null)return;
   try{using(var e=EventWaitHandle.OpenExisting(EventName))e.Set();}catch(WaitHandleCannotBeOpenedException){throw new Exception("연결 종료 신호를 전달하지 못했습니다. 잠시 후 다시 시도하세요.");}
   if(!p.WaitForExit(20000))throw new Exception("아직 종료 중입니다. 폴더는 삭제하지 않았습니다.");
  }
 }
 public static Process Spawn(Settings candidate,string request) {
  var psi=new ProcessStartInfo(Exe,"--agent "+request){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=candidate!=null};
  var p=Process.Start(psi);
  if(candidate!=null) {var b=Seal(candidate); try{p.StandardInput.WriteLine(Convert.ToBase64String(b));p.StandardInput.Close();}finally{Array.Clear(b,0,b.Length);}}
  return p;
 }
 public static void WriteBridge(Settings s,string directory=null) {ScopeStore.Locked(()=>{ScopeStore.Init();return 0;});}
 public static ProcessStartInfo Cli(Settings s,string verb,bool probe=false) {
  string url=Path.Combine(Root,"health.url");
  var cfg=new {config_version=1,control_plane=new {api_key="env:CONTROL_PLANE_API_KEY",base_url="https://api.openai.com",tunnel_id=s.Tunnel},mcp=new {commands=new[]{new {channel="main",command=Q(Exe.Replace('\\','/'))+(probe?" --mcp-probe":" --mcp-server")}}},health=new {listen_addr="127.0.0.1:0",url_file=url},log=new {file="stdout",format="json",level="warn"}};
  string profile=Path.Combine(Root,probe?"probe-config.json":"runtime-config.json");
  Atomic(profile,Encoding.UTF8.GetBytes(Json().Serialize(cfg)));
  var psi=new ProcessStartInfo(Bin,verb+" --config "+Q(profile)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Root};
  // Do not inherit unrelated OpenAI credentials or tunnel overrides.
  foreach(string k in psi.EnvironmentVariables.Keys.Cast<string>().ToArray())
   if(k.StartsWith("CONTROL_PLANE_")||k.StartsWith("TUNNEL_CLIENT_")||k.StartsWith("CLOUDFLARED_")||k.StartsWith("MCP_")||k.StartsWith("HEALTH_")||k.StartsWith("HARPOON_")||k.StartsWith("LOG_")||k=="OPENAI_API_KEY"||k=="OPENAI_ADMIN_KEY"||k=="ALLOW_REMOTE_UI") psi.EnvironmentVariables.Remove(k);
  psi.EnvironmentVariables["CONTROL_PLANE_API_KEY"]=s.Key;
  psi.EnvironmentVariables["XDG_CONFIG_HOME"]=Path.Combine(Root,"isolated-config");
  psi.EnvironmentVariables["XDG_STATE_HOME"]=Path.Combine(Root,"isolated-state");
  return psi;
 }
 public static string SafeError(string raw) {
  string t=(raw??"").ToLowerInvariant();
  if(t.Contains("401")||t.Contains("unauthorized")||t.Contains("invalid_api_key"))return "API 키 인증에 실패했습니다. 키를 확인하거나 새로 발급하세요.";
  if(t.Contains("403")||t.Contains("forbidden"))return "터널 접근 권한이 없습니다. 실행용 키의 Tunnels Read / Use 권한을 확인하세요.";
  if(t.Contains("404")||t.Contains("not found"))return "터널을 찾지 못했습니다. ID와 소속 조직을 확인하세요.";
  if(t.Contains("timeout")||t.Contains("no such host")||t.Contains("network"))return "네트워크 연결을 확인하세요. 잠시 후 다시 시도할 수 있습니다.";
  return "연결 검증에 실패했습니다. 키·터널 ID·인터넷 연결을 확인하세요. 원문 로그는 민감정보 보호를 위해 표시하지 않습니다.";
 }
 public static void Doctor(Settings s) {
  Validate(s); WriteBridge(s);
  using(var job=new ChildJob()) using(var p=new Process()) {
   p.StartInfo=Cli(s,"doctor --json",true); p.Start(); job.Attach(p);p.StartInfo.EnvironmentVariables.Remove("CONTROL_PLANE_API_KEY");
   var a=p.StandardOutput.ReadToEndAsync();var b=p.StandardError.ReadToEndAsync();
   if(!p.WaitForExit(45000))throw new Exception("연결 진단 시간 초과. 기존 설정은 유지됩니다.");
   if(p.ExitCode!=0)throw new Exception(SafeError(a.Result+"\n"+b.Result));
  }
 }
 public static bool HttpReady() {
  try {
   if(!File.Exists(Path.Combine(Root,"health.url")))return false;
   var u=new Uri(File.ReadAllText(Path.Combine(Root,"health.url")).Trim());
   if(u.Scheme!="http"||u.Host!="127.0.0.1")return false;
   var r=(HttpWebRequest)WebRequest.Create(u.AbsoluteUri.TrimEnd('/')+"/readyz");r.Proxy=null;r.Timeout=1800;
   using(var resp=(HttpWebResponse)r.GetResponse())return resp.StatusCode==HttpStatusCode.OK;
  }catch{return false;}
 }
 public static string ReadResource(string name){using(var r=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(name),Encoding.UTF8))return r.ReadToEnd();}
 public static void Share(string dest) {
  string full=Path.GetFullPath(dest);
  if(full.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("공유 파일은 설정 폴더 밖에 저장하세요.");
  if(String.Equals(full,Self,StringComparison.OrdinalIgnoreCase))throw new Exception("현재 실행 중인 EXE와 다른 이름을 선택하세요.");
  File.Copy(Self,full,true);
  if(Hash(Self)!=Hash(full))throw new Exception("공유 파일 무결성 검증에 실패했습니다.");
 }
 public static void AssertOwned() {
  NoLinks(Root);
  if(!Directory.Exists(Root))return;
  if(File.ReadAllText(Path.Combine(Root,".pcbridge-owner"))!=Marker)throw new Exception("소유 확인 실패: 폴더를 삭제하지 않습니다.");
  CheckTree(Root);
 }
 static void CheckTree(string dir) {
  foreach(var p in Directory.GetFileSystemEntries(dir)) {
   var a=File.GetAttributes(p);if((a&FileAttributes.ReparsePoint)!=0)throw new Exception("전용 폴더 안에 링크가 있습니다. 자동 삭제를 중단했습니다.");
   if((a&FileAttributes.Directory)!=0)CheckTree(p);
  }
 }

}

sealed class ChildJob:IDisposable {
 IntPtr handle;
 [StructLayout(LayoutKind.Sequential)]struct Basic {public long PerProcess,PerJob;public uint Flags;public UIntPtr Min,Max;public uint Count;public UIntPtr Affinity;public uint Priority,Scheduling;}
 [StructLayout(LayoutKind.Sequential)]struct IO {public ulong A,B,C,D,E,F;}
 [StructLayout(LayoutKind.Sequential)]struct Extended {public Basic Basic;public IO Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr CreateJobObject(IntPtr a,string n);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr h,int c,IntPtr p,uint l);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr h,IntPtr p);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
 public ChildJob(){handle=CreateJobObject(IntPtr.Zero,null);var e=new Extended();e.Basic.Flags=0x2000;int n=Marshal.SizeOf(e);var p=Marshal.AllocHGlobal(n);try{Marshal.StructureToPtr(e,p,false);if(!SetInformationJobObject(handle,9,p,(uint)n))throw new Exception("프로세스 보호 설정 실패");}finally{Marshal.FreeHGlobal(p);}}
 public void Attach(Process p){if(!AssignProcessToJobObject(handle,p.Handle)){try{p.Kill();}catch{}throw new Exception("하위 프로세스 종료 보호를 설정하지 못했습니다.");}}
 public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
}
sealed class Session:IDisposable {
 ChildJob job;Process p;
 public Session(Settings s){Core.WriteBridge(s);string h=Path.Combine(Core.Root,"health.url");if(File.Exists(h))File.Delete(h);job=new ChildJob();try{p=new Process{StartInfo=Core.Cli(s,"run")};p.OutputDataReceived+=(a,b)=>{};p.ErrorDataReceived+=(a,b)=>{};p.Start();job.Attach(p);p.StartInfo.EnvironmentVariables.Remove("CONTROL_PLANE_API_KEY");p.BeginOutputReadLine();p.BeginErrorReadLine();}catch{Dispose();throw;}}
 public bool Alive{get{return p!=null&&!p.HasExited;}}
 public bool AwaitReady(EventWaitHandle stop){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<40000){if(stop.WaitOne(500)||!Alive)return false;if(Core.HttpReady())return true;}return false;}
 public void Dispose(){if(job!=null){job.Dispose();job=null;}if(p!=null){try{p.WaitForExit(5000);}catch{}p.Dispose();p=null;}}
}
static class Agent {
 public static int Run(string request) {
  using(var singleton=new Mutex(false,"Local\\"+Core.Instance+"-agent")) {
   if(!singleton.WaitOne(0))return 2;
   using(var stop=new EventWaitHandle(false,EventResetMode.ManualReset,Core.EventName)) {
    stop.Reset();Settings previous=null,current=null;bool applied=false;Session session=null;
    try {
     previous=Core.Load();
     bool candidate=!String.IsNullOrEmpty(request);
     if(candidate){string input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8,true).ReadLine();current=Core.Unseal(Convert.FromBase64String(input));}else current=previous;
     Core.Validate(current);
     Core.Record("starting","연결 시작 중입니다.",false,request,false);
     if(candidate) {
      try {
       session=new Session(current);
       if(!session.AwaitReady(stop))throw new Exception("새 설정의 실제 연결을 확인하지 못했습니다.");
       if(stop.WaitOne(0))return 0;
       Core.Save(current);applied=true;previous=null;
      } catch {
       if(session!=null){session.Dispose();session=null;}
       if(previous==null){Core.Record("failed","설정 저장 안 됨: 새 연결을 확인하지 못했습니다.",false,request,false);return 3;}
       current=previous;Core.WriteBridge(current);
       Core.Record("rollback","새 설정 적용 실패. 기존 설정으로 복구 중입니다.",false,request,false);
      }
     }
     int backoff=5;
     while(!stop.WaitOne(0)) {
      if(session==null) {
       Core.Record("connecting",candidate&&!applied?"기존 설정으로 다시 연결 중입니다.":"연결 중입니다.",false,request,applied);
       try {session=new Session(current);if(!session.AwaitReady(stop)){session.Dispose();session=null;}}
       catch{if(session!=null)session.Dispose();session=null;}
      }
      if(stop.WaitOne(0))break;
      if(session!=null && session.Alive && Core.HttpReady()) {
       backoff=5;Core.Record("ready",candidate&&!applied?"새 설정 실패 · 기존 설정으로 연결됨":"연결 켜짐 · 정상",true,request,applied);
       // Slow health checks also recover after network/sleep changes. No raw logs are retained.
       if(stop.WaitOne(5000))break;
       if(session.Alive) {
        if(Core.HttpReady())continue;
        Core.Record("reconnecting","네트워크 복구를 기다리는 중입니다.",false,request,applied);
        if(stop.WaitOne(15000))break;
        if(session.Alive&&Core.HttpReady())continue;
       }
       session.Dispose();session=null;
      } else {
       if(session!=null){session.Dispose();session=null;}
       Core.Record("retry","연결 실패 · 자동 재시도 대기 중. 키·권한·네트워크를 확인하세요.",false,request,applied);
       if(stop.WaitOne(backoff*1000))break;backoff=Math.Min(60,backoff*2);
      }
     }
     return 0;
    }catch{try{Core.Record("failed","설정을 읽거나 연결을 시작하지 못했습니다. 설정 창에서 확인하세요.",false,request,false);}catch{}return 1;}
    finally{if(session!=null)session.Dispose();foreach(string f in new[]{"runtime-config.json","health.url"})try{File.Delete(Path.Combine(Core.Root,f));}catch{}singleton.ReleaseMutex();}
   }
  }
 }
}

sealed class MainWindow:Form {
 Label status=new Label(),sub=new Label();Button start,stop,setup,share,readme,remove,diagnostics;CheckBox auto=new CheckBox();
 System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();bool busy=false,loading=false;NotifyIcon tray;
 public MainWindow() {
  ScopeStore.Locked(()=>{ScopeStore.Init();return 0;});
  Text="PCBridge Portable · "+Core.Version;Font=new Font("Malgun Gothic",10);ClientSize=new Size(570,445);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
  Controls.Add(new Label{Text="PCBridge",Font=new Font("Malgun Gothic",23,FontStyle.Bold),Bounds=new Rectangle(22,15,500,45)});
  status.SetBounds(25,73,520,30);sub.SetBounds(25,110,520,43);sub.Text="창을 닫아도 연결은 유지됩니다. 끄려면 ‘연결 중지’를 누르세요.";Controls.Add(status);Controls.Add(sub);
  start=Button("연결 시작",25,165,()=>Do(async()=>{if(Core.Load()==null){SettingsDialog();return;}using(var p=Core.Worker()){if(p==null)Core.Spawn(null,"").Dispose();}await Task.Delay(500);}));
  stop=Button("연결 중지",200,165,()=>Do(()=>Task.Run(()=>Core.Stop())));
  setup=Button("설정 / 키 교체",375,165,SettingsDialog);
  share=Button("공유용 EXE 저장",25,215,Share);
  readme=Button("README · 사용 안내",200,215,()=>new ReadmeWindow().ShowDialog(this));
  remove=Button("작업 권한",375,215,()=>new PermissionWindow().ShowDialog(this));
  Button("복구함 / 삭제 기록",25,320,()=>new RecoveryWindow().ShowDialog(this));
  Button("데이터 위치",200,320,()=>MessageBox.Show(this,"설정·감사 로그·복구함 위치:\r\n"+Core.Root+"\r\n복구함 내용이 있으므로 자동 완전 삭제는 제공하지 않습니다."));
  Button("도구 권한 / 하드락",25,375,()=>new DesktopWindow().ShowDialog(this));
  Button("오픈소스 라이선스",200,375,()=>System.Diagnostics.Process.Start("notepad.exe",Core.Q(Path.Combine(Core.Root,"THIRD_PARTY_NOTICES.txt"))));
  auto.Text="Windows 로그인 시 자동 시작";auto.SetBounds(25,269,330,30);Controls.Add(auto);
  loading=true;auto.Checked=Core.Auto();loading=false;
  auto.CheckedChanged+=(a,b)=>{if(loading)return;try{if(auto.Checked&&Core.Load()==null)throw new Exception("먼저 연결 설정을 완료하세요.");Core.SetAuto(auto.Checked);}catch(Exception e){MessageBox.Show(this,e.Message);loading=true;auto.Checked=Core.Auto();loading=false;}};
  diagnostics=Button("진단 정보",375,265,Diagnostics);
  tray=new NotifyIcon{Icon=SystemIcons.Application,Text="PCBridge Portable",Visible=true};
  var menu=new ContextMenuStrip();menu.Items.Add("연결 관리 열기",null,(a,b)=>{Show();WindowState=FormWindowState.Normal;Activate();});menu.Items.Add("연결 중지",null,(a,b)=>Do(()=>Task.Run(()=>Core.Stop())));menu.Items.Add("README",null,(a,b)=>new ReadmeWindow().ShowDialog());menu.Items.Add("관리 창 닫기 (연결 유지)",null,(a,b)=>Close());tray.ContextMenuStrip=menu;tray.DoubleClick+=(a,b)=>{Show();Activate();};
  timer.Interval=2000;timer.Tick+=(a,b)=>RefreshState();timer.Start();RefreshState();
  Shown+=(a,b)=>{if(Core.Load()==null&&!Core.Instance.StartsWith("PCBridge-Integrated-Test-"))SettingsDialog();};
  FormClosing+=(a,b)=>{if(busy){b.Cancel=true;MessageBox.Show(this,"진행 중인 작업이 끝난 뒤 닫아 주세요.");}};
  FormClosed+=(a,b)=>{timer.Stop();tray.Dispose();};
 }
 Button Button(string text,int x,int y,Action action){var b=new Button{Text=text,Bounds=new Rectangle(x,y,165,36)};b.Click+=(a,e)=>action();Controls.Add(b);return b;}
 async void Do(Func<Task> work){if(busy)return;busy=true;foreach(Control c in Controls)if(c is Button||c is CheckBox)c.Enabled=false;try{await work();}catch(Exception e){MessageBox.Show(this,e.Message,"PCBridge");}finally{busy=false;foreach(Control c in Controls)if(c is Button||c is CheckBox)c.Enabled=true;RefreshState();}}
 void RefreshState(){var s=Core.State();status.Text=s.Message;status.ForeColor=s.Ready?Color.DarkGreen:Color.DimGray;tray.Text=s.Ready?"PCBridge · 연결 정상":"PCBridge · "+(s.Phase=="off"?"연결 꺼짐":"연결 확인 중");}
 void SettingsDialog(){if(busy)return;using(var f=new SetupWindow())f.ShowDialog(this);RefreshState();}
 void Share(){using(var d=new SaveFileDialog{Filter="프로그램 (*.exe)|*.exe",FileName="PCBridge-Portable.exe",OverwritePrompt=true})if(d.ShowDialog(this)==DialogResult.OK)Do(()=>Task.Run(()=>{Core.Share(d.FileName);MessageBox.Show("공유용 EXE 저장 및 파일 일치 확인 완료. 키·터널 ID·설정·로그는 포함하지 않았습니다.","PCBridge");}));}
 void Diagnostics(){using(var f=new Form{Text="진단 정보 (키·터널 ID 제외)",Size=new Size(610,360),StartPosition=FormStartPosition.CenterParent}){var s=Core.State();var t=new TextBox{Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,Text="버전: "+Core.Version+"\r\n상태: "+s.Phase+"\r\n정상 응답: "+s.Ready+"\r\n설정 저장: "+File.Exists(Core.Config)+"\r\n자동 시작: "+Core.Auto()+"\r\n전용 폴더: "+Core.Root+"\r\n\r\n원문 통신 로그와 키는 기록하거나 내보내지 않습니다.\r\n프로그램 실행·터널 준비 상태와 ChatGPT 도구 호출 성공은 별도입니다.\r\nChatGPT에서 bridge_status 호출로 최종 확인하세요."};f.Controls.Add(t);f.ShowDialog(this);}}

}

sealed class SetupWindow:Form {
 TextBox tunnel=new TextBox(),key=new TextBox(),folder=new TextBox();CheckBox shell=new CheckBox();Label message=new Label();Button apply;bool busy=false;Settings old;
 public SetupWindow(){
  old=Core.Load();Text=old==null?"처음 연결하기":"설정 / API 키 교체";Font=new Font("Malgun Gothic",10);ClientSize=new Size(660,555);StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;
  AddLabel("1. 준비: ChatGPT에서 MCP 앱과 터널 설정을 사용할 수 있어야 합니다.",15,12,630,25);
  AddLink("터널 만들기 / ID 확인",15,45,"https://platform.openai.com/settings/organization/tunnels");AddLink("API 키 발급",300,45,"https://platform.openai.com/settings/organization/api-keys");
  AddLabel("2. 실행용 API 키를 발급하세요. Tunnels Read / Use 권한이 필요합니다.\r\n관리자 키를 넣지 마세요. 키는 이 PC의 Windows 계정으로 암호화합니다.",15,80,630,45);
  AddLabel("터널 ID",15,140,95,25);tunnel.SetBounds(115,136,520,28);tunnel.Text=old==null?"":old.Tunnel;
  AddLabel("API 키",15,180,95,25);key.SetBounds(115,176,520,28);key.UseSystemPasswordChar=true;
  AddLabel(old==null?"키 원문은 EXE·로그·공유 파일에 포함하지 않습니다.":"교체하려면 새 키 입력. 빈칸이면 저장된 키를 유지합니다.",115,209,520,25);
  folder.Text=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);folder.Visible=false;shell.Visible=false;shell.Checked=false;
  var browse=new Button{Visible=false};
  AddLabel("접근 권한은 작업마다 요청하고 메인 화면의 ‘작업 권한’에서 승인합니다.\r\n추가 경로: 매번 승인 / 기록 후 허용을 작업별로 선택합니다.\r\n확장 도구는 4급 자동, 3/2급 제한적 작업 lease, 1급 HARDLOCK으로 제어합니다.\r\n작업 lease는 PC 재부팅 또는 만료 시 초기화됩니다.",15,249,630,105);
  AddLabel("3. 검증 후 저장 → 4. ChatGPT에서 MCP 앱 추가 → bridge_status 호출",15,374,630,25);
  AddLink("ChatGPT 앱 설정 열기",15,410,"https://chatgpt.com/#settings/Connectors");
  var copy=new Button{Text="터널 ID 복사",Bounds=new Rectangle(305,402,155,32)};copy.Click+=(a,b)=>{if(tunnel.Text.Length>0)Clipboard.SetText(tunnel.Text.Trim());};
  var help=new Button{Text="README",Bounds=new Rectangle(475,402,160,32)};help.Click+=(a,b)=>new ReadmeWindow().ShowDialog(this);
  message.SetBounds(15,446,620,43);message.Text="검증에 실패하면 기존 키를 지우지 않습니다.";
  apply=new Button{Text="연결 검증 후 저장",Bounds=new Rectangle(405,500,230,36)};apply.Click+=async(a,b)=>await Apply();
  Controls.AddRange(new Control[]{tunnel,key,folder,shell,browse,copy,help,message,apply});
  FormClosing+=(a,b)=>{if(busy)b.Cancel=true;};
 }
 void AddLabel(string s,int x,int y,int w,int h){Controls.Add(new Label{Text=s,Bounds=new Rectangle(x,y,w,h)});}
 void AddLink(string s,int x,int y,string url){var l=new LinkLabel{Text=s,Bounds=new Rectangle(x,y,285,25)};l.LinkClicked+=(a,b)=>{try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch{MessageBox.Show(url);}};Controls.Add(l);}
 async Task Apply(){
  if(busy)return;
  Settings next=new Settings{Tunnel=tunnel.Text.Trim(),Key=String.IsNullOrWhiteSpace(key.Text)&&old!=null?old.Key:key.Text.Trim(),Folder=folder.Text,Shell=shell.Checked};
  try{Core.Validate(next);}catch(Exception e){message.Text=e.Message;return;}
  if(next.Shell&&(old==null||!old.Shell)&&MessageBox.Show(this,"AI가 사용자 권한으로 명령을 실행할 수 있습니다. 허용하시겠습니까?","PowerShell 허용",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
  busy=true;foreach(Control c in Controls)c.Enabled=false;message.Enabled=true;message.Text="키·권한·서버 진단 중입니다. 기존 연결은 유지됩니다.";
  try {
   await Task.Run(()=>Core.Doctor(next));
   message.Text="새 연결을 검증 중입니다. 실패하면 기존 설정으로 복구합니다.";
   string request=Guid.NewGuid().ToString("N");
   await Task.Run(()=>Core.Stop());
   using(var p=Core.Spawn(next,request)) {
    bool success=false;
    for(int i=0;i<110;i++){await Task.Delay(700);var s=Core.State();if(s.Request==request&&s.Applied&&s.Ready){success=true;break;}if(p.HasExited)break;if(s.Request==request&&!s.Applied&&(s.Ready||s.Phase=="rollback"||s.Phase=="retry"||s.Phase=="failed"))break;}
    if(!success)throw new Exception("새 설정 적용을 확인하지 못했습니다. 기존 저장 설정은 유지됩니다. 메인 화면에서 연결 상태를 확인하세요.");
   }
   key.Clear();old=next;message.Text="저장 완료. 이전 키의 로컬 저장값은 교체됐습니다. 이전 키의 플랫폼 Revoke는 별도입니다.";
   MessageBox.Show(this,"새 연결 검증과 저장이 완료됐습니다.\r\nChatGPT에서 앱을 연결한 뒤 bridge_status를 호출하세요.\r\n기존 API 키는 필요 없으면 플랫폼에서 Revoke하세요.","PCBridge");
  } catch(Exception e){message.Text=e.Message;}
  finally{busy=false;foreach(Control c in Controls)c.Enabled=true;}
 }
}
sealed class ReadmeWindow:Form {
 public ReadmeWindow(){Text="README · PCBridge Portable";Size=new Size(790,640);StartPosition=FormStartPosition.CenterParent;var text=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Malgun Gothic",10),Text=Core.ReadResource("PCBridge.Readme").Replace("\n","\r\n")};Controls.Add(text);}
}

static class Program {
 [STAThread]static int Main(string[] args){
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  try {
   if(args.Length>0&&args[0]=="--cleanup")return 9; // No permanent data removal in this build.
   if(args.Length>0&&args[0]=="--integration-test")return IntegrationTests.Run();
   if(args.Length>0&&args[0]=="--self-test")return ScopeTests.Run();
   if(args.Length>0&&args[0]=="--mcp-server")return ScopeEngine.Serve();
   if(args.Length>1&&args[0]=="--chat-relay")return DesktopIntegration.RunRelay(args[1]);
   if(args.Length>0&&args[0]=="--mcp-probe")return ScopeEngine.Serve(true);
   Core.Prepare();
   ScopeStore.Locked(()=>{ScopeStore.Init();return 0;});
   if(args.Length>0&&args[0]=="--desktop-approval"){using(var guard=new Mutex(false,"Local\\"+Core.Instance+"-desktop-approval-ui")){if(!guard.WaitOne(0))return 0;try{Application.Run(new DesktopWindow(args.Length>1?args[1]:null));return 0;}finally{guard.ReleaseMutex();}}}
   if(args.Length>0&&args[0]=="--agent")return Agent.Run(args.Length>1?args[1]:"");
   if(args.Length>0&&args[0]=="--render-test"){if(!Core.Instance.StartsWith("PCBridge-Integrated-Test-"))return 9;using(var f=new SetupWindow())using(var bmp=new Bitmap(f.Width,f.Height)){f.Show();Application.DoEvents();f.DrawToBitmap(bmp,new Rectangle(0,0,bmp.Width,bmp.Height));f.Hide();bmp.Save(Path.Combine(Path.GetDirectoryName(Core.Self),"onboarding-preview.png"));}return 0;}
   if(args.Length>0&&args[0]=="--status"){File.WriteAllText(Path.Combine(Core.Root,"test-status.json"),Core.Json().Serialize(Core.State()));return 0;}
   if(args.Length>0&&args[0]=="--doctor-stdin"){if(!Core.Instance.StartsWith("PCBridge-Integrated-Test-"))return 9;var s=Core.Unseal(Convert.FromBase64String(new StreamReader(Console.OpenStandardInput(),Encoding.UTF8,true).ReadLine()));Core.Doctor(s);File.WriteAllText(Path.Combine(Core.Root,"doctor-result.txt"),"PASS");return 0;}
   using(var guard=new Mutex(false,"Local\\"+Core.Instance+"-ui")){if(!guard.WaitOne(0)){MessageBox.Show("이미 연결 관리 창이 실행 중입니다.");return 0;}Application.Run(new MainWindow());guard.ReleaseMutex();}return 0;
  }catch(Exception e){if(args.Length>0){try{Directory.CreateDirectory(Core.Root);File.WriteAllText(Path.Combine(Core.Root,"operation-error.txt"),e.GetType().Name+": "+e.Message);}catch{}return 1;}MessageBox.Show(e.Message,"PCBridge");return 1;}
 }
}