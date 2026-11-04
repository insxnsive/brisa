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
 if(q.command==='closeOutput'){ process.exit(17); }
 if(q.command==='environment'){ console.log(JSON.stringify({id:q.id,ok:true,result:{success:!Object.hasOwn(process.env,'ELECTRON_RUN_AS_NODE')}})); return; }
 if(q.command==='spawnClient'){
   const child = require('node:child_process').spawn(process.execPath, ['-e', 'setTimeout(()=>{},30000)'], {stdio:'ignore',detached:true,windowsHide:true});
   child.unref();
   return process.stdout.write(JSON.stringify({id:q.id,ok:true,result:{success:true,message:String(child.pid)}})+'\n');
 }
 if (q.command==='snapshot') return process.stdout.write(JSON.stringify({id:q.id,ok:true,result:{connected:false,externalTunnel:false,reliable:true,signedIn:false,username:'',route:null,mode:'proton'}})+'\n');
 if(q.command==='noResponse'){ return; }
 if(q.command==='malformedWithId'){ process.stdout.write(JSON.stringify({id:q.id,ok:true})+'\n'); return; }
 if(q.command==='idlessError'){ process.stdout.write(JSON.stringify({ok:false,error:'synthetic-hostile-text'})+'\n'); return; }
 if(q.command==='invalidJson'){ process.stdout.write('not a frame at all synthetic-hostile-text\n'); return; }
 if(q.command==='oversized'){ process.stdout.write('x'.repeat(1048577)+'\n'); return; }
 if(q.command==='lateReply'){ setTimeout(()=>process.stdout.write(JSON.stringify({id:q.id,ok:true,result:{success:true}})+'\n'), 600); return; }
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

    VerifyConcurrentDisposal(root);
    await VerifyBoundedRequests(root);
    await using var closedOutput = new BackendClient(root, Path.Combine(root, "closed-output"));
    await ExpectTerminalFailure(closedOutput.CommandAsync("closeOutput", new { }));
    await ExpectTerminalFailure(closedOutput.SnapshotAsync());
    Console.WriteLine("PASS a crashed peer is terminal for current and later requests");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

static void VerifyConcurrentDisposal(string root)
{
    var previous = SynchronizationContext.Current;
    using var context = new QueuedContext();
    BackendClient client;
    try { SynchronizationContext.SetSynchronizationContext(context); client = new BackendClient(root, Path.Combine(root, "joined-disposal")); }
    finally { SynchronizationContext.SetSynchronizationContext(previous); }
    var first = client.DisposeAsync().AsTask();
    var second = client.DisposeAsync().AsTask();
    try
    {
        if (!ReferenceEquals(first, second)) throw new Exception("Concurrent backend disposal must join one cleanup task");
    }
    finally { context.DrainUntil(Task.WhenAll(first, second)); }
    Console.WriteLine("PASS concurrent backend disposal joins all owned readers");
}

static async Task VerifyBoundedRequests(string root)
{
    // A request with no caller deadline must still complete: bounded by the fallback, cleaned
    // up, and the late reply for its expired id is ignored rather than trusted.
    await using (var client = new BackendClient(root, Path.Combine(root, "no-response"), TimeSpan.FromMilliseconds(300)))
    {
        await ExpectTimeout(client.CommandAsync("lateReply", new { }));
        await Task.Delay(700);
        var ok = await client.CommandAsync("environment", new { }, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        if (!ok.Success) throw new Exception("A timed-out request must not poison the transport");
        Console.WriteLine("PASS a default request deadline ends a hung request and the transport survives");
    }

    // A caller that brought its own token owns the budget; the fallback must not shorten it.
    await using (var client = new BackendClient(root, Path.Combine(root, "caller-budget"), TimeSpan.FromMilliseconds(150)))
    {
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var slow = await client.CommandAsync("lateReply", new { }, caller.Token);
        if (!slow.Success) throw new Exception("A caller-owned budget must not be shortened by the default deadline");
        Console.WriteLine("PASS a caller-owned budget outlives the default request deadline");
    }

    // A malformed frame that names exactly one pending request faults that request only.
    await using (var client = new BackendClient(root, Path.Combine(root, "correlated"), TimeSpan.FromMilliseconds(400)))
    {
        await ExpectInvalidData(client.CommandAsync("malformedWithId", new { }));
        var ok = await client.CommandAsync("environment", new { }, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        if (!ok.Success) throw new Exception("A correlated bad frame must not poison unrelated requests");
        Console.WriteLine("PASS a malformed frame faults only the request it names");
    }

    // An uncorrelated protocol failure poisons the transport instead of leaving callers stuck.
    foreach (var command in new[] { "idlessError", "invalidJson", "oversized" })
    {
        await using var client = new BackendClient(root, Path.Combine(root, command), TimeSpan.FromSeconds(5));
        await ExpectTerminalFailure(client.CommandAsync(command, new { }));
        await ExpectTerminalFailure(client.CommandAsync("environment", new { }));
        Console.WriteLine($"PASS {command} fails current and later requests without echoing the frame");
    }
}

static async Task ExpectTimeout(Task request)
{
    try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (TimeoutException ex) when (ex.Message == BackendClient.TimeoutMessage) { return; }
    throw new Exception("A request without a caller deadline must fail with the fixed timeout message");
}

static async Task ExpectInvalidData(Task request)
{
    try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (InvalidDataException) { return; }
    throw new Exception("A correlated malformed frame must fault its own request");
}

static async Task ExpectTerminalFailure(Task request)
{
    try { await request.WaitAsync(TimeSpan.FromSeconds(2)); }
    catch (BackendUnavailableException) { return; }
    throw new Exception("A closed transport must fail with a terminal backend error, not succeed or hang");
}

sealed class QueuedContext : SynchronizationContext, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
    public void DrainUntil(Task completion)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!completion.IsCompleted)
        {
            if (!_queue.TryTake(out var item, 50, bound.Token)) continue;
            item.Callback(item.State);
        }
        completion.GetAwaiter().GetResult();
    }
    public void Dispose() => _queue.Dispose();
}
