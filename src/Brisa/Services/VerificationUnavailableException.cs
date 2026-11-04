namespace Brisa.Services;

/// <summary>
/// The verification window could not start (WebView2 runtime missing or broken, profile
/// creation failure, host failure). Distinct from the user closing the window, which is a
/// cancellation and must stay silent.
/// </summary>
public sealed class VerificationUnavailableException : Exception
{
    public VerificationUnavailableException(Exception? inner = null)
        : base("The verification window could not start.", inner) { }
}
