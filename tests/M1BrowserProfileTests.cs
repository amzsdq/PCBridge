using System;
using System.IO;
using System.Linq;

static class M1BrowserProfileTests {
 static int passed;
 static void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed++;}
 static void Expect(Action a,string fragment,string name) {
  bool hit=false;try{a();}catch(Exception e){hit=e.Message.IndexOf(fragment,StringComparison.OrdinalIgnoreCase)>=0;}
  Check(hit,name);
 }

 public static int Main(string[] args) {
  try {
   string baseRoot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"M1BrowserTestArtifacts","run-"+Guid.NewGuid().ToString("N"));
   string fakeBrowser=Path.Combine(baseRoot,"runtime","cft","chrome.exe");
   string extension=Path.Combine(baseRoot,"provider","chatgpt-extension");
   Directory.CreateDirectory(Path.GetDirectoryName(fakeBrowser));
   File.WriteAllBytes(fakeBrowser,new byte[]{77,90});
   Directory.CreateDirectory(extension);
   File.WriteAllText(Path.Combine(extension,"manifest.json"),"{\"manifest_version\":3,\"name\":\"PCBridge M1 ChatGPT Companion\"}");

   var manager=new M1BrowserProfileManager(baseRoot,fakeBrowser,extension);
   Check(manager.IsIsolatedFromNormalProfile(),"dedicated profile is not normal Chrome/Edge profile");
   manager.EnsureOwnedProfile();
   Check(File.Exists(Path.Combine(manager.ProfileRoot,".pcbridge-browser-profile")),"profile ownership marker created");
   manager.EnsureOwnedProfile();
   Check(true,"profile ownership marker re-opened");
   manager.ValidateCompanion();Check(true,"companion manifest validated");

   string foreign=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"M1BrowserTestArtifacts","foreign-"+Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(Path.Combine(foreign,"browser","chatgpt-default"));
   File.WriteAllText(Path.Combine(foreign,"browser","chatgpt-default","user-file.txt"),"do not touch");
   var unsafeManager=new M1BrowserProfileManager(foreign,fakeBrowser,extension);
   Expect(delegate{unsafeManager.EnsureOwnedProfile();},"not_owned","foreign browser profile refused");

   var discovered=manager.Discover();
   Check(discovered.Count>=1 && discovered.All(delegate(M1BrowserCandidate x){return File.Exists(x.executable);}),"discovery returns owned/configured runtimes only");
   Check(discovered.All(delegate(M1BrowserCandidate x){return x.companion_capable;}),"selected runtimes support companion loading");

   var plan=manager.Plan("https://chatgpt.com/");
   Check(plan.executable==Path.GetFullPath(fakeBrowser),"configured testing runtime selected");
   Check(plan.arguments.IndexOf("--user-data-dir=",StringComparison.Ordinal)>=0,"launch plan pins dedicated user-data-dir");
   Check(plan.arguments.IndexOf("--start-minimized",StringComparison.Ordinal)>=0,"launch plan requests minimized startup");
   Check(plan.arguments.IndexOf("--load-extension=",StringComparison.Ordinal)>=0,"companion extension explicitly loaded");
   Check(plan.arguments.IndexOf("--disable-extensions-except=",StringComparison.Ordinal)>=0,"dedicated profile extension surface restricted");
   Check(plan.arguments.IndexOf(manager.ExtensionRoot,StringComparison.OrdinalIgnoreCase)>=0,"launch plan uses exact companion root");
   var psi=manager.StartInfo(plan);
   Check(!psi.UseShellExecute && psi.CreateNoWindow && psi.WindowStyle==System.Diagnostics.ProcessWindowStyle.Minimized,"start info avoids console and requests minimized window");

   var missing=new M1BrowserProfileManager(Path.Combine(baseRoot,"missing"),"",extension);
   Expect(delegate{missing.Plan("https://chatgpt.com/");},"browser_runtime_required","normal installed Chrome is not silently used as extension runtime");
   Expect(delegate{manager.Plan("http://example.com/");},"browser_url_invalid","non-ChatGPT URL refused");

   Console.WriteLine("M1 browser profile tests PASS: "+passed);
   Console.WriteLine("profile_root="+manager.ProfileRoot);
   return 0;
  } catch(Exception e) {
   Console.Error.WriteLine(e.ToString());return 1;
  }
 }
}
