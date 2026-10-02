using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

public sealed class M1BrowserCandidate {
 public string name="";
 public string executable="";
 public string source="";
}

public sealed class M1BrowserLaunchPlan {
 public string browser="";
 public string executable="";
 public string profile_root="";
 public string arguments="";
 public bool start_minimized=true;
}

public sealed class M1BrowserProfileManager {
 readonly string root;
 readonly string profileRoot;
 const string MarkerText="PCBridge M1 dedicated browser profile v1";

 public M1BrowserProfileManager(string pcbridgeRoot) {
  if(String.IsNullOrWhiteSpace(pcbridgeRoot))throw new ArgumentException("pcbridgeRoot");
  root=Path.GetFullPath(pcbridgeRoot);
  profileRoot=Path.Combine(root,"browser","chatgpt-default");
 }

 public string ProfileRoot { get { return profileRoot; } }

 public void EnsureOwnedProfile() {
  string marker=Path.Combine(profileRoot,".pcbridge-browser-profile");
  if(Directory.Exists(profileRoot)) {
   if(!File.Exists(marker)) {
    if(Directory.EnumerateFileSystemEntries(profileRoot).Any())
     throw new InvalidOperationException("browser_profile_not_owned");
   } else if(File.ReadAllText(marker)!=MarkerText) {
    throw new InvalidOperationException("browser_profile_marker_invalid");
   }
  }
  Directory.CreateDirectory(profileRoot);
  if(!File.Exists(marker))File.WriteAllText(marker,MarkerText);
  if(File.ReadAllText(marker)!=MarkerText)throw new InvalidOperationException("browser_profile_marker_write_failed");
 }

 public List<M1BrowserCandidate> Discover() {
  var result=new List<M1BrowserCandidate>();
  Add(result,"chrome",RegistryPath(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),"HKCU App Paths");
  Add(result,"chrome",RegistryPath(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),"HKLM App Paths");
  Add(result,"edge",RegistryPath(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"),"HKCU App Paths");
  Add(result,"edge",RegistryPath(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"),"HKLM App Paths");

  string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  string pf=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
  string pfx86=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
  Add(result,"chrome",Path.Combine(local,"Google","Chrome","Application","chrome.exe"),"standard");
  Add(result,"chrome",Path.Combine(pf,"Google","Chrome","Application","chrome.exe"),"standard");
  Add(result,"chrome",Path.Combine(pfx86,"Google","Chrome","Application","chrome.exe"),"standard");
  Add(result,"edge",Path.Combine(pf,"Microsoft","Edge","Application","msedge.exe"),"standard");
  Add(result,"edge",Path.Combine(pfx86,"Microsoft","Edge","Application","msedge.exe"),"standard");

  return result
   .Where(delegate(M1BrowserCandidate x){return File.Exists(x.executable);})
   .GroupBy(delegate(M1BrowserCandidate x){return Path.GetFullPath(x.executable).ToUpperInvariant();})
   .Select(delegate(IGrouping<string,M1BrowserCandidate> g){return g.First();})
   .ToList();
 }

 public M1BrowserCandidate Select(string preferred) {
  var browsers=Discover();
  string want=(preferred??"chrome").Trim().ToLowerInvariant();
  var selected=browsers.FirstOrDefault(delegate(M1BrowserCandidate x){return x.name==want;});
  if(selected==null && want!="chrome")selected=browsers.FirstOrDefault(delegate(M1BrowserCandidate x){return x.name=="chrome";});
  if(selected==null)selected=browsers.FirstOrDefault(delegate(M1BrowserCandidate x){return x.name=="edge";});
  if(selected==null)throw new InvalidOperationException("browser_setup_required");
  return selected;
 }

 public M1BrowserLaunchPlan Plan(string preferred,string initialUrl) {
  EnsureOwnedProfile();
  var b=Select(preferred);
  string url=String.IsNullOrWhiteSpace(initialUrl)?"https://chatgpt.com/":ValidateUrl(initialUrl);
  string args=
   "--user-data-dir="+Quote(profileRoot)+" "+
   "--profile-directory=Default "+
   "--no-first-run --no-default-browser-check --start-minimized "+
   "--disable-background-networking=false "+
   Quote(url);
  return new M1BrowserLaunchPlan {
   browser=b.name,
   executable=Path.GetFullPath(b.executable),
   profile_root=profileRoot,
   arguments=args,
   start_minimized=true
  };
 }

 public ProcessStartInfo StartInfo(M1BrowserLaunchPlan plan) {
  if(plan==null)throw new ArgumentNullException("plan");
  if(!File.Exists(plan.executable))throw new FileNotFoundException("browser executable missing",plan.executable);
  if(Path.GetFullPath(plan.profile_root)!=profileRoot)throw new InvalidOperationException("browser_profile_mismatch");
  return new ProcessStartInfo(plan.executable,plan.arguments) {
   UseShellExecute=false,
   CreateNoWindow=true,
   WindowStyle=ProcessWindowStyle.Minimized,
   WorkingDirectory=Path.GetDirectoryName(plan.executable)
  };
 }

 public bool IsIsolatedFromNormalProfile() {
  string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  string chromeNormal=Path.Combine(local,"Google","Chrome","User Data");
  string edgeNormal=Path.Combine(local,"Microsoft","Edge","User Data");
  string ours=Path.GetFullPath(profileRoot).TrimEnd(Path.DirectorySeparatorChar);
  return !ours.Equals(Path.GetFullPath(chromeNormal).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase) &&
         !ours.Equals(Path.GetFullPath(edgeNormal).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);
 }

 static string RegistryPath(string key) {
  try {
   object v=Registry.GetValue(key,"",null);
   return v==null?"":Convert.ToString(v);
  } catch { return ""; }
 }

 static void Add(List<M1BrowserCandidate> list,string name,string path,string source) {
  if(String.IsNullOrWhiteSpace(path))return;
  try {
   string full=Path.GetFullPath(path.Trim().Trim('"'));
   if(File.Exists(full))list.Add(new M1BrowserCandidate{name=name,executable=full,source=source});
  } catch {}
 }

 static string Quote(string value) {
  return "\"" + (value??"").Replace("\"","\\\"") + "\"";
 }

 static string ValidateUrl(string raw) {
  Uri u;
  if(!Uri.TryCreate(raw,UriKind.Absolute,out u))throw new ArgumentException("browser_url_invalid");
  if(u.Scheme!="https")throw new ArgumentException("browser_url_invalid");
  if(!u.Host.Equals("chatgpt.com",StringComparison.OrdinalIgnoreCase) &&
     !u.Host.Equals("chat.openai.com",StringComparison.OrdinalIgnoreCase))
   throw new ArgumentException("browser_url_invalid");
  return u.AbsoluteUri;
 }
}
