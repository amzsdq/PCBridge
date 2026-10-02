using System;
using System.IO;
using System.Linq;

static class M1BrowserProfileTests {
 static int passed;
 static void Check(bool ok,string name) {
  if(!ok)throw new Exception("FAIL: "+name);
  passed++;
 }
 static void Expect(Action a,string fragment,string name) {
  bool hit=false;
  try { a(); } catch(Exception e) { hit=e.Message.IndexOf(fragment,StringComparison.OrdinalIgnoreCase)>=0; }
  Check(hit,name);
 }

 public static int Main(string[] args) {
  try {
   string baseRoot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"M1BrowserTestArtifacts","run-"+Guid.NewGuid().ToString("N"));
   var manager=new M1BrowserProfileManager(baseRoot);
   Check(manager.IsIsolatedFromNormalProfile(),"dedicated profile is not normal Chrome/Edge profile");
   manager.EnsureOwnedProfile();
   Check(File.Exists(Path.Combine(manager.ProfileRoot,".pcbridge-browser-profile")),"profile ownership marker created");
   manager.EnsureOwnedProfile();
   Check(true,"profile ownership marker re-opened");

   string foreign=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"M1BrowserTestArtifacts","foreign-"+Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(Path.Combine(foreign,"browser","chatgpt-default"));
   File.WriteAllText(Path.Combine(foreign,"browser","chatgpt-default","user-file.txt"),"do not touch");
   var unsafeManager=new M1BrowserProfileManager(foreign);
   Expect(delegate { unsafeManager.EnsureOwnedProfile(); },"not_owned","foreign browser profile refused");

   var discovered=manager.Discover();
   Check(discovered.All(delegate(M1BrowserCandidate x){return File.Exists(x.executable);}),"discovery returns existing executables only");
   if(discovered.Count>0) {
    var plan=manager.Plan("chrome","https://chatgpt.com/");
    Check(File.Exists(plan.executable),"launch plan browser exists");
    Check(plan.arguments.IndexOf("--user-data-dir=",StringComparison.Ordinal)>=0,"launch plan pins dedicated user-data-dir");
    Check(plan.arguments.IndexOf("--start-minimized",StringComparison.Ordinal)>=0,"launch plan requests minimized startup");
    Check(plan.arguments.IndexOf(manager.ProfileRoot,StringComparison.OrdinalIgnoreCase)>=0,"launch plan uses owned profile");
    var psi=manager.StartInfo(plan);
    Check(!psi.UseShellExecute && psi.CreateNoWindow && psi.WindowStyle==System.Diagnostics.ProcessWindowStyle.Minimized,"start info avoids console and requests minimized window");
   } else {
    Console.WriteLine("NOTE: no supported Chrome/Edge executable found; discovery contract still passed.");
   }

   Expect(delegate { manager.Plan("chrome","http://example.com/"); },"browser_url_invalid","non-ChatGPT URL refused");
   Console.WriteLine("M1 browser profile tests PASS: "+passed+" browsers="+discovered.Count);
   Console.WriteLine("profile_root="+manager.ProfileRoot);
   foreach(var b in discovered)Console.WriteLine("browser="+b.name+" path="+b.executable+" source="+b.source);
   return 0;
  } catch(Exception e) {
   Console.Error.WriteLine(e.ToString());
   return 1;
  }
 }
}
