using CopilotBridge.Cli.Auth;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class GitHubTokenSourceTests
{
    [Fact]
    public void EnvironmentToken_TakesPrecedenceOverPersistedCredential()
    {
        var resolved = GitHubTokenSource.Resolve("actions-token", "persisted-token");

        Assert.Equal("actions-token", resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingEnvironmentToken_FallsBackToPersistedCredential(string? environmentToken)
    {
        var resolved = GitHubTokenSource.Resolve(environmentToken, "persisted-token");

        Assert.Equal("persisted-token", resolved);
    }

    [Fact]
    public void NoCredential_ReturnsNull()
    {
        Assert.Null(GitHubTokenSource.Resolve(null, null));
    }
}
