using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;

public sealed class M1BrowserCandidate {
 public string name="";
 public string executable="";
 public string source="";
 public bool companion_capable;
}

public sealed class M1BrowserLaunchPlan {
 public string browser="";
 public string executable="";
 public string profile_root="";
 public string extension_root="";
 public string arguments="";
 public bool start_minimized=true;
}

public sealed class M1BrowserProfileManager {
 readonly string root;
 readonly string profileRoot;
 readonly string configuredExecutable;
 readonly string extensionRoot;
 const string MarkerText="PCBridge M1 dedicated browser profile v1";

 public M1BrowserProfileManager(string pcbridgeRoot)
  : this(pcbridgeRoot,Environment.GetEnvironmentVariable("PCBRIDGE_M1_BROWSER_EXE"),null) {}

 public M1BrowserProfileManager(string pcbridgeRoot,string browserExecutable,string companionExtensionRoot) {
  if(String.IsNullOrWhiteSpace(pcbridgeRoot))throw new ArgumentException("pcbridgeRoot");
  root=Path.GetFullPath(pcbridgeRoot);
  profileRoot=Path.Combine(root,"browser","chatgpt-default");
  configuredExecutable=String.IsNullOrWhiteSpace(browserExecutable)?"":Path.GetFullPath(browserExecutable);
  extensionRoot=Path.GetFullPath(String.IsNullOrWhiteSpace(companionExtensionRoot)
   ? Path.Combine(root,"provider","chatgpt-extension")
   : companionExtensionRoot);
 }

 public string ProfileRoot { get { return profileRoot; } }
 public string ExtensionRoot { get { return extensionRoot; } }

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

 public void ValidateCompanion() {
  if(!Directory.Exists(extensionRoot))throw new InvalidOperationException("browser_companion_missing");
  string manifest=Path.Combine(extensionRoot,"manifest.json");
  if(!File.Exists(manifest))throw new InvalidOperationException("browser_companion_manifest_missing");
  string text=File.ReadAllText(manifest);
  if(text.IndexOf("\"manifest_version\"",StringComparison.Ordinal)<0 ||
     text.IndexOf("\"PCBridge M1 ChatGPT Companion\"",StringComparison.Ordinal)<0)
   throw new InvalidOperationException("browser_companion_manifest_invalid");
 }

 public List<M1BrowserCandidate> Discover() {
  var result=new List<M1BrowserCandidate>();
  Add(result,"configured",configuredExecutable,"explicit",true);

  // Chrome branded builds removed --load-extension in Chrome 137 and
  // --disable-extensions-except in Chrome 139. M1 therefore only auto-selects
  // a PCBridge-managed/testing Chromium runtime where unpacked companion loading
  // is an intentional supported use case.
  string[] owned = new[] {
   Path.Combine(root,"browser-runtime","chrome-for-testing","chrome-win64","chrome.exe"),
   Path.Combine(root,"browser-runtime","chrome-for-testing","chrome.exe"),
   Path.Combine(root,"runtime","chrome-for-testing","chrome-win64","chrome.exe"),
   Path.Combine(root,"runtime","chrome-for-testing","chrome.exe"),
   Path.Combine(root,"browser-runtime","chromium","chrome.exe")
  };
  foreach(string path in owned)Add(result,"pcbridge-chromium",path,"pcbridge-runtime",true);

  return result
   .Where(delegate(M1BrowserCandidate x){return File.Exists(x.executable);})
   .GroupBy(delegate(M1BrowserCandidate x){return Path.GetFullPath(x.executable).ToUpperInvariant();})
   .Select(delegate(IGrouping<string,M1BrowserCandidate> g){return g.First();})
   .ToList();
 }

 public M1BrowserCandidate Select() {
  var selected=Discover().FirstOrDefault(delegate(M1BrowserCandidate x){return x.companion_capable;});
  if(selected==null)throw new InvalidOperationException("browser_runtime_required");
  return selected;
 }

 public M1BrowserLaunchPlan Plan(string initialUrl) {
  EnsureOwnedProfile();
  ValidateCompanion();
  var b=Select();
  string url=String.IsNullOrWhiteSpace(initialUrl)?"https://chatgpt.com/":ValidateUrl(initialUrl);
  string args=
   "--user-data-dir="+Quote(profileRoot)+" "+
   "--profile-directory=Default "+
   "--no-first-run --no-default-browser-check --disable-sync --start-minimized "+
   "--disable-extensions-except="+Quote(extensionRoot)+" "+
   "--load-extension="+Quote(extensionRoot)+" "+
   Quote(url);
  return new M1BrowserLaunchPlan {
   browser=b.name,
   executable=Path.GetFullPath(b.executable),
   profile_root=profileRoot,
   extension_root=extensionRoot,
   arguments=args,
   start_minimized=true
  };
 }

 public ProcessStartInfo StartInfo(M1BrowserLaunchPlan plan) {
  if(plan==null)throw new ArgumentNullException("plan");
  if(!File.Exists(plan.executable))throw new FileNotFoundException("browser executable missing",plan.executable);
  if(Path.GetFullPath(plan.profile_root)!=profileRoot)throw new InvalidOperationException("browser_profile_mismatch");
  if(Path.GetFullPath(plan.extension_root)!=extensionRoot)throw new InvalidOperationException("browser_extension_mismatch");
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

 static void Add(List<M1BrowserCandidate> list,string name,string path,string source,bool capable) {
  if(String.IsNullOrWhiteSpace(path))return;
  try {
   string full=Path.GetFullPath(path.Trim().Trim('"'));
   if(File.Exists(full))list.Add(new M1BrowserCandidate{name=name,executable=full,source=source,companion_capable=capable});
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
