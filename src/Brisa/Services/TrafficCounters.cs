using System.Net.NetworkInformation;

namespace Brisa.Services;

/// <summary>Octets counted by the adapter so far, not a rate.</summary>
public readonly record struct TrafficReading(ulong Received, ulong Sent);

/// <summary>
/// Where the counters come from. The real implementation reads the tunnel adapter; isolated
/// sessions get a simulated one so screenshots never show invented numbers in a real build.
/// </summary>
public interface ITrafficSource
{
    /// <summary>Null when no tunnel adapter is up, or the counters cannot be read.</summary>
    TrafficReading? Read();
}

/// <summary>
/// Counters of the WireSock adapter, read from the IP statistics the stack already keeps.
/// Enumerating every adapter is the expensive part, and the result's OperationalStatus is a
/// snapshot taken at enumeration. So the adapter list is read at most once per
/// <see cref="RefreshInterval"/>: to find a tunnel while none is cached, and to confirm a cached
/// tunnel is still up. Between refreshes only the cached adapter's counters are read.
/// </summary>
public sealed class InterfaceTrafficSource : ITrafficSource
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly Func<IEnumerable<NetworkInterface>> _enumerate;
    private readonly Func<DateTime> _clock;
    private NetworkInterface? _adapter;
    private DateTime? _nextRefresh;

    public InterfaceTrafficSource() : this(NetworkInterface.GetAllNetworkInterfaces, () => DateTime.UtcNow) { }

    internal InterfaceTrafficSource(Func<IEnumerable<NetworkInterface>> enumerate, Func<DateTime> clock)
    {
        _enumerate = enumerate;
        _clock = clock;
    }

    public TrafficReading? Read()
    {
        var now = _clock();
        if (_nextRefresh is not { } due || now >= due)
        {
            _nextRefresh = now + RefreshInterval;
            // A fresh enumeration carries the live status. The cached object's status never changes.
            var cachedId = _adapter?.Id;
            _adapter = Find(cachedId);
        }
        if (_adapter is not { } adapter) return null;
        try
        {
            var stats = adapter.GetIPStatistics();
            return new TrafficReading((ulong)Math.Max(0, stats.BytesReceived), (ulong)Math.Max(0, stats.BytesSent));
        }
        catch (NetworkInformationException) { _adapter = null; return null; }
        catch (PlatformNotSupportedException) { _adapter = null; return null; }
    }

    private NetworkInterface? Find(string? preferredId)
    {
        try
        {
            var tunnels = _enumerate().Where(IsTunnel).ToList();
            return tunnels.FirstOrDefault(adapter => adapter.Id == preferredId) ?? tunnels.FirstOrDefault();
        }
        catch (NetworkInformationException) { return null; }
    }

    private static bool IsTunnel(NetworkInterface adapter) =>
        adapter.OperationalStatus == OperationalStatus.Up &&
        adapter.Description.Contains("wiresock", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Deterministic stand-in for isolated sessions: a slow walk with a periodic spike, so the chart
/// exercises every code path without pretending to measure anything.
/// </summary>
public sealed class SimulatedTrafficSource : ITrafficSource
{
    private readonly DateTime _start = DateTime.UtcNow;
    private ulong _received, _sent;
    private int _tick;

    public TrafficReading? Read()
    {
        _tick++;
        var phase = (_tick % 40) / 40.0;
        var down = 900_000 + (long)(700_000 * Math.Sin(phase * Math.PI * 2)) + (_tick % 7) * 41_000;
        // Upload de verdade no Discord fica entre um quinto e um terço do download; era isso que
        // faltava para a linha tracejada sair do chão do gráfico.
        var up = down * (0.22 + 0.1 * Math.Sin(phase * Math.PI * 4 + 1));
        _received += (ulong)Math.Max(12_000, down);
        _sent += (ulong)Math.Max(2_000, up);
        _ = _start;
        return new TrafficReading(_received, _sent);
    }
}
