# copilot-bridge — design doc

> Status: draft v0.2 · Last updated: 2026-05-06
>
> **2026-05-06 major revision (v0.2)**: v0.1 assumed Copilot only exposes the OpenAI shape and that we'd need to write a full Anthropic↔OpenAI translation layer. Research confirmed Copilot has a native Anthropic endpoint `POST /v1/messages` — sourced from Microsoft's official `@vscode/copilot-api` npm package; see [`copilot-api-research.md`](copilot-api-research.md). M1 is now **native Anthropic passthrough + preprocessing** — no translation layer. §1.3, §9, and §11 below are updated; §3.x protocol details now live in the research doc and aren't duplicated here.

## 1. Background and goals

### 1.1 What we're building

A .NET 10 Native AOT reverse proxy that exposes the GitHub Copilot LLM API in two compatibility shapes:

- **Anthropic Messages API** (`POST /v1/messages`) — so Claude Code can use Copilot as an Anthropic backend
- **OpenAI Chat Completions API** (`POST /v1/chat/completions`) — so any OpenAI-compatible client can use Copilot

Plus a tiny HTML config page for first-time GitHub login (device-code flow) and runtime settings (port, account type, default model).

### 1.2 Why AOT

The single hard goal is a **single-file, small-footprint `.exe`** with no .NET runtime dependency. AOT isn't chosen for cold-start speed — it's chosen for deploy simplicity and binary size. Every design decision (no reflection-based JSON, no `IHttpClientFactory`, no `wwwroot/` static files) follows from that.

### 1.3 Milestone 1 success criterion

One end-to-end flow working = M1 done:

- Claude Code with `ANTHROPIC_BASE_URL=http://localhost:<port>` runs a multi-turn conversation including tool calls. Our server **forwards the request as-is** to `https://api.githubcopilot.com/v1/messages` (with only the necessary preprocessing + auth header swap) and **forwards Copilot's SSE stream as-is** back to Claude Code.

### 1.4 Non-goals (not in M1)

- **OpenAI Chat Completions endpoint (`/v1/chat/completions`)** — supporting non-Claude models is M3 territory. M1 only serves the Claude Code → Anthropic path.
- **Anthropic↔OpenAI translation** — only needed in M3 as a fallback for models whose `supported_endpoints` doesn't include `/v1/messages`
- Rate limiting / manual approval (the `--rate-limit`, `--manual` advanced features in copilot-api)
- Embeddings (`POST /v1/embeddings`)
- Usage dashboard (`GET /usage`)
- Multi-user / multi-account
- HTTPS / auth on the bridge itself (we listen on localhost; OS-level isolation is enough)
- Cross-platform (M1 is win-x64 only; Linux/macOS later)

---

## 2. Reference implementations

Protocol facts and the full reference index are maintained in [`docs/copilot-api-research.md`](copilot-api-research.md) §1 — not duplicated here.

Quick priority order (read in this order when implementing M1):

1. **`references/vscode-copilot-api-pkg/package/dist/index.js`** ← Microsoft's official npm package. URL paths and the auto-injected header set live here. Authoritative.
2. **`references/vscode-copilot-chat-snippets/`** ← Excerpts of the VS Code Copilot Chat extension itself: Anthropic Messages API request body construction, stream parsing, `anthropic-beta` auto-generation.
3. **`references/copilot-api-anthropic/`** (caozhiyuan fork) ← Reverse-engineered but fully working implementation. Read for the preprocessing pipeline and Claude Code-specific edge cases.
4. **`references/copilot-api/`** (ericc-ch original) ← Read only the GitHub OAuth device-code flow part (the rest is outdated for our purposes).

---

## 3. Architecture overview

### 3.1 Big picture

