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

/// <summary>Counters of the WireSock adapter, read from the IP statistics the stack already keeps.</summary>
public sealed class InterfaceTrafficSource : ITrafficSource
{
    private NetworkInterface? _adapter = null;

    public TrafficReading? Read()
    {
        var adapter = _adapter is { } cached && IsTunnel(cached) ? cached : Find();
        _adapter = adapter;
        if (adapter is null) return null;
        try
        {
            var stats = adapter.GetIPStatistics();
            return new TrafficReading((ulong)Math.Max(0, stats.BytesReceived), (ulong)Math.Max(0, stats.BytesSent));
        }
        catch (NetworkInformationException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    private static NetworkInterface? Find()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(IsTunnel); }
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
