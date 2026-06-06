// Intentionally minimal. The Cli assembly is no longer windows-only — its token
// store dispatches on the OS at runtime (DPAPI on Windows; AES-256-CBC+HMAC
// machine-derived key on Linux/macOS). So there is NO assembly-wide
// [SupportedOSPlatform("windows")] here either: keeping CA1416 active lets the
// cross-platform build catch any newly-introduced Windows-only API. The single
// Windows-attributed type under test (WindowsDpapiTokenProtector) is only ever
// constructed under an OperatingSystem.IsWindows() guard, so it needs no
// suppression; the cross-platform DerivedKeyTokenProtector is testable here on
// Windows via an injected IMachineKeyProvider stub.
