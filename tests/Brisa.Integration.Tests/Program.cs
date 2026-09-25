using Brisa.Services;
using System.Diagnostics;

// Real host transport with a clearly synthetic, local-only Node peer.
var root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "brisa-integration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "runtime"));
Directory.CreateDirectory(Path.Combine(root, "backend"));
var node = Environment.GetEnvironmentVariable("NATIVE_TEST_NODE") ?? throw new InvalidOperationException("Set NATIVE_TEST_NODE to the installed node.exe");
File.Copy(node, Path.Combine(root, "runtime", "node.exe"));
File.WriteAllText(Path.Combine(root, "backend", "backend.cjs"), """
const readline = require('node:readline');
readline.createInterface({input:process.stdin}).on('line', line => {
 const q=JSON.parse(line);
 if(q.command==='environment'){ console.log(JSON.stringify({id:q.id,ok:true,result:{success:!Object.hasOwn(process.env,'ELECTRON_RUN_AS_NODE')}})); return; }
 if(q.command==='spawnClient'){
   const child = require('node:child_process').spawn(process.execPath, ['-e', 'setTimeout(()=>{},30000)'], {stdio:'ignore',detached:true,windowsHide:true});
   child.unref();
   return process.stdout.write(JSON.stringify({id:q.id,ok:true,result:{success:true,message:String(child.pid)}})+'\n');
 }
 if (q.command==='snapshot') return process.stdout.write(JSON.stringify({id:q.id,ok:true,result:{connected:false,externalTunnel:false,reliable:true,signedIn:false,username:'',route:null,mode:'proton'}})+'\n');
 const containsNull=Object.values(q.payload).some(v=>v===null);
 const valid=q.command==='login' && q.payload.username==='fixture-user' && q.payload.password==='synthetic-password' && !containsNull;
 process.stdout.write(JSON.stringify({id:q.id,ok:true,result:{success:valid,message:valid?'Fixture accepted':'Optional fields must be omitted, not null'}})+'\n');
});
""");
try
{
    await using var client = new BackendClient(root, Path.Combine(root, "data"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    var snapshot = await client.SnapshotAsync(timeout.Token);
    if (!snapshot.Reliable || snapshot.SignedIn || snapshot.Connected) throw new Exception("Unexpected fixture snapshot");
    Console.WriteLine("PASS actual C# host -> Node snapshot request/response");
    var env = await client.CommandAsync("environment", new { });
    if (!env.Success) throw new Exception("Electron run-as-Node flag leaked into the production backend environment.");
    Console.WriteLine("PASS Electron launch-mode environment flag is absent");
    var login = await client.CommandAsync("login", new {
        username = "fixture-user", password = "synthetic-password", twoFactorCode = (string?)null,
        humanVerificationToken = (string?)null, humanVerificationMethod = (string?)null
    }, timeout.Token);
    if (!login.Success) throw new Exception(login.Message);
    Console.WriteLine("PASS login request preserves values and omits optional null fields");
    var spawned = await client.CommandAsync("spawnClient", new { }, timeout.Token);
    using var independentClient = Process.GetProcessById(int.Parse(spawned.Message!));
    try
    {
        await Task.Delay(500);
        if (independentClient.HasExited) throw new Exception("Synthetic client exited before disposal, exit=" + independentClient.ExitCode);
        await client.DisposeAsync();
        await Task.Delay(300);
        if (independentClient.HasExited) throw new Exception("Backend disposal killed the restored client process");
        Console.WriteLine("PASS backend shutdown preserves the independently running restored client");
    }
    finally { if (!independentClient.HasExited) { independentClient.Kill(); await independentClient.WaitForExitAsync(); } }
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}
