using System.CommandLine;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Hosting;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class ProviderTokenCommandTests
{
    [Fact]
    public async Task Hidden_provider_token_command_prints_only_the_public_sentinel()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exit = await RootCli.Build()
                .Parse(["auth", "provider-token"])
                .InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });

            Assert.Equal(0, exit);
            Assert.Equal(AuthCommand.ProviderSentinel + Environment.NewLine, stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void Provider_token_command_is_hidden_from_auth_help()
    {
        var auth = RootCli.Build().Subcommands.Single(command => command.Name == "auth");
        var providerToken = auth.Subcommands.Single(command => command.Name == "provider-token");

        Assert.True(providerToken.Hidden);
    }
}
