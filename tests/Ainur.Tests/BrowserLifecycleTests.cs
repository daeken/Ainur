using Ainur.Core.Browser;
namespace Ainur.Tests;
public class BrowserLifecycleTests {
 [Fact] public async Task RuntimeDisposeShouldCloseOwnedBrowser() {
  using var home=new TempHome();var rt=home.Runtime(new FakeProvider((_,_)=>FakeProvider.Text("unused")),start:false);
  var manager=BrowserManager.For(rt);BrowserSession? browser=null;
  try {
   using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
   browser=await manager.AcquireAsync("zz-owned","zz-agent",new BrowserOptions {Width=400,Height=300},ct.Token);
   rt.Dispose();await Task.Delay(200);
   Assert.False(browser.IsRunning,$"Runtime.Dispose returned but owned Chromium pid {browser.ProcessId} still alive, profile={browser.ProfileDirectory}");
  } finally {await manager.DisposeAsync();}
 }
 [Fact] public async Task SameSessionConcurrentAcquireSingleflightsAndCloseCannotEvictReplacement() {
  using var home=new TempHome();using var rt=home.Runtime(new FakeProvider((_,_)=>FakeProvider.Text("unused")),start:false);
  var manager=BrowserManager.For(rt);
  using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(25));
  var acquires=Enumerable.Range(0,5).Select(_=>manager.AcquireAsync("singleflight","agent",new BrowserOptions {Width=400,Height=300},ct.Token)).ToArray();
  var results=await Task.WhenAll(acquires);
  Assert.All(results,x=>Assert.Same(results[0],x));
  Assert.Single(manager.Active());
  var closed=manager.CloseSessionAsync("singleflight",ct:ct.Token);
  await closed;
  Assert.False(results[0].IsRunning);
  Assert.Null(manager.Get("singleflight"));
  var next=await manager.AcquireAsync("singleflight","agent",new BrowserOptions {Width=400,Height=300},ct.Token);
  Assert.NotSame(results[0],next);
  Assert.Same(next,manager.Get("singleflight"));
  await manager.CloseSessionAsync("singleflight",ct:ct.Token);
  Assert.False(next.IsRunning);
  Assert.Empty(manager.Active());
 }
 [Fact] public async Task CancelledLaunchShouldLeaveNoProfileOrBrowser() {
  var root=Path.Combine(Path.GetTempPath(),"zz-cancel-browser-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(root);
  try {
   using var ct=new CancellationTokenSource();ct.Cancel();
   await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>BrowserSession.LaunchAsync("cancelled",new BrowserOptions {DataRoot=root},ct:ct.Token));
   await Task.Delay(300);
   Assert.False(Directory.Exists(Path.Combine(root,"cancelled","profile")),"Cancelled launch leaked browser profile: "+root);
  } finally {
   // Kill only processes launched by this unique scratch test profile, never user's or platform browsers.
   var ps=new System.Diagnostics.Process {StartInfo=new("/bin/ps","-axo pid=,command=") {RedirectStandardOutput=true,UseShellExecute=false}};ps.Start();var lines=await ps.StandardOutput.ReadToEndAsync();await ps.WaitForExitAsync();
   foreach(var line in lines.Split('\n').Where(x=>x.Contains("--user-data-dir="+root))){var pid=int.Parse(line.Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries)[0]);try{System.Diagnostics.Process.GetProcessById(pid).Kill(true);}catch{}}
   await Task.Delay(200);try{Directory.Delete(root,true);}catch{}
  }
 }
}
