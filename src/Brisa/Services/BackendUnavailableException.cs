namespace Brisa.Services;

/// <summary>A terminal transport/startup failure, not an operation rejection.</summary>
public sealed class BackendUnavailableException : IOException
{
    public BackendUnavailableException() : base("The native service is unavailable. Close and reopen Brisa to retry.") { }
}
