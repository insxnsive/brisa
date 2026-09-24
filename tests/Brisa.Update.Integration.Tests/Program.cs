using System.Security.Cryptography;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

if (args.Length != 4) throw new ArgumentException("Expected source, current version, target version and isolated cache path");
var source = args[0];
var cache = Path.GetFullPath(args[3]);
Directory.CreateDirectory(cache);
var locator = new TestVelopackLocator("Brisa", args[1], cache);
IUpdateSource feed = source == "github"
    ? new GithubSource("https://github.com/insxnsive/brisa", accessToken: null, prerelease: true)
    : new SimpleFileSource(new DirectoryInfo(Path.GetFullPath(source)));
var options = new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false };
var manager = new UpdateManager(feed, options, locator);
var update = await manager.CheckForUpdatesAsync() ?? throw new Exception("Expected a newer release");
if (update.TargetFullRelease.Version.ToString() != args[2]) throw new Exception("Wrong release version");
await manager.DownloadUpdatesAsync(update);
var file = Path.Combine(cache, update.TargetFullRelease.FileName);
if (!File.Exists(file)) throw new Exception("No downloaded update package");
if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).Equals(update.TargetFullRelease.SHA256, StringComparison.OrdinalIgnoreCase))
    throw new Exception("Downloaded package hash mismatch");
var current = new UpdateManager(feed, options, new TestVelopackLocator("Brisa", args[2], cache));
if (await current.CheckForUpdatesAsync() is not null) throw new Exception("Current version must not reinstall itself");
var future = new UpdateManager(feed, options, new TestVelopackLocator("Brisa", "999.0.0", cache));
if (await future.CheckForUpdatesAsync() is not null) throw new Exception("Update source attempted a downgrade");
Console.WriteLine("PASS real Velopack feed selection, verified download, no reinstall and no downgrade");
Console.WriteLine(file);