```
┌────────────────────────────────────────────────────────────────────┐
│                      Claude Code / OpenAI client                    │
└────────────────────────────────────────────────────────────────────┘
   POST /v1/messages          POST /v1/chat/completions
   (Anthropic shape)          (OpenAI shape)
        │                          │
        ▼                          ▼
┌────────────────────────────────────────────────────────────────────┐
│  Hosting (Kestrel + minimal API)                                    │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │ Endpoints                                                     │ │
│  │  · AnthropicMessagesEndpoint   ← Translation in/out           │ │
│  │  · OpenAiChatCompletionsEndpoint (passthrough)                │ │
│  │  · ModelsEndpoint, CountTokensEndpoint                        │ │
│  │  · AuthEndpoints (/api/auth/*)                                │ │
│  │  · ConfigEndpoints (/api/config), ConfigPageEndpoint (/)      │ │
│  └───────────────────────────────────────────────────────────────┘ │
│                │                                                    │
│                ▼                                                    │
│  ┌──────────────────────────┐    ┌─────────────────────────────┐   │
│  │ Translation              │    │ AuthService (self-contained) │   │
│  │  · AnthropicToOpenAi     │    │  GetCopilotTokenAsync()       │   │
│  │  · OpenAiToAnthropic     │    │  GetStatus()                  │   │
│  │  · StreamEventTranslator │    │  BeginDeviceFlowAsync()       │   │
│  └──────────────────────────┘    │  SignOutAsync()                │   │
│                │                  └─────────────────────────────┘   │
│                ▼                                                    │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ CopilotClient (HTTP infrastructure)                         │    │
│  │  · ChatCompletionsAsync / ChatCompletionsStreamAsync        │    │
│  │  · GetModelsAsync                                            │    │
│  └────────────────────────────────────────────────────────────┘    │
└────────────────────────────────────────────────────────────────────┘
                │                                       │
                ▼                                       ▼
   api.githubcopilot.com                  github.com / api.github.com
   (api.business.* / enterprise.*)        (device flow + Copilot token issuance)
```

### 3.2 Layering

Outer depends on inner; never the reverse:

```
Cli (Program.cs)                                ← entry point, arg parsing, DI bootstrap
   ↓
Hosting (KestrelHost, EndpointMap)              ← HTTP server, routing
   ↓
Endpoints                                       ← route handlers (no business logic)
   ↓
Application services (Translation, Auth)         ← orchestration, protocol translation, token lifecycle
   ↓
Infrastructure (CopilotClient, file stores)     ← HttpClient, file I/O
   ↓
Models                                          ← DTOs + JsonSerializerContext
```

