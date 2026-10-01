namespace NetRatel.API.Security.Local;

internal sealed class LocalAuthenticationUnavailableException()
    : Exception("Local authentication is temporarily unavailable.");
