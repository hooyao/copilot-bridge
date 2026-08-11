using System.Net;

namespace CopilotBridge.Cli.Auth;

internal sealed class GitHubApiRequestException : Exception
{
    public GitHubApiRequestException(string operation, HttpStatusCode statusCode)
        : base($"GitHub {operation} failed: {(int)statusCode} {statusCode}.")
    {
        Operation = operation;
        StatusCode = statusCode;
    }

    public string Operation { get; }
    public HttpStatusCode StatusCode { get; }
}
