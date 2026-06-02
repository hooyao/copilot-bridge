using System.Runtime.Versioning;

// The Cli assembly is windows-only (DPAPI token storage). These tests exercise
// its types directly, so declare the whole test assembly windows-only too —
// silences CA1416 without per-class [SupportedOSPlatform] attributes.
[assembly: SupportedOSPlatform("windows")]