**Single executable project** — all layers above live under `src/CopilotBridge.Cli/`, separated by folders. When size or compile time forces a split, carve out `Core` along the seam described in [§ 8](#8-file-and-folder-structure).

---

## 4. Key design principles

### 4.1 AuthService is self-contained

`AuthService` is **the only public auth surface**. Internally it owns:

- GitHub OAuth token acquisition (device-code flow)
- GitHub OAuth token persistence (file + ACL/DPAPI)
- Copilot token exchange (calling `/copilot_internal/v2/token` with the GitHub token)
- In-memory cache of the Copilot token
- Background refresh timer (fires at `refresh_in - 60s`)

Callers (`CopilotClient`, endpoints) **only call `GetCopilotTokenAsync()`**. They don't know — and shouldn't care — whether the token came from disk, just got freshly issued, or is about to refresh. None of that internal state leaks out.

```csharp
// src/CopilotBridge.Cli/Auth/IAuthService.cs
namespace CopilotBridge.Cli.Auth;

public interface IAuthService
{
    /// <summary>
    /// Returns a valid Copilot bearer token. Triggers the device-code flow if no GitHub
    /// token is present (blocks until user authorizes). Refreshes the Copilot token
    /// transparently — caller never sees an expired one.
    /// </summary>
    ValueTask<string> GetCopilotTokenAsync(CancellationToken ct = default);

    /// <summary>For the HTML config page — show login state.</summary>
    AuthStatus GetStatus();

    /// <summary>Explicitly start the device-code flow (HTML "Login" button).</summary>
    ValueTask<DeviceCodeChallenge> BeginDeviceFlowAsync(CancellationToken ct = default);

    /// <summary>Sign out: clears persisted GitHub token and all in-memory tokens.</summary>
    ValueTask SignOutAsync(CancellationToken ct = default);
}

public sealed record AuthStatus(bool IsAuthenticated, string? GitHubLogin, DateTimeOffset? CopilotTokenExpiry);
public sealed record DeviceCodeChallenge(string UserCode, string VerificationUri, TimeSpan ExpiresIn);
```

The implementation is split across files (for testability) but everything except `AuthService` itself is `internal`:

```
Auth/
  IAuthService.cs              // public interface
  AuthService.cs               // public implementation + state machine
  internal:
    GitHubAuthClient.cs        // device-code POST + access_token poll
    CopilotTokenClient.cs      // GET /copilot_internal/v2/token
    TokenStore.cs              // file persistence + ACL/DPAPI
    AuthStateMachine.cs        // internal states: NotLoggedIn / DeviceFlowInProgress / Authenticated / Refreshing
```

### 4.2 Translation is pure functions

`AnthropicToOpenAi.TranslateRequest(payload)` and `OpenAiToAnthropic.TranslateResponse(response)` are side-effect-free, depend only on DTOs, and don't touch HttpClient, State, or Configuration. Benefits:

- Trivial unit tests (feed input, assert output)
- AOT-friendly (no reflection, no DI baked in)
- Easy to diff 1:1 against the TypeScript reference

Streaming translation needs state (content block index, tool_call id-to-block mapping, JSON fragment buffer), but **only within the lifetime of one request** — `StreamEventTranslator` is a sealed class, instantiated per request, disposed when the request ends.

### 4.3 CopilotClient knows nothing about auth

```csharp
// src/CopilotBridge.Cli/Copilot/ICopilotClient.cs
public interface ICopilotClient
{
    ValueTask<OpenAiChatCompletionResponse> ChatCompletionsAsync(
        OpenAiChatCompletionsPayload payload, CancellationToken ct = default);

    IAsyncEnumerable<OpenAiStreamChunk> ChatCompletionsStreamAsync(
        OpenAiChatCompletionsPayload payload, CancellationToken ct = default);

    ValueTask<ModelsResponse> GetModelsAsync(CancellationToken ct = default);
}
```

The implementation calls `IAuthService` for a token on every request (it always gets a currently-valid one — refresh has already swapped expired tokens out). `HttpClient` is a singleton, not bound to any token; the token only lives in the `Authorization` request header.

### 4.4 Endpoints orchestrate; don't write business logic

Each endpoint handler is one file and only does:

1. Deserialize the request (via `JsonContext`)
2. Call application services (Translation, AuthService, CopilotClient)
3. Serialize the response or write SSE
4. Map typed exceptions to HTTP errors (Anthropic-shape or OpenAI-shape, depending on which endpoint)

No `if/else` business branching in endpoints — push it down into services.

### 4.5 Single JsonSerializerContext

```csharp
// src/CopilotBridge.Cli/Models/JsonContext.cs
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AnthropicMessagesPayload))]
[JsonSerializable(typeof(AnthropicResponse))]
[JsonSerializable(typeof(AnthropicStreamEvent))]
[JsonSerializable(typeof(OpenAiChatCompletionsPayload))]
[JsonSerializable(typeof(OpenAiChatCompletionResponse))]
[JsonSerializable(typeof(OpenAiStreamChunk))]
[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(AccessTokenResponse))]
[JsonSerializable(typeof(CopilotTokenResponse))]
[JsonSerializable(typeof(ModelsResponse))]
[JsonSerializable(typeof(AppConfig))]
internal partial class JsonContext : JsonSerializerContext;
```

Serialization always goes through `JsonContext.Default.<TypeName>`:

```csharp
// correct
var json = JsonSerializer.Serialize(payload, JsonContext.Default.OpenAiChatCompletionsPayload);

// WRONG — silently returns "{}" under AOT
var json = JsonSerializer.Serialize(payload);
```

Build-time, enable `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>` so the compiler flags unsafe calls as warnings; CI treats warnings as errors.

---

## 5. Critical protocol details

`references/copilot-api/src/lib/api-config.ts` is the source of truth for protocol constants. The points below are easy to get wrong.

### 5.1 VS Code-shaped headers

Requests to Copilot endpoints must include (order and values matching the reference):

```
Authorization: Bearer <copilot-token>
content-type: application/json
copilot-integration-id: vscode-chat
editor-version: vscode/<vscode-version>
editor-plugin-version: copilot-chat/0.26.7
user-agent: GitHubCopilotChat/0.26.7
openai-intent: conversation-panel
x-github-api-version: 2025-04-01
x-request-id: <uuid v4>
x-vscode-user-agent-library-version: electron-fetch
copilot-vision-request: true   # only when the request body contains image content
X-Initiator: agent | user      # see §5.2
```

`<vscode-version>` starts hardcoded (e.g. `1.95.0`); later we can implement `cacheVSCodeVersion()` (see `references/copilot-api/src/services/get-vscode-version.ts`).

### 5.2 `X-Initiator` semantics

- If **any inbound message** has role `assistant` or `tool` → `agent`
- Otherwise → `user`

Affects Copilot's quota accounting; must be set correctly.

### 5.3 Account type → URL

| accountType | base URL |
| --- | --- |
| `individual` (default) | `https://api.githubcopilot.com` |
| `business` | `https://api.business.githubcopilot.com` |
| `enterprise` | `https://api.enterprise.githubcopilot.com` |

Driven by `AppConfig.AccountType`, modifiable at runtime via the HTML config page (requires server restart because the HttpClient base URL is cached).

### 5.4 Model name normalization

Claude Code sends models like `claude-sonnet-4-20250514`, `claude-opus-4-20250101`.
Copilot accepts: `claude-sonnet-4`, `claude-opus-4` (no version suffix).

```csharp
// Translation/ModelNameNormalizer.cs
public static string NormalizeForCopilot(string anthropicModel) => anthropicModel switch
{
    var m when m.StartsWith("claude-sonnet-4-", StringComparison.Ordinal) => "claude-sonnet-4",
    var m when m.StartsWith("claude-opus-4-", StringComparison.Ordinal) => "claude-opus-4",
    _ => anthropicModel,
};
```

### 5.5 Tool result ordering

In the Anthropic protocol, a user message can contain `tool_result` blocks. When translating to OpenAI messages, **`tool_result` must come before any other user content** to preserve the OpenAI `tool_use → tool_result → user` ordering:

```
Anthropic single user message:
  content: [tool_result, tool_result, text]

translated to OpenAI multiple messages:
  { role: "tool", tool_call_id: ..., content: ... }
  { role: "tool", tool_call_id: ..., content: ... }
  { role: "user", content: "..." }
```

### 5.6 Streaming translation state machine

Copilot's streaming response is OpenAI-style SSE chunks. `/v1/messages` clients expect Anthropic-style events. State within one request:

```csharp
// Translation/StreamEventTranslator.cs
internal sealed class StreamEventTranslator
{
    private bool _messageStartSent;
    private int _contentBlockIndex;
    private bool _contentBlockOpen;
    // OpenAI tool_call.index → our assigned Anthropic content block index
    private readonly Dictionary<int, ToolCallTracker> _toolCalls = new();

    private record ToolCallTracker(string Id, string Name, int AnthropicBlockIndex);

    public IEnumerable<AnthropicStreamEvent> ProcessChunk(OpenAiStreamChunk chunk) { /* ... */ }
}
```

Event sequence (one `message_start` per request; `start`/`stop` paired per content block):

```
message_start                   ← first chunk
  content_block_start (text)
    content_block_delta (text_delta) × N
  content_block_stop
  content_block_start (tool_use, id+name from OpenAI tool_call.index)
    content_block_delta (input_json_delta) × N   ← OpenAI's partial JSON
  content_block_stop
  ...
message_delta (stop_reason, usage)
message_stop
```

Tool-call argument JSON arrives in fragments (`{"location":` → `"San Fr` → `ancisco"}`). Each fragment goes out as an `input_json_delta.partial_json` — **don't try to validate or reassemble**; the client accumulates on its end.

### 5.7 Stop reason mapping

```
OpenAI finish_reason  →  Anthropic stop_reason
  stop                  →  end_turn
  length                →  max_tokens
  tool_calls            →  tool_use
  content_filter        →  refusal
  null (mid-stream)     →  null
```

---

## 6. Authentication flow

### 6.1 State machine

```
┌──────────────┐
│ NotLoggedIn  │  (on startup, if token file missing or empty)
└──────┬───────┘
       │ BeginDeviceFlowAsync() or GetCopilotTokenAsync()
       ▼
┌──────────────────────┐
│ DeviceFlowInProgress │  ← user enters user_code in browser
└──────┬───────────────┘
       │ poll succeeds → access_token returned
       ▼
┌──────────────────────────┐
│ HasGitHubTokenNeedsCopilot│  ← persist github_token to disk
└──────┬───────────────────┘
       │ GET /copilot_internal/v2/token
       ▼
┌────────────────┐  ←─────────────┐
│ Authenticated  │   refresh timer fires,
└──────┬─────────┘   re-issues GET /copilot_internal/v2/token
       │ refresh fails (401)
       ▼
┌──────────────────────────┐
│ TokenInvalidNeedsReauth  │  → fall back to NotLoggedIn, notify user
└──────────────────────────┘
```

### 6.2 Cold-start sequence (first login)

```
User              Cli           AuthService          GitHub OAuth          api.github.com
 │                 │                 │                    │                    │
 │  copilot-bridge               │                    │                    │
 │────────────────►│                 │                    │                    │
 │                 │ EnsureLoggedIn  │                    │                    │
 │                 │────────────────►│                    │                    │
 │                 │                 │ POST /login/device/code                 │
 │                 │                 │───────────────────►│                    │
 │                 │                 │  user_code, verif_uri                   │
 │                 │                 │◄───────────────────│                    │
 │  print user_code + URL            │                    │                    │
 │◄────────────────│◄────────────────│                    │                    │
 │   open browser, type code, authorize on github.com ─────────────►          │
 │                 │                 │ POST /login/oauth/access_token (poll)   │
 │                 │                 │───────────────────►│                    │
 │                 │                 │  access_token (after user authorizes)   │
 │                 │                 │◄───────────────────│                    │
 │                 │                 │ persist to disk    │                    │
 │                 │                 │ GET /copilot_internal/v2/token          │
 │                 │                 │──────────────────────────────────────► │
 │                 │                 │  copilot_token, expires_at, refresh_in  │
 │                 │                 │◄────────────────────────────────────── │
 │                 │                 │ start refresh Timer(refresh_in - 60s)   │
 │                 │   ✓ ready       │                    │                    │
 │                 │◄────────────────│                    │                    │
 │  start Kestrel listening                              │                    │
```

### 6.3 Persistence paths

| File | Path |
| --- | --- |
| GitHub OAuth token | `<exe-directory>\github_token.dat` (DPAPI-encrypted) |
| App config | `<exe-directory>\config.json` (plaintext, no secrets) |

`<exe-directory>` = `AppContext.BaseDirectory` — the publish directory or `bin\Debug\net10.0\`. Putting the token next to the .exe lets you copy a deploy as one folder (the encrypted token won't decrypt on another machine, which is the expected "move machine = re-login" behavior).

#### DPAPI encryption (no key management)

The GitHub token goes through Windows DPAPI via `System.Security.Cryptography.ProtectedData`:

```csharp
var encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
File.WriteAllBytes(path, encrypted);
// ...
var encrypted = File.ReadAllBytes(path);
var plain = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
```

- **`CurrentUser` scope**: Windows derives the key from the current user's Windows credentials. Other users, other machines: cannot decrypt.
- **`entropy`**: a hardcoded byte string (not a key — it's a salt) that prevents another app on the same machine from decrypting our blob if it stole the file.
- **No key for us to persist or rotate** — Windows handles it.
- **AOT-friendly**: ProtectedData internally P/Invokes `Crypt32.dll`; no reflection.
- **Threat model**: code running as the same Windows user can decrypt. This is the inherent DPAPI tradeoff and a reasonable boundary for a single-user desktop tool (Chromium uses the same approach for cookie storage).

Why not Windows Credential Manager: the user wants the token file **next to the .exe** for portable deployment; Credential Manager stores in the system credential vault, which doesn't satisfy that.

### 6.4 GitHub OAuth constants

Don't change these:

```
client_id: Iv1.b507a08c87ecfe98       (GitHub's official Copilot OAuth app)
scope:     read:user
device-code endpoint: https://github.com/login/device/code
poll endpoint:        https://github.com/login/oauth/access_token
copilot-token endpoint: https://api.github.com/copilot_internal/v2/token
```

---

## 7. AOT and size discipline

### 7.1 Required

- `<PublishAot>true</PublishAot>` + `<OptimizationPreference>Size</OptimizationPreference>`
- `<InvariantGlobalization>true</InvariantGlobalization>`, `<UseSystemResourceKeys>true</UseSystemResourceKeys>`, `<IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>`
- Release build: disable `DebuggerSupport`, `StackTraceSupport`, `MetricsSupport`, `EventSourceSupport`, `HttpActivityPropagationSupport`
- All JSON serialization through `JsonSerializerContext` (see [§4.5](#45-single-jsonserializercontext))

### 7.2 Forbidden

- `JsonSerializer.Serialize(obj)` without a `JsonTypeInfo` overload
- `Activator.CreateInstance(Type)` / `Type.GetType(string)` / any dynamic loading
- `[FromBody] dynamic` or `object` parameters on minimal API delegates
- `IHttpClientFactory` (its default implementation goes through DI factories — AOT-friendly but adds size; an `HttpClient` singleton is enough for M1)
- `wwwroot/` static files (use `<EmbeddedResource>` for HTML/JS/CSS)

### 7.3 Size monitoring

After every dependency change:

```pwsh
dotnet publish src/CopilotBridge.Cli -c Release -r win-x64 -o publish
Get-Item publish/copilot-bridge.exe | Select-Object Length
```

Record in `docs/size-history.md` (started at M1). Budget: M1 finishes with .exe < 25 MB; reassess before M2.

### 7.4 Dependency allowlist (M1 only)

Allowed:

- `System.Net.Http` (BCL)
- `System.Text.Json` (BCL, source generator)
- `Microsoft.AspNetCore.App` (shared framework — AOT path uses minimal API + Kestrel)

Anything outside the list **must** be evaluated and the size impact noted in the PR description.

---

## 8. File and folder structure

```
copilot-bridge/
├── CLAUDE.md
├── CopilotBridge.slnx
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── .gitignore
├── docs/
│   ├── design.md                           ← this file
│   └── size-history.md                     ← M1 onward
├── references/
│   └── copilot-api/                        ← read-only references
└── src/
    └── CopilotBridge.Cli/
        ├── CopilotBridge.Cli.csproj
        ├── Program.cs                      ← entry, arg parsing, host bootstrap
        │
        ├── Hosting/
        │   ├── KestrelHost.cs              ← builds and runs the HTTP server
        │   └── EndpointMap.cs              ← route table (single source of truth for all endpoints)
        │
        ├── Endpoints/
        │   ├── AnthropicMessagesEndpoint.cs
        │   ├── OpenAiChatCompletionsEndpoint.cs
        │   ├── ModelsEndpoint.cs
        │   ├── CountTokensEndpoint.cs
        │   ├── AuthEndpoints.cs            ← /api/auth/start, /api/auth/status
        │   ├── ConfigEndpoints.cs          ← GET/POST /api/config
        │   └── ConfigPageEndpoint.cs       ← GET / returns the embedded HTML
        │
        ├── Translation/
        │   ├── AnthropicToOpenAi.cs        ← request translation (pure functions)
        │   ├── OpenAiToAnthropic.cs        ← non-streaming response translation
        │   ├── StreamEventTranslator.cs    ← streaming state machine
        │   ├── ModelNameNormalizer.cs
        │   └── StopReasonMap.cs
        │
        ├── Copilot/
        │   ├── ICopilotClient.cs
        │   ├── CopilotClient.cs
        │   ├── CopilotEndpoints.cs         ← accountType → base URL
        │   └── CopilotHeaderFactory.cs     ← VS Code-shaped request headers
        │
        ├── Auth/
        │   ├── IAuthService.cs             ← public interface
        │   ├── AuthService.cs              ← single public implementation
        │   ├── GitHubAuthClient.cs         ← internal: device-code + poll
        │   ├── CopilotTokenClient.cs       ← internal: GitHub→Copilot token exchange
        │   └── TokenStore.cs               ← internal: file persistence + ACL/DPAPI
        │
        ├── Configuration/
        │   ├── AppConfig.cs                ← record (port, accountType, defaultModel, ...)
        │   ├── ConfigStore.cs              ← JSON read/write
        │   └── CommandLineOverrides.cs     ← --port etc. override file settings
        │
        ├── Models/
        │   ├── Anthropic/                  ← Anthropic protocol DTOs
        │   ├── OpenAi/                     ← OpenAI protocol DTOs
        │   ├── GitHub/                     ← OAuth + Copilot token responses
        │   └── JsonContext.cs              ← single JsonSerializerContext
        │
        ├── State/
        │   └── Errors.cs                   ← typed exceptions (CopilotHttpError, etc.)
        │
        └── WebAssets/                      ← <EmbeddedResource>
            ├── index.html
            ├── app.css
            └── app.js
```

Future split (only if size or compile time forces it):

```
src/
  CopilotBridge.Cli/                     # Program.cs + Hosting + Endpoints + WebAssets
  CopilotBridge.Core/                    # Translation + Copilot + Auth + Configuration + Models + State
```

---

## 9. Milestone plan

### M1 — Anthropic native passthrough (current goal)

In dependency order, with acceptance criteria.

1. **CopilotTokenClient + AuthService.GetCopilotTokenAsync()**
   - `GET https://api.github.com/copilot_internal/v2/token`; parse `token`, `expires_at`, `refresh_in`, `endpoints.api`
   - In-memory cache + Timer firing at `refresh_in - 60s`
   - **Base URL is taken from the token's `endpoints.api`** (no more accountType configuration)
   - Acceptance: a temporary `copilot-bridge auth copilot-status` subcommand prints the token head + expiry + base URL

2. **CopilotClient.GetModelsAsync() + a temporary `debug list-models` subcommand**
   - `GET <baseUrl>/models`, with the official 7 headers per §3.0.4
   - Filter to models whose `supported_endpoints` contains `/v1/messages`
   - Acceptance: lists the Claude models available on the user's account, with each model's `supported_endpoints`

3. **(optional live probe) `debug echo` subcommand**
   - `POST <baseUrl>/v1/messages` with a minimal payload `{model, messages:[{role:user,content:"hello"}], stream:true, max_tokens:128}`
   - Dump the SSE stream to stdout
   - Acceptance: receive a real Anthropic-style event sequence — **confirms our header set is sufficient**

4. **Preprocessing pipeline** (~10 pure functions, AOT-friendly; see research doc §3.6 for details)
   - Model name normalization (5 regex patterns)
   - Strip `system[*].cache_control.scope`
   - Filter assistant `thinking` blocks (keep only those with a signature)
   - Fix trailing assistant message
   - Sanitize IDE tools (`mcp__ide__executeCode` etc.)
   - Merge `tool_result` + adjacent text in the same message
   - Strip `Tool loaded.` boundary
   - Detect compact / auto-continue requests (affects `x-initiator`)
   - Detect warmup requests (no tools + has anthropic-beta + not compact → force small model)
   - Infer `x-initiator` (user vs agent)
   - Acceptance: unit tests cover each rule

5. **CopilotHeaderFactory** (per research doc §3.0.4 — the official 7 headers)
   - `X-GitHub-Api-Version: 2026-01-09`, `Copilot-Integration-Id: vscode-chat`, `VScode-SessionId/MachineId`, `Editor-Device-Id/Plugin-Version/Version`
   - + `Authorization: Bearer <copilot-token>`
   - + `anthropic-beta` (generated by us based on model capabilities + config — **not** forwarded from Claude Code)
   - Acceptance: snapshot test (input: model + capabilities → output: header dict)

6. **`POST /v1/messages` endpoint** (Kestrel + minimal API)
   - Accept Claude Code request → run preprocessing pipeline → call CopilotClient → forward SSE events as-is
   - Filter out `data: [DONE]` (not part of the Anthropic protocol)
   - Acceptance: curl with non-streaming returns an Anthropic response; curl with streaming sees `message_start ... message_stop`

7. **`GET /v1/models` endpoint**
   - Convert Copilot models to Anthropic-style; only expose ones with `/v1/messages` in `supported_endpoints`
   - Acceptance: returns a model picker JSON Claude Code can read at startup

8. **`POST /v1/messages/count_tokens` endpoint**
   - M1 simplification: always returns `{"input_tokens": 1}`
   - Acceptance: Claude Code starts without 404

9. **End-to-end**
   - Configure Claude Code with `ANTHROPIC_BASE_URL=http://localhost:<port>` + `ANTHROPIC_AUTH_TOKEN=dummy`
   - Run a multi-turn conversation with tool calls
   - Acceptance: complete 5+ turns including tool_use/tool_result

Total complexity is roughly half of v0.1's plan — no Translation layer, no streaming-translation state machine.

### M2 — Config page + size optimization

- HTML config page + `/api/auth/*`, `/api/config`, browser-driven login flow
- Size analysis on publish output; trim unreferenced ASP.NET sub-components
- `linux-x64` / `osx-arm64` publish

### M3 — Fallback path for non-Claude models

- `/responses` translation path (GPT-5 / o3 family)
- `/chat/completions` translation path (older GPT-4o / Gemini / Grok)
  - This is when v0.1's full Anthropic↔OpenAI translation + streaming state machine actually gets built
- `/v1/messages/count_tokens` real implementation (local tokenizer estimate or `ANTHROPIC_API_KEY` forward)
- Expose `POST /v1/chat/completions` for non-Claude-Code clients

---

## 10. Open questions

- **Kestrel vs raw HttpListener?** Kestrel is mature on AOT but adds size. Raw HttpListener is smaller but means hand-writing SSE writes. M1 leans Kestrel (correctness first; revisit size in M2).
- **How to fetch vscode-version?** copilot-api caches the GitHub stable version. M1 hardcodes; M2 implements caching.
- **What HTML stack?** Vanilla JS + one CSS file is enough; no framework in M1.
- **Config changes: live or restart?** M1 simplifies: write to disk + tell user to restart. Hot reload is a future version.
- **Multi-instance concurrency safety?** Out of scope for M1 (single-process expected); the file persistence layer uses `FileShare.None` so a second instance fails fast rather than corrupts data.

---

## 11. Decision log

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-05-06 | .NET 10 Native AOT | Single-file + small binary is the hard goal |
| 2026-05-06 | Single csproj, folder-based modules | Multiple csprojs each pay an AOT cost; keep it simple |
| 2026-05-06 | AuthService is a self-contained facade | Callers don't need to know about the token lifecycle; isolates protocol details |
| 2026-05-06 | Translation = pure functions + streaming state machine | Easy to test, AOT-friendly, easy to diff against the reference |
| 2026-05-06 | OptimizationPreference=Size (RamDrive uses Speed) | User prioritizes binary size over latency |
| 2026-05-06 | M1 uses Kestrel | Correctness first |
| 2026-05-06 (v0.2) | M1 switches to Anthropic native passthrough (no translation) | Research confirmed Copilot has an official `/v1/messages` endpoint (`@vscode/copilot-api` package source); translation can be avoided entirely |
| 2026-05-06 (v0.2) | Base URL comes from the Copilot token's `endpoints.api`, not user-configured accountType | Aligns with the official `DomainService._getCAPIUrl`; more robust (business/enterprise auto-resolved) |
| 2026-05-06 (v0.2) | Header set: official 7 + Authorization + Content-Type + anthropic-beta only | Reverse-engineered projects' extra headers are likely redundant (the official `_mixinHeaders` only emits 7); start small, add only when needed |
| 2026-05-06 (v0.2) | `anthropic-beta` is generated by us based on model capabilities, not forwarded from Claude Code | Mirrors `chatEndpoint.ts:182-215` |
