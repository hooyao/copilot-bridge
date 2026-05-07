using System.Runtime.Versioning;
using Xunit;

[assembly: SupportedOSPlatform("windows")]

// Tests hit live Copilot — Anthropic's per-account rate limit makes parallel test runs flaky.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
