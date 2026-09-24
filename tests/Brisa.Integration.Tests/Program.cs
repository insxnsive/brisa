using Brisa.Services;

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
    var login = await client.CommandAsync("login", new {
        username = "fixture-user", password = "synthetic-password", twoFactorCode = (string?)null,
        humanVerificationToken = (string?)null, humanVerificationMethod = (string?)null
    }, timeout.Token);
    if (!login.Success) throw new Exception(login.Message);
    Console.WriteLine("PASS login request preserves values and omits optional null fields");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}
