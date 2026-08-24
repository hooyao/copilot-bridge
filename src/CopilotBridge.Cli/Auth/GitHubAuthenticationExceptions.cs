using System.Net;

namespace CopilotBridge.Cli.Auth;

internal class GitHubOAuthException : Exception
{
    public GitHubOAuthException(
        string operation,
        string? errorCode,
        HttpStatusCode? statusCode = null)
        : base(FormatMessage(operation, errorCode, statusCode))
    {
        Operation = operation;
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public string Operation { get; }
    public string? ErrorCode { get; }
    public HttpStatusCode? StatusCode { get; }

    private static string FormatMessage(
        string operation,
        string? errorCode,
        HttpStatusCode? statusCode)
    {
        var status = statusCode is null ? "" : $" ({(int)statusCode} {statusCode})";
        var code = string.IsNullOrWhiteSpace(errorCode) ? "unknown_error" : errorCode;
        return $"GitHub OAuth {operation} failed{status}: {code}.";
    }
}

internal sealed class GitHubRefreshCredentialRejectedException : GitHubOAuthException
{
    public GitHubRefreshCredentialRejectedException(
        string? errorCode,
        HttpStatusCode? statusCode = null)
        : base("refresh-token exchange", errorCode, statusCode)
    {
    }
}

internal sealed class GitHubReauthenticationRequiredException : InvalidOperationException
{
    public GitHubReauthenticationRequiredException(string reason, Exception? inner = null)
        : base(
            $"Stored GitHub credentials cannot be refreshed ({reason}). "
            + "Run `auth login` to replace them with the newest credential version.",
            inner)
    {
    }
}
