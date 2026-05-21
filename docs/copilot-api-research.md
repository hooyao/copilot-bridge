# Copilot LLM API and Claude Code client protocol research

> Status: research v0.1 · 2026-05-06
>
> This document's goal: pin down what Claude Code sends and what GitHub Copilot accepts on both sides, to serve as the protocol-level ground truth for implementing copilot-bridge. No .NET code here — just protocol facts and implementation guidance.

---

## 0. TL;DR — major architectural finding

**Initial assumption** (design.md v0.1): Copilot only exposes the OpenAI shape; we'd need a full Anthropic↔OpenAI translation layer.

**Actual fact**: **Copilot has a native Anthropic Messages API endpoint at `POST https://api.githubcopilot.com/v1/messages`**. Requests for Claude-family models **don't need translation** — minimal preprocessing + auth header injection, then pass the Anthropic payload through to Copilot.

**This is not reverse-engineering speculation** — the URL path comes from Microsoft's own `@vscode/copilot-api` npm package (v0.3.0, located at `references/vscode-copilot-api-pkg/` in this repo). The `DomainService.capiMessagesURL` getter literally returns `${capiBaseURL}/v1/messages`. See [§3.0](#30-protocol-source--official-vscodecopilot-api-package).

| Model class | Endpoint on Copilot | What we do |
| --- | --- | --- |
| **Claude (`claude-*`)** | `/v1/messages` (Anthropic native) | **Passthrough + header swap + light preprocessing** ← M1's only path |
| GPT-5 / o3 reasoning models | `/responses` (OpenAI Responses) | Not in M1 (user not requesting yet) |
| Older GPT / Gemini / Grok | `/chat/completions` (OpenAI Chat) | Not in M1 (user not requesting yet) |

Routing rule: `GET /models` returns each model's `supported_endpoints: string[]`, a subset of `/v1/messages`, `/responses`, `/chat/completions`. M1 only supports `/v1/messages`; other models return 400.

Implication: M1 implementation shrinks from "full Anthropic↔OpenAI translation + streaming state machine" to "auth + header rewrite + 4 preprocessing rules + body/SSE passthrough" — roughly **80% less code**. `docs/design.md` §9 needs to be rewritten.

---

## 1. Reference implementations overview

| Repo | Tech | Location under `references/` | Value |
| --- | --- | --- | --- |
| [ericc-ch/copilot-api](https://github.com/ericc-ch/copilot-api) | TypeScript / Bun | `references/copilot-api/` | First-generation reverse engineering. **Only** has `/chat/completions` + Anthropic↔OpenAI translation. M1 no longer uses its translation logic as the main path, but its **headers + auth flow** are still gold. |
| [caozhiyuan/copilot-api](https://github.com/caozhiyuan/copilot-api) | TypeScript / Bun (fork of above) | `references/copilot-api-anthropic/` | **Primary reference**. Adds `services/copilot/create-messages.ts` for native `/v1/messages`, `create-responses.ts` for `/responses`, with the router in `routes/messages/handler.ts`. The "messages-proxy" mode VS Code uses is reproduced faithfully. |
| [whtsky/copilot2api](https://github.com/whtsky/copilot2api) | Go | `references/copilot2api/` | Same three-endpoint routing model. Test fixtures with `supported_endpoints` confirm the routing logic. |
| [microsoft/vscode-copilot-chat](https://github.com/microsoft/vscode-copilot-chat) | TypeScript (VS Code extension) | `references/vscode-copilot-chat-snippets/` (selected files) | **Most authoritative**. The VS Code Copilot Chat extension's own Messages API integration code — itself the official client of Copilot's `/v1/messages`. |
| [@vscode/copilot-api npm package v0.3.0](https://www.npmjs.com/package/@vscode/copilot-api) | TypeScript (compiled dist) | `references/vscode-copilot-api-pkg/` | **Definitive protocol source**. Microsoft's official npm package, containing the `CAPIClient` class with **all endpoint URL paths** and **all auto-injected HTTP headers**. The most authoritative protocol definition we can get. |
| [Joouis/agent-maestro](https://github.com/Joouis/agent-maestro) | TypeScript (VS Code extension) | `references/agent-maestro/` | Uses VS Code's LM API (doesn't hit Copilot HTTP directly). Useful only as a secondary reference for Anthropic↔VS Code type mapping; **not our implementation path**. |

---

## 2. The API Claude Code uses (what it sends)

> Sources: [Claude Code LLM gateway docs](https://code.claude.com/docs/en/llm-gateway), `references/copilot-api-anthropic/src/routes/messages/handler.ts`, the Anthropic Messages API docs.

### 2.1 Endpoints Claude Code calls

| When | Method + path | Purpose |
| --- | --- | --- |
| At startup | `GET /v1/models` | Discover the gateway's models, populate the model picker. **If you don't expose this, Claude Code stalls before the first message.** |
| Each turn | `POST /v1/messages` | Main conversation. Streaming (default) or non-streaming. |
| Before a large request | `POST /v1/messages/count_tokens` | Estimate input tokens; decides whether to trigger auto-compact. |

> Claude Code does NOT call `/v1/messages/streaming`, `/responses`, or `/chat/completions`. Our server only needs the three above.

### 2.2 How Claude Code authenticates the gateway

Driven by environment variables (set in `~/.claude/settings.json` or the shell env):

```
ANTHROPIC_BASE_URL=http://localhost:<port>          # our server's address
ANTHROPIC_AUTH_TOKEN=<bearer>     # preferred: sent as Authorization: Bearer <bearer>
ANTHROPIC_API_KEY=<key>           # fallback: sent as x-api-key: <key>
ANTHROPIC_CUSTOM_HEADERS="..."    # arbitrary extra headers (semicolon-separated)
```

If `ANTHROPIC_AUTH_TOKEN` is set, Claude Code sends `Authorization: Bearer ...`; otherwise it sends `x-api-key: ...`.

Our gateway **doesn't actually need Claude Code's token** — internally it authenticates via GitHub OAuth + Copilot tokens. So we can either accept any value ("dummy") or validate it for local protection. M1 picks "accept any value" (matching what copilot-api does).

### 2.3 Two headers Claude Code requires the gateway to forward

> From the official Claude Code docs: the gateway must propagate these two headers upstream.

```
anthropic-version: 2023-06-01
anthropic-beta: <optional comma-separated list>
```

Common `anthropic-beta` values, by purpose:

| Value | Purpose | What we do |
| --- | --- | --- |
| `claude-code-...` | Claude Code marker — identifies the client to the gateway | **Detect, but don't forward** (Copilot doesn't recognize it) |
| `interleaved-thinking-2025-05-14` | Enable interleaved thinking (thinking interleaved with text) | **Forward** if the Copilot model supports it |
| `context-management-2025-06-27` | Enable context management (auto-cleanup of old tool_use) | **Forward** if the model supports it |
| `advanced-tool-use-2025-11-20` | Advanced tool use (default-on in VS Code Copilot) | **Forward** if the model supports it |
| `token-counting-2024-11-01` | Beta for the count_tokens endpoint | **Forward** (only on count_tokens) |
| Older betas like `prompt-caching-2024-07-31` | Stale beta names | **Drop** (Copilot rejects unknown betas) |

The whitelist used in `caozhiyuan/copilot-api` (`create-messages.ts:28-32`):

```ts
const allowedAnthropicBetas = new Set([
  "interleaved-thinking-2025-05-14",
  "context-management-2025-06-27",
  "advanced-tool-use-2025-11-20",
])
```

Anything not on the whitelist gets stripped — Copilot strictly validates and 400s on unknown betas.

### 2.4 Request body shape Claude Code sends

Standard Anthropic Messages API shape, see <https://platform.claude.com/docs/en/api/messages>:

```jsonc
{
  "model": "claude-sonnet-4-5-20250929",   // usually with version-date suffix
  "system": "string" | [{"type":"text","text":"...","cache_control":{"type":"ephemeral"}}],
  "messages": [
    {"role":"user","content":"string" | [<ContentBlock>...]},
    {"role":"assistant","content":[<ContentBlock>...]},
    {"role":"user","content":[{"type":"tool_result","tool_use_id":"...","content":"..."}]}
  ],
  "max_tokens": 8192,
  "stream": true,
  "tools": [{"name":"...","description":"...","input_schema":{...}}],
  "tool_choice": {"type":"auto"},
  "temperature": 1,
  "top_p": 0.95,
  "stop_sequences": ["..."],
  "thinking": {"type":"enabled","budget_tokens":4096},
  "metadata": {"user_id":"..."},
  "anthropic_beta": [...]   // may also be in the header
}
```

ContentBlock types Claude Code emits:
- `{type:"text", text, cache_control?}`
- `{type:"image", source:{type:"base64",media_type,data} | {type:"url",url}}`
- `{type:"document", source:{type:"base64",media_type:"application/pdf",data}}`
- `{type:"tool_use", id, name, input}` (in assistant messages)
- `{type:"tool_result", tool_use_id, content, cache_control?}` (in user messages)
- `{type:"thinking", thinking, signature}` / `{type:"redacted_thinking", data}` (in assistant messages, when echoing prior turns)

### 2.5 Response shape Claude Code expects

Non-streaming (`stream:false`): a standard Anthropic Message object.

Streaming (`stream:true`, **default**): SSE event sequence; event types:

```
event: message_start
data: {"type":"message_start","message":{"id":"msg_...","type":"message","role":"assistant","model":"...","content":[],"stop_reason":null,"usage":{"input_tokens":N,"output_tokens":1,...}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":12}}

event: message_stop
data: {"type":"message_stop"}
```

Tool-call content_blocks:

```
content_block_start: {"type":"tool_use","id":"toolu_xxx","name":"read_file","input":{}}
content_block_delta: {"type":"input_json_delta","partial_json":"{\"path\":\""}
content_block_delta: {"type":"input_json_delta","partial_json":"src/main.ts\"}"}
content_block_stop
```

Thinking blocks:

```
content_block_start: {"type":"thinking","thinking":""}
content_block_delta: {"type":"thinking_delta","thinking":"reasoning..."}
content_block_delta: {"type":"signature_delta","signature":"<encrypted>"}
content_block_stop
```

### 2.6 stop_reason mapping in Claude Code

From `messagesApi.ts:1006-1018`:

```ts
switch (stopReason) {
  case 'refusal':                          finishReason = ClientDone;
  case 'max_tokens':
  case 'model_context_window_exceeded':    finishReason = Length;
  default:                                 finishReason = Stop;
}
```

We must map `stop_reason` correctly, especially `model_context_window_exceeded` — Claude Code triggers auto-compact on it.

---

## 3. The API Copilot offers (what it accepts)

### 3.0 Protocol source — official `@vscode/copilot-api` package

**Everything in this section comes from Microsoft's official npm package** (`@vscode/copilot-api`, a dependency of the VS Code Copilot Chat extension), not reverse-engineering speculation. The relevant code is in `references/vscode-copilot-api-pkg/package/dist/index.js`.

#### 3.0.1 All endpoint URL paths (`DomainService` getters)

```js
// dist/index.js, ~offset 2300
get capiBaseURL()        { return this._capiBaseUrl; }                              // default "https://api.githubcopilot.com"
get capiChatURL()        { return `${this._capiBaseUrl}/chat/completions`; }
get capiResponsesURL()   { return `${this._capiBaseUrl}/responses`; }
get capiMessagesURL()    { return `${this._capiBaseUrl}/v1/messages`; }              // ← Anthropic endpoint
get capiEmbeddingsURL()  { return `${this._capiBaseUrl}/embeddings`; }
get capiModelsURL()      { return `${this._capiBaseUrl}/models`; }
get capiAutoModelURL()   { return `${this.capiModelsURL}/session`; }                 // /models/session
get capiModelRouterURL() { return `${this.capiAutoModelURL}/intent`; }               // /models/session/intent
// dotcom side (api.github.com by default)
get tokenURL()           { return `${this._dotcomAPIUrl}/copilot_internal/v2/token`; }
get tokenNoAuthURL()     { return `${this._dotcomAPIUrl}/copilot_internal/v2/nltoken`; }
get copilotUserInfoURL() { return `${this._dotcomAPIUrl}/copilot_internal/user`; }
get capiPingURL()        { return `${this._domainService.capiBaseURL}/_ping`; }
```

#### 3.0.2 Base URL resolution

```js
// dist/index.js, ~offset 2160
_getCAPIUrl(token) {
  return token && token.endpoints.api || "https://api.githubcopilot.com";
}
```

In other words, the base URL **comes from the Copilot token's `endpoints.api` field** (returned by `GET /copilot_internal/v2/token`).
- Individual: token usually omits this field or sets it to `https://api.githubcopilot.com`
- Business / Enterprise: the token returns the per-account domain

**Conclusion**: our implementation should **not** stitch URLs based on an `accountType` string — use whatever the Copilot token returns. caozhiyuan/copilot-api's if/else is a reverse-engineering simplification; the official approach is cleaner.

#### 3.0.3 RequestType → URL mapping (the `makeRequest` switch)

```js
// dist/index.js, ~offset 14000
case "ChatCompletions": return fetch(_domainService.capiChatURL, ...);       // POST /chat/completions
case "ChatResponses":   return fetch(_domainService.capiResponsesURL, ...);  // POST /responses
case "ChatMessages":    return fetch(_domainService.capiMessagesURL, ...);   // POST /v1/messages
case "CAPIEmbeddings":  return fetch(_domainService.capiEmbeddingsURL, ...);
case "Models":          return fetch(_domainService.capiModelsURL, ...);     // GET /models
case "AutoModels":      return fetch(_domainService.capiAutoModelURL, ...);  // /models/session
case "ModelRouter":     return fetch(_domainService.capiModelRouterURL, ...);// /models/session/intent
case "ListModel":       return fetch(`${capiModelsURL}/${modelId}`, ...);
case "ModelPolicy":     return fetch(`${capiModelsURL}/${modelId}/policy`, ...);
case "CopilotToken":    return fetch(_domainService.tokenURL, ...);          // GET /copilot_internal/v2/token
```

#### 3.0.4 Auto-injected HTTP headers (`_mixinHeaders`)

```js
async _mixinHeaders(t, e) {
  if (!C(e.type)) return;  // only adds headers for chat/messages/responses-class types
  let r = t.headers || {};
  r["X-GitHub-Api-Version"]  = "2026-01-09";                                 // ← latest official API version
  r["VScode-SessionId"]      = this._extensionInfo.sessionId;
  r["VScode-MachineId"]      = this._extensionInfo.machineId;
  r["Editor-Device-Id"]      = this._extensionInfo.deviceId;
  r["Editor-Plugin-Version"] = `copilot-chat/${this._extensionInfo.version}`;
  r["Editor-Version"]        = `vscode/${this._extensionInfo.vscodeVersion}`;

  let i = "";
  if (!t.suppressIntegrationId) {
    i = "code-oss";  // default
    if (this._integrationId && this._hmacSecret) {
      i = this._integrationId;                  // private integrationId (requires HMAC)
    } else if (this._copilotSku === "no_auth_limited_copilot") {
      i = "vscode-nl";
    } else if (this._licenseCheckSucceeded && this._extensionInfo.buildType === "prod") {
      i = "vscode-chat";                        // ← production VS Code Copilot value
    } else if (this._extensionInfo.buildType === "dev" && this._hmacSecret) {
      i = "vscode-chat-dev";
    }
    r["Copilot-Integration-Id"] = i;
  }
  if (i === "vscode-chat-dev") {
    r["Request-Hmac"] = await y(this._hmacSecret);  // dev-only
  }
  t.headers = r;
}
```

**Authoritative header set (required for chat/messages/responses)**:

```http
X-GitHub-Api-Version: 2026-01-09         # ← newer than 2025-04-01 / 2025-10-01 used by reverse-engineered projects
Copilot-Integration-Id: vscode-chat      # the prod VS Code value
VScode-SessionId: <uuid>                 # mock VS Code session id
VScode-MachineId: <uuid>                 # mock VS Code machine id
Editor-Device-Id: <uuid>                 # mock device id
Editor-Plugin-Version: copilot-chat/<version>     # e.g. copilot-chat/0.46.0
Editor-Version: vscode/<vscodeVersion>            # e.g. vscode/1.95.0
Authorization: Bearer <copilot-token>             # added by the fetcher; token from /copilot_internal/v2/token
Content-Type: application/json                    # added by the fetcher
```

**Note**: the extra headers reverse-engineered projects send (`openai-intent`, `x-initiator`, `x-request-id`, `x-interaction-type`, `x-agent-task-id`, `x-vscode-user-agent-library-version`) are **not** in `_mixinHeaders`. They come from one of:
- `chatEndpoint.ts`'s `getExtraHeaders` (only `anthropic-beta` and `X-Model-Provider-Preference`; see §3.0.5)
- Reverse-engineering simulations that go beyond what's actually required (Copilot accepts but doesn't require them)
- Per-model `requestHeaders` in `/models` metadata (rare, model-specific)

**M1 implementation guidance**: send only **the official 7 headers + Authorization + Content-Type + anthropic-beta** first; see if it works. If specific behaviors (e.g. quota accounting that depends on `x-initiator`) demand more, add as needed.

#### 3.0.5 Extra headers `chatEndpoint.ts` adds for the messages API

```ts
// references/vscode-copilot-chat-snippets/chatEndpoint.ts:182-215
public getExtraHeaders(location?: ChatLocation): Record<string, string> {
  const headers: Record<string, string> = { ...this.modelMetadata.requestHeaders };

  const isAllowedConversationAgentModel =
    location === ChatLocation.Agent || location === ChatLocation.MessagesProxy;

  if (isAllowedConversationAgentModel && this.useMessagesApi) {
    const modelProviderPreference = this._configurationService
      .getConfig(ConfigKey.TeamInternal.ModelProviderPreference);
    if (modelProviderPreference) {
      headers['X-Model-Provider-Preference'] = modelProviderPreference;
    }

    const betaFeatures: string[] = [];
    if (!this.supportsAdaptiveThinking) {
      betaFeatures.push('interleaved-thinking-2025-05-14');
    }
    if (isAnthropicContextEditingEnabled(this.model, ...)) {
      betaFeatures.push('context-management-2025-06-27');
    }
    if (isAnthropicToolSearchEnabled(this.model, ...)) {
      betaFeatures.push('advanced-tool-use-2025-11-20');
    }
    if (betaFeatures.length > 0) {
      headers['anthropic-beta'] = betaFeatures.join(',');
    }
  }
  return headers;
}
```

`anthropic-beta` is **generated by VS Code based on model capabilities + config** — it's not forwarded from the client. This means:

- We **shouldn't** forward Claude Code's `anthropic-beta` header verbatim to Copilot
- We should read model capabilities and generate the right beta list ourselves per §3.7 / §7.1

`X-Model-Provider-Preference`: VS Code internal A/B testing — ignorable.

`modelMetadata.requestHeaders`: a few models include a `request_headers` field in `/models` telling clients to send extra headers. Rare; forward only when present.

### 3.1 Base URL (legacy framing — see §3.0.2)

The historical reverse-engineering approach (**superseded by §3.0.2**):

```
individual   → https://api.githubcopilot.com
business     → https://api.business.githubcopilot.com
enterprise   → https://api.enterprise.githubcopilot.com
```

**Official approach**: read the `endpoints.api` field from the `GET /copilot_internal/v2/token` response; fall back to `https://api.githubcopilot.com` if missing. The token carries the field; business/enterprise tokens naturally return the right domain. No `accountType` config needed from the user.

### 3.2 Endpoints Copilot offers

| Path | Shape | Note |
| --- | --- | --- |
| `GET /models` | OpenAI-style models list, but each model has `supported_endpoints` and `capabilities` | Required for routing |
| `POST /v1/messages` | **Anthropic Messages API** (native) | Claude models |
| `POST /responses` | OpenAI Responses API | GPT-5, o-mini family |
| `POST /chat/completions` | OpenAI Chat Completions | Older GPT-4o / Gemini / Grok |
| `POST /embeddings` | OpenAI embeddings | Out of scope for M1 |

`GET /v1/messages/count_tokens` **does not exist** on Copilot — it's an upstream-Anthropic-only endpoint. Our gateway exposes it but internally either does a local estimate (`o200k_base` tokenizer + 1.15× claude multiplier) or forwards to `https://api.anthropic.com/v1/messages/count_tokens` (if `ANTHROPIC_API_KEY` is configured).

### 3.3 `GET /models` response shape (key)

From `references/copilot-api-anthropic/src/services/copilot/get-models.ts`:

```ts
{
  data: [
    {
      id: "claude-sonnet-4.6",
      name: "Claude Sonnet 4.6",
      object: "model",
      vendor: "Anthropic",
      version: "...",
      preview: false,
      model_picker_enabled: true,
      capabilities: {
        family: "claude-sonnet-4.6",
        type: "chat",
        tokenizer: "o200k_base",
        limits: {
          max_context_window_tokens: 200000,
          max_output_tokens: 8192,
          max_prompt_tokens: 128000
        },
        supports: {
          tool_calls: true,
          parallel_tool_calls: true,
          streaming: true,
          structured_outputs: true,
          vision: true,
          adaptive_thinking: true,
          reasoning_effort: ["low", "medium", "high"],
          max_thinking_budget: 32000,
          min_thinking_budget: 1024
        }
      },
      supported_endpoints: ["/v1/messages"]   // ← routing decision!
    },
    // ...
  ],
  object: "list"
}
```

**`supported_endpoints` is M1's only reliable routing signal**:

- Contains `/v1/messages` → Claude model, native passthrough
- Contains `/responses` → GPT-5/o3 family, **not in M1**
- Neither → older OpenAI shape, **not in M1**

> Note: Claude Code calls `GET /v1/models` at startup (see §2.1). Our gateway must convert Copilot's model list to **Anthropic-style** (drop `supported_endpoints`, rename `capabilities` etc.) so Claude Code can render it. Or simplify maximally: only expose `/v1/messages`-supporting models.

### 3.4 Request headers Copilot expects (for `/v1/messages`)

> ⚠ **This section is reverse-engineered observation. The authoritative header list is [§3.0.4](#304-auto-injected-http-headers-_mixinheaders).** What's listed below is caozhiyuan/copilot-api's "feature-rich" simulation (official + several non-required extras), useful as reference, but M1 should try sending only the official 7 first and add extras if needed.

Historical observation (from `references/copilot-api-anthropic/src/lib/api-config.ts:225-294`):

```http
Authorization: Bearer <copilot-token>          # the Copilot token, NOT the GitHub token
content-type: application/json
copilot-integration-id: vscode-chat
editor-version: vscode/<vscode-version>        # e.g. vscode/1.95.0
editor-plugin-version: copilot-chat/<version>  # e.g. copilot-chat/0.46.0
user-agent: GitHubCopilotChat/<version>        # e.g. GitHubCopilotChat/0.46.0
openai-intent: conversation-agent
x-github-api-version: 2025-10-01
x-request-id: <uuid v4>
x-vscode-user-agent-library-version: electron-fetch
x-agent-task-id: <uuid v4, usually = x-request-id>
x-interaction-type: conversation-agent
x-initiator: user | agent                      # see §3.5
editor-device-id: <optional>                   # mock VS Code device id
vscode-machineid: <optional>
vscode-sessionid: <optional>
copilot-vision-request: true                   # only when payload contains image content
anthropic-beta: <whitelisted list>             # optional, see §2.3
```

**Variant: when the request comes via Claude Code's "messages-proxy" mode, VS Code uses a different header set (see `prepareMessageProxyHeaders`):**

```http
user-agent: vscode_claude_code/2.1.112 (external, sdk-ts, agent-sdk/0.2.112)
x-interaction-type: messages-proxy
openai-intent: messages-proxy
x-agent-task-id: <uuid>
x-request-id: <same uuid>
# omit copilot-integration-id
```

This mode is specifically for Claude Code routing through `ANTHROPIC_BASE_URL` to a gateway — **exactly our use case**. Recommendation: M1 default to "messages-proxy" headers (less likely to trip anti-abuse).

### 3.5 `x-initiator` semantics

From `caozhiyuan/copilot-api/src/services/copilot/create-messages.ts:88-100`:

```ts
let isInitiateRequest = false
const lastMessage = payload.messages.at(-1)
if (lastMessage?.role === "user") {
  isInitiateRequest = Array.isArray(lastMessage.content)
    ? lastMessage.content.some(block => block.type !== "tool_result")
    : true
}
headers["x-initiator"] = isInitiateRequest ? "user" : "agent"
```

Simplified: if the last message is a user message and **contains non-tool_result content** → `user`; otherwise → `agent`.
Affects Copilot's quota accounting (user-initiated vs agent loop).

### 3.6 What Copilot's `/v1/messages` accepts in the request body

**Almost identical to the official Anthropic API**, with a few additions and constraints:

#### Additions (Copilot accepts / expects extra fields)

- `output_config: { effort: "low"|"medium"|"high" }` — pairs with `thinking: {type:"adaptive"}`
- `context_management: { edits: [...] }` — auto-cleanup of old tool_use / thinking turns
- `tools[].defer_loading: boolean` — for server-side tool search
- `tools[]` may include `{name:"tool_search_tool_regex", type:"tool_search_tool_regex_20251119"}` — built-in tool search
- `system[].cache_control` and `messages[*].content[*].cache_control` — `{type:"ephemeral"}` (max 4)
- Response may include `copilot_annotations.IPCodeCitations` — public-repo code citation warnings

#### Subtractions / restrictions (Copilot rejects / must be stripped)

From `caozhiyuan/copilot-api/src/routes/messages/preprocess.ts`:

```ts
// 1. Strip cache_control.scope from system blocks; Copilot rejects extra fields
stripCacheControl(payload)

// 2. Filter thinking blocks in assistant messages
//    Keep only: thinking non-empty AND signature non-empty AND signature doesn't contain '@'
//    (Drop "Thinking..." placeholders, drop unsigned ones)
filterAssistantThinkingBlocks(payload)

// 3. No trailing assistant message (Anthropic treats it as a prefill request, returns 400)
//    On detection, append {role:"user",content:[{type:"text",text:"Please continue."}]}

// 4. tool_choice="any" / "tool" is incompatible with thinking; disable forced tools when thinking is on

// 5. Models that support adaptive_thinking → force thinking={type:"adaptive"}
//    and set output_config.effort based on reasoning_effort
```

#### Claude Code-specific preprocessing

Claude Code emits some shapes that need normalization:

```ts
// 6. Drop the mcp__ide__executeCode tool (unless defer_loading is set)
//    Rewrite mcp__ide__getDiagnostics description to match VS Code
sanitizeIdeTools(payload)

// 7. If a user message's content array contains both tool_result and text,
//    merge the text into tool_result.content
//    (otherwise Copilot treats it as a fresh premium request)
mergeToolResultForClaude(payload)

// 8. Strip the "Tool loaded." tool reference turn boundary
stripToolReferenceTurnBoundary(payload)

// 9. Detect compact / auto-continue requests:
//    - System prompt starts with "<compact-summary>" etc. → COMPACT_REQUEST
//    - Last user message starts with specific strings → COMPACT_AUTO_CONTINUE
//    Set x-initiator=agent and x-interaction-type=conversation-other (COMPACT_REQUEST)

// 10. Requests with no tools + has anthropic-beta + not compact → force the small model
//     (Claude Code 2.0.28+ warmup requests consume premium quota; this avoids that)
```

### 3.7 Copilot model names vs Claude Code model names

Claude Code sends model ids like `claude-sonnet-4-5-20250929`, `claude-opus-4-1-20250101` — **with a version date suffix**.

Copilot model ids are **dotted variants**: `claude-sonnet-4.5`, `claude-opus-4.1` (no date suffix).

We need to normalize:

```ts
// from caozhiyuan/copilot-api/src/lib/models.ts (5 regex patterns)
"claude-sonnet-4-5-20250929" → "claude-sonnet-4.5"
"claude-opus-4-1-20250101"   → "claude-opus-4.1"
"claude-haiku-4-5"           → "claude-haiku-4.5"
"claude-sonnet-4-5"          → "claude-sonnet-4.5"
```

Concretely: replace `-(\d+)-(\d+)$` with `.$1.$2`, and strip any `claude-*-\d+-\d+-\d{8}` date suffix.

---

## 4. Copilot SSE stream (response format)

**Identical in shape to the Anthropic Messages API SSE format** — that's why passthrough is simple.

The streaming branch of `handleWithMessagesApi` in `caozhiyuan/copilot-api/src/routes/messages/api-flows.ts:306-340` is essentially "forward each SSE event verbatim":

```ts
for await (const event of response) {
  const eventName = event.event   // e.g. "message_start"
  const data = event.data ?? ""
  if (data === "[DONE]") break
  if (!data) continue
  // Only processing: parse message_start and message_delta to extract usage (for local accounting)
  // Then write the SSE through unchanged
  await stream.writeSSE({ event: eventName, data })
}
```

**Extension events to be aware of**: Copilot's SSE stream includes fields not in the standard Anthropic protocol, mostly in deltas:

- `copilot_annotations.IPCodeCitations` — appears inside `content_block_delta`, public-repo code citation warnings
- `usage.server_tool_use.tool_search_requests` — server-side tool search request count
- `context_management` — appears in `message_delta`, reports which `applied_edits` happened
- `content_block_start` type extensions: `server_tool_use`, `tool_search_tool_result`

Claude Code may not understand these extension fields. **Passthrough is fine for M1** — the Anthropic SSE protocol is forgiving and Claude Code ignores unknown fields. We'll filter only if specific issues surface.

### 4.1 Empirically captured SSE event flow (playground stream experiment, 2026-05-06)

Capturing a "thinking-then-text" no-tools response on `claude-sonnet-4.6` — 18 events; **the 18th is an OpenAI-style `[DONE]` terminator the bridge MUST drop**:

| # | event | data summary |
| --- | --- | --- |
| 1 | `message_start` | id, model (**model is normalized in the response**: `claude-sonnet-4.6` → `claude-sonnet-4-6`); usage contains a nested `cache_creation` object |
| 2 | `content_block_start` | `{type:"thinking", thinking:"", signature:""}`, **index=0** |
| 3-5 | `content_block_delta` × 3 | `{type:"thinking_delta", thinking:"..."}` accumulating thinking text |
| 6 | `content_block_delta` (signature) | `{type:"signature_delta", signature:"..."}` — **emitted separately**, not mixed into a thinking_delta payload |
| 7 | `content_block_stop` | Closes thinking block (idx=0) |
| 8 | `content_block_start` | `{type:"text", text:""}`, **index=1** — index does not reset |
| 9-14 | `content_block_delta` × 6 | `{type:"text_delta", text:"..."}` accumulating text |
| 15 | `content_block_stop` | Closes text block (idx=1) |
| 16 | `message_delta` | `delta.stop_reason="end_turn"` + usage (final token counts) + **`copilot_usage`** (Copilot quota ledger, `{token_details, total_nano_aiu}`, extension field) |
| 17 | `message_stop` | Includes **`amazon-bedrock-invocationMetrics`** extension field (`{firstByteLatency:981, invocationLatency:1432, ...}`) — **reveals Copilot's backend is AWS Bedrock**, not direct Anthropic API |
| 18 | `event:message data:[DONE]` ⚠ | **OpenAI-style `[DONE]` terminator; not in the Anthropic spec; bridge MUST filter** (see §8.6) |

Implications for the protocol layer:

- **Index does not reset**: thinking gets idx=0, text gets idx=1. Bridge should not remap.
- **`signature_delta` is its own event** — separate from thinking text deltas. Just passthrough.
- **Copilot backend is AWS Bedrock**. Explains the shape of `usage.cache_creation` (a Bedrock addition) and `amazon-bedrock-invocationMetrics`. Direct Anthropic API responses don't have these.
- **Extension fields don't break passthrough** — the Anthropic SDK follows the "ignore unknown fields" principle and tolerates `copilot_usage`, the nested `cache_creation` object, `amazon-bedrock-invocationMetrics`, etc. But unknown **event types** like `[DONE]` are not safe to forward (see §8.6).

### 4.2 Empirically captured non-streaming response fields (playground effort experiment, 2026-05-06)

Non-streaming responses include these Copilot extension fields:

- `stop_details: null` — not in the Anthropic spec; Copilot-added (placeholder, possibly for future stop-reason details)
- `usage.cache_creation: {ephemeral_1h_input_tokens, ephemeral_5m_input_tokens}` — Anthropic's newer 1h-TTL prompt cache extension; Copilot reports the two TTL buckets separately

The top-level `usage` object is present on non-streaming responses (verified by `RawUsageShapeTests`, 2026-05-21) with this exact shape — same as `message_start.message.usage`:

```json
{
  "cache_creation": { "ephemeral_1h_input_tokens": 0, "ephemeral_5m_input_tokens": 0 },
  "cache_creation_input_tokens": 0,
  "cache_read_input_tokens": 0,
  "input_tokens": 14,
  "output_tokens": 4
}
```

All five keys appear even when cache buckets are zero. `message_delta.usage` carries the same scalars but **drops the nested `cache_creation`** — only the four flat counters are emitted on the final delta.

**This is Anthropic protocol design, not Copilot dropping data.** The official Anthropic SDK has two separate types: `BetaUsage` (non-streaming response + `message_start`, includes `cache_creation`) and `BetaMessageDeltaUsage` (streaming `message_delta`, no `cache_creation`). Our DTOs in `src/CopilotBridge.Cli/Models/Anthropic/Common/Usage.cs` mirror this split. The per-TTL cache breakdown is announced once at the start of the stream — repeating it on the final delta would be redundant, since only `output_tokens` actually grows during generation.

---

## 5. Two-stage authentication chain

```
┌──────────────────────────────────┐  device-code flow
│ user does GitHub OAuth login     │ ──────────────────► GitHub
│ once                             │ ◄────────── ghu_xxx (long-lived)
└──────────────────────────────────┘
                 │
                 │ ghu_xxx + githubHeaders()
                 ▼
GET https://api.github.com/copilot_internal/v2/token
                 │
                 ▼
┌──────────────────────────────────┐
│ {token: "tid=...", expires_at,    │ Copilot token lifetime ~30 min
│  refresh_in, ...}                │ Refresh before refresh_in - 60s
└──────────────────────────────────┘
                 │
                 │ tid=... + copilotHeaders() as Authorization Bearer
                 ▼
POST https://api.<accountType>.githubcopilot.com/v1/messages
```

The GitHub OAuth half is implemented (`Auth/GitHubAuthClient.cs` + `TokenStore.cs`). What's left:

1. `CopilotTokenClient` — `GET /copilot_internal/v2/token` (with the GitHub token)
2. `AuthService.GetCopilotTokenAsync()` — in-memory cache + background refresh timer; transparent to callers
3. `accountType` configuration (individual / business / enterprise) drives the base URL (superseded by §3.0.2 — driven by token instead)

### 5.1 Headers for the GitHub→Copilot token exchange

`api.github.com/copilot_internal/v2/token` requires (from `copilot-api-anthropic/src/lib/api-config.ts:297-310`):

```http
authorization: token <github-oauth-token>
user-agent: GitHubCopilotChat/0.46.0
x-github-api-version: 2025-04-01
x-vscode-user-agent-library-version: electron-fetch
```

Note `authorization: token <token>` (lowercase, `token` prefix), not `Bearer`.

Response:

```jsonc
{
  "token": "tid=abc;...",
  "expires_at": 1700000000,
  "refresh_in": 1500,
  // ... other quota-related fields
}
```

`expires_at` is Unix epoch seconds; `refresh_in` is seconds-from-now until refresh time. Use `refresh_in - 60s` for the timer.

---

## 6. Full request timing (M1 happy path)

```
Claude Code                copilot-bridge                Copilot
    │                            │                             │
    │ POST /v1/messages          │                             │
    │  body: <Anthropic>         │                             │
    │  Authorization: Bearer ... │                             │
    │  anthropic-version: ...    │                             │
    │  anthropic-beta: ...       │                             │
    │──────────────────────────►│                             │
    │                            │ (drop client auth header)   │
    │                            │ AuthService.GetCopilotToken()│
    │                            │ ──── (cache hit, in-mem) ──►│
    │                            │ ◄──── tid=...               │
    │                            │                             │
    │                            │ preprocess(body):           │
    │                            │   • normalize model name    │
    │                            │   • strip cache_control.scope│
    │                            │   • filter thinking blocks  │
    │                            │   • fix trailing assistant  │
    │                            │   • sanitize IDE tools       │
    │                            │   • merge tool_result+text  │
    │                            │   • detect compact, force x-initiator│
    │                            │                             │
    │                            │ build VS Code headers:      │
    │                            │   • Authorization: Bearer tid│
    │                            │   • copilot-integration-id  │
    │                            │   • editor-version, etc.    │
    │                            │   • x-initiator (inferred from body)│
    │                            │   • anthropic-beta (filtered)│
    │                            │                             │
    │                            │ POST /v1/messages           │
    │                            │  body: <preprocessed>       │
    │                            │  headers: <vscode-shaped>   │
    │                            │────────────────────────────►│
    │                            │ ◄──── SSE stream ─────────  │
    │                            │                             │
    │                            │ forward SSE events downstream│
    │                            │ (optional: parse usage for accounting)│
    │ ◄────── SSE stream ──────  │                             │
    │                            │                             │
```

The bulk of the implementation: **header construction** + **preprocessing pipeline** + **stream forwarding**. No translation, no state machine.

---

## 7. Model routing (which models M1 supports)

After `AuthService` has a Copilot token, call `GET /models` once. Filter to those whose `supported_endpoints` contains `/v1/messages` — that's the available model list.

#### Empirical results (2026-05-06, Enterprise account)

Captured via `copilot-bridge debug list-models`: **44 models total, 11 support `/v1/messages` (all Claude, all also support `/chat/completions`)**:

| Model ID | Endpoints |
| --- | --- |
| `claude-haiku-4.5` | `/chat/completions`, `/v1/messages` |
| `claude-sonnet-4` | `/chat/completions`, `/v1/messages` |
| `claude-sonnet-4.5` | `/chat/completions`, `/v1/messages` |
| `claude-sonnet-4.6` | `/chat/completions`, `/v1/messages` |
| `claude-opus-4.5` | `/chat/completions`, `/v1/messages` |
| `claude-opus-4.6` | `/v1/messages`, `/chat/completions` |
| `claude-opus-4.6-1m` | `/v1/messages`, `/chat/completions` |
| `claude-opus-4.7` | `/v1/messages`, `/chat/completions` |
| `claude-opus-4.7-1m-internal` | `/v1/messages`, `/chat/completions` |
| `claude-opus-4.7-high` | `/v1/messages`, `/chat/completions` |
| `claude-opus-4.7-xhigh` | `/v1/messages`, `/chat/completions` |

Observations:

- **No `claude-opus-4` or `claude-opus-4.1`** — those IDs only appear in older reference projects; they're retired on a 2026-05 Enterprise account
- **`-1m` variants** (1M context) take `/v1/messages` natively — **not** what copilot2api's test fixture suggested ("`-1m` is `/responses`-only" was synthetic test data)
- **`-high` / `-xhigh`** suffixes are likely reasoning-effort variants (Opus 4.7 special SKUs); verify against `capabilities.supports.reasoning_effort` when needed
- Other account plans (individual / business) likely have a narrower model list — don't treat this table as universal ground truth; **always fetch from `GET /models` live**

⚠ This list will drift as GitHub Copilot ships updates. Re-run `debug list-models` every few months.

### 7.1 Model capability matrix (known)

From `references/vscode-copilot-chat-snippets/anthropic.ts:148-206`:

| Capability | Supporting models |
| --- | --- |
| Context editing (`context_management`) | claude-haiku-4.5, claude-sonnet-4+, claude-opus-4+ |
| Interleaved thinking | claude-sonnet-4+, claude-haiku-4.5, claude-opus-4.5 |
| Tool search (`tool_search_tool_regex_20251119`) | claude-sonnet-4.5, claude-sonnet-4.6, claude-opus-4.5, claude-opus-4.6 |
| Memory tool | claude-haiku-4.5, claude-sonnet-4+, claude-opus-4+ |
| Adaptive thinking (`thinking.type:"adaptive"`) | check `capabilities.supports.adaptive_thinking` per model |

Variants with `1m` suffix (e.g., `claude-opus-4.6-1m`) take `/responses` per copilot2api fixtures — but live data on Enterprise shows they accept `/v1/messages` too; trust live data.

---

## 8. Edge cases and gotchas

### 8.1 Trailing assistant message
The Anthropic API treats a trailing assistant message as a prefill request (unsupported), returning 400. VS Code detects this and appends `{role:"user",content:[{type:"text",text:"Please continue."}]}`. We must do the same.

### 8.2 Thinking block signatures
Copilot/Anthropic require thinking blocks in assistant history to have a valid `signature` (received from a prior streaming response). Claude Code's forwarded signatures are valid, but `"Thinking..."` placeholders and signatures containing `'@'` are bad — drop them (they're Claude Code/opencode artifacts).

### 8.3 cache_control limit
Max 4 `cache_control:{type:"ephemeral"}` breakpoints. Anthropic's cache hierarchy: tools → system → messages. VS Code's approach: count existing breakpoints in messages+system, then add to the last tools entry and last system entry as long as slots remain.

In a passthrough scenario, Claude Code already places its own cache_control — **we usually don't need to add more**. But beware: Claude Code's cache_control on system blocks may include a `scope` field (Copilot doesn't accept it); strip it.

### 8.4 Tool name restrictions
Tool names starting with `mcp__` are MCP tools; they don't participate in Copilot's built-in tool search. `mcp__ide__executeCode` without `defer_loading` is dropped (matching VS Code's behavior).

### 8.5 `anthropic_beta` in body vs header
The Anthropic protocol accepts it in both places. Claude Code typically uses the header. We read the header, filter, and write the result to the upstream header. The body's `anthropic_beta` field (if present) is either ignored or filtered too.

### 8.6 The `[DONE]` terminator — must be filtered

Copilot's SSE stream's last line is `event: message data: [DONE]` — an OpenAI-style terminator. **Not in the Anthropic spec**; the [official streaming docs](https://platform.claude.com/docs/en/build-with-claude/streaming) explicitly say "The stream ends with a `message_stop` event," and all 4 complete examples end at `event: message_stop` / `data: {"type":"message_stop"}` — no `[DONE]`.

Two risks of not filtering:

1. **JSON parse errors**: `anthropic-sdk-typescript` JSON.parses each `data:` field aggressively (see [issue #292](https://github.com/anthropics/anthropic-sdk-typescript/issues/292) "Unexpected end of JSON input", [#346](https://github.com/anthropics/anthropic-sdk-typescript/issues/346) error reporting, [#14321](https://github.com/openclaw/openclaw/issues/14321) parse errors on control characters). The literal `[DONE]` is not valid JSON (unquoted `DONE`); the SDK will likely throw.
2. **Protocol compliance**: do the right thing at the gateway layer — fewer potential bug paths, don't lean on client-side tolerance.

Implementation: in the SSE forwarding loop, when `event=="message"` and `data=="[DONE]"`, `continue`.

### 8.7 Rate limit signals
Copilot returns `copilot-rate-limit-*` headers in responses. M1 doesn't actively manage rate limiting; just log them. Pass 429s through unchanged.

### 8.8 Vision trigger condition
When request messages contain `image` content blocks, add `copilot-vision-request: true` to the headers. Images nested inside `tool_result.content` count too (recurse).

### 8.9 Token counting
Claude Code calls `/v1/messages/count_tokens` to decide when to auto-compact. Copilot **doesn't have this endpoint**. Three options:

1. Implement a local estimate (gpt-family tokenizer + 1.15× multiplier)
2. Forward to upstream `https://api.anthropic.com/v1/messages/count_tokens` (requires the user to provide `ANTHROPIC_API_KEY`)
3. Always return a constant `1` (Claude Code thinks compaction is never needed)

`caozhiyuan/copilot-api` defaults to option 3, switches to option 2 when `ANTHROPIC_API_KEY` is configured. M1 recommendation: **return `1`** — simple, non-blocking. Improve later.

---

## 9. GitHub OAuth client ID for Copilot auth + model discovery

From `references/copilot-api-anthropic/src/lib/api-config.ts:313`:

```
GITHUB_CLIENT_ID = "Iv1.b507a08c87ecfe98"   // VS Code Copilot's official OAuth app
GITHUB_APP_SCOPES = "read:user"
```

Don't change. This client_id is the official one used by the VS Code Copilot extension; substitute it and the Copilot backend rejects you.

---

## 10. Should we live-probe?

Strongly recommended; verify these two things:

| What to verify | How |
| --- | --- |
| The current account's actual Claude model list returned by `GET /models` + each model's `supported_endpoints` | Add a temporary `copilot-bridge debug list-models` subcommand, dump raw JSON |
| What a simple `POST /v1/messages` actually responds with (especially the SSE event shape — does `copilot_annotations` etc. really appear?) | Add a temporary `copilot-bridge debug echo "<text>"` subcommand, send a test message, dump the entire stream |

Both debug subcommands **become the embryo of M1's CopilotTokenClient + CopilotClient** — not throwaway work.

If we skip the live probe, the worst cases are:
- The actual model list differs from our assumption (per-account variation), causing "no models available" on first Claude Code run
- The Copilot SSE stream contains undocumented fields that break Claude Code parsing

Both are moderate risks and recoverable (logging will surface them). So skipping is also fine — discover during M1 end-to-end.

I lean **probe**: it's cheap (~50 lines of C#) and eliminates the routing-layer's biggest unknown ahead of time.

---

## 11. Recommended M1 implementation order (replaces design.md §9)

```
1. CopilotTokenClient + AuthService.GetCopilotTokenAsync()
   - GET /copilot_internal/v2/token
   - In-memory cache + Timer(refresh_in - 60)
   Acceptance: copilot-bridge auth copilot-status prints the current token + expiry

2. CopilotClient.GetModelsAsync() + temporary debug list-models subcommand
   - GET /models
   - Filter to supported_endpoints containing /v1/messages
   Acceptance: lists the Claude models available on the account

3. (Optional live probe) temporary debug echo subcommand
   - POST /v1/messages with {model, messages:[{role:user,content:hello}], stream:true}
   - Dump SSE stream to stdout
   Acceptance: receives a real Anthropic-style event sequence

4. Preprocessing pipeline (8–10 pure functions, AOT-friendly)
   - Model name normalization, strip cache_control.scope, filter thinking,
     fix trailing assistant, sanitize IDE tools, merge tool_result+text,
     strip tool reference boundary, detect compact, warmup → small model,
     infer x-initiator
   Acceptance: unit tests for each rule pass

5. CopilotHeaderFactory (messages-proxy mode)
   - Full header set in "vscode_claude_code/2.x" mode
   Acceptance: drops copilot-integration-id, matches caozhiyuan's implementation

6. /v1/messages endpoint (Kestrel)
   - Receive Claude Code request, run preprocessing, call CopilotClient, forward SSE
   - Drop the [DONE] event
   Acceptance: curl non-streaming returns an Anthropic response
   Acceptance: curl streaming sees message_start ... message_stop

7. /v1/models endpoint
   - Convert §3.3 Copilot models to Anthropic-style
   - Only expose /v1/messages-supporting models
   Acceptance: returns a model picker JSON Claude Code can read at startup

8. /v1/messages/count_tokens endpoint
   - Simplified: always returns {"input_tokens": 1}
   Acceptance: Claude Code starts without a 404

9. End-to-end: Claude Code with ANTHROPIC_BASE_URL=http://localhost:<port> runs a conversation
   Acceptance: completes a multi-turn dialog with tool calls

10. (M2) accountType config + business/enterprise URL switching
11. (M2) HTML config page + browser-driven login flow
12. (M3) /chat/completions + /responses translation paths (other models)
```

Overall complexity is at least half of design.md §9's original — main reason: **no translation layer, no state machine**.

---

## 12. Content in design.md to be replaced

The following sections of `docs/design.md` are **outdated** and need updating (tracked as task #10):

| Section | Current state | Should become |
| --- | --- | --- |
| §1.3 M1 success criterion | "Translate to Copilot's OpenAI Chat Completions" | "Anthropic passthrough to Copilot `/v1/messages`" |
| §3.1 big picture | Translation as central | Translation as fallback; M1 doesn't draw it |
| §4.2 Translation pure functions | Streaming state machine central | "M1 main path has no translation; fallback (M3) needs the state machine" |
| §5.4 Model name normalization | Simple regex | Replace with §3.7's full 5-rule set |
| §5.5/5.6 Tool result ordering + streaming translation | M1 work | Move to M3 |
| §6 auth flow | Aligns with this research; keep | (Add Copilot token refresh details) |
| §9 milestone order | Translation + streaming state machine first | Replace with §11's 9 steps |
| §10 Kestrel vs HttpListener | Open question | M1 still uses Kestrel |
| Folder structure | `Translation/` central | `Preprocessing/` central; `Translation/` moves to M3 |

---

## 13. Source pointer index

| Protocol fact | Source |
| --- | --- |
| **All endpoint URL paths (official)** | `references/vscode-copilot-api-pkg/package/dist/index.js` (`DomainService` getters) |
| **All auto-injected headers (official)** | `references/vscode-copilot-api-pkg/package/dist/index.js` (`_mixinHeaders` function) |
| **RequestType → URL mapping (official)** | `references/vscode-copilot-api-pkg/package/dist/index.js` (`makeRequest` switch) |
| **Base URL resolution (official)** | `references/vscode-copilot-api-pkg/package/dist/index.js` (`_getCAPIUrl` method) |
| **`anthropic-beta` auto-generation (official)** | `references/vscode-copilot-chat-snippets/chatEndpoint.ts:182-215` |
| **Messages API enable conditions (official)** | `references/vscode-copilot-chat-snippets/chatEndpoint.ts:248-251` (`UseAnthropicMessagesApi` config + `supported_endpoints` includes `Messages`) |
| Copilot `/v1/messages` URL pattern (rev-eng) | `references/copilot-api-anthropic/src/lib/api-config.ts:156-173` |
| Copilot request headers (regular mode) | `references/copilot-api-anthropic/src/lib/api-config.ts:225-294` |
| Copilot request headers (messages-proxy mode) | `references/copilot-api-anthropic/src/lib/api-config.ts:175-192` |
| GitHub→Copilot token exchange + headers | `references/copilot-api-anthropic/src/lib/api-config.ts:297-310` + `references/copilot-api/src/services/github/get-copilot-token.ts` |
| supported_endpoints routing rule | `references/copilot-api-anthropic/src/routes/messages/handler.ts:133-150` |
| `x-initiator` inference logic | `references/copilot-api-anthropic/src/services/copilot/create-messages.ts:88-100` |
| anthropic-beta whitelist | `references/copilot-api-anthropic/src/services/copilot/create-messages.ts:26-32` |
| Preprocessing: cache_control.scope strip | `references/copilot-api-anthropic/src/routes/messages/preprocess.ts:489-502` |
| Preprocessing: thinking filter | `references/copilot-api-anthropic/src/routes/messages/preprocess.ts:506-522` |
| Preprocessing: IDE tools sanitize | `references/copilot-api-anthropic/src/routes/messages/preprocess.ts:456-477` |
| Preprocessing: tool_result + text merge | `references/copilot-api-anthropic/src/routes/messages/preprocess.ts:435-453` |
| Preprocessing: compact detection | `references/copilot-api-anthropic/src/routes/messages/preprocess.ts:82-115` |
| Trailing assistant guard | `references/vscode-copilot-chat-snippets/messagesApi.ts:198-224` |
| stop_reason mapping | `references/vscode-copilot-chat-snippets/messagesApi.ts:1006-1018` |
| Stream event shape (with extensions) | `references/vscode-copilot-chat-snippets/messagesApi.ts:54-98` |
| Stream passthrough implementation | `references/copilot-api-anthropic/src/routes/messages/api-flows.ts:306-340` |
| Model capabilities (context editing etc.) | `references/vscode-copilot-chat-snippets/anthropic.ts:148-206` |
| Tool search type constants | `references/vscode-copilot-chat-snippets/anthropic.ts:72-84` |
| ContentBlock → Anthropic conversion | `references/vscode-copilot-chat-snippets/messagesApi.ts:379-481` |
| GitHub OAuth client_id | `references/copilot-api-anthropic/src/lib/api-config.ts:313` |

---

## 14. One-line conclusion

**M1 = auth + header rewrite + ~10 pure-function preprocessors + body/SSE passthrough** — not a translator. With Copilot's native `/v1/messages` endpoint confirmed, design.md §9's "OpenAI translation first" path becomes an optional M3 fallback.

---

## 15. Empirical verification (playground tests)

`tests/CopilotBridge.Playground/` contains 9 test classes / 18 test cases, all passing against live Copilot `/v1/messages` (2026-05-06, Enterprise account, models: sonnet-4.6 / haiku-4.5 / opus-4.7-1m-internal). Each test class verifies one capability; together they're the wire-format ground truth the bridge passthrough layer must reproduce.

Run: `dotnet test` (~1m40s, single-threaded to dodge Anthropic's per-account rate limit).

### 15.1 Test matrix

| Test class | Cases | Verifies |
| --- | --- | --- |
| `EffortLevelsTests` | 4 | `thinking:{type:"adaptive"}` + `output_config:{effort:X}` × {low, medium, high, xhigh} on `claude-opus-4.7-1m-internal` are all accepted |
| `ExplicitThinkingTests` | 1 | `thinking:{type:"enabled", budget_tokens:2048}` produces a thinking content block on sonnet-4.6 |
| `PromptCachingTests` | 2 | `cache_control:{type:"ephemeral"}` default 5m TTL: a second identical request's `cache_read_input_tokens` matches the first request's `cache_creation_input_tokens` (verified on sonnet/haiku) |
| `ExtendedCacheTtlTests` | 1 | `cache_control:{type:"ephemeral", ttl:"1h"}` round-trips the same way; tokens land in the `usage.cache_creation.ephemeral_1h_input_tokens` bucket |
| `StreamingTests` | 3 | Standard Anthropic event sequence (`message_start` → blocks → `message_delta` → `message_stop`) + Copilot-appended non-protocol `[DONE]` event the bridge must filter |
| `ToolUseTests` | 2 | Full tool round-trip: tools[] definition → `stop_reason:"tool_use"` response → client appends `tool_result` → `stop_reason:"end_turn"` with the correct numerical answer (verified on sonnet/haiku) |
| `ParallelToolUseTests` | 1 | Multiple `tool_use` blocks in one assistant turn; multiple `tool_result` blocks in one user message; final text contains both tool results |
| `VisionTests` | 1 | base64 PNG image content block + `Copilot-Vision-Request: true` header — sonnet-4.6 processes and returns text |
| `ContextManagementTests` | 1 | `context_management.edits` field + `anthropic-beta: context-management-2025-06-27` header are accepted (doesn't verify `applied_edits` triggering — would need >100k input tokens of history) |
| `MaxTokensTests` | 2 | When request hits `max_tokens`, `stop_reason="max_tokens"` and `output_tokens` is close to the ceiling (verified on sonnet/haiku) |
| `RawUsageShapeTests` | 2 | Top-level `usage` in non-streaming responses matches `message_start.message.usage` exactly (5 keys: nested `cache_creation`, two flat cache counters, `input_tokens`, `output_tokens`); `message_delta.usage` carries the same four scalars but drops nested `cache_creation` |
| `CopilotGapProbes` | 4 | `POST /v1/messages/count_tokens` is supported upstream (HTTP 200, real `{input_tokens:N}`); `tools[].type:"web_search_20250305"` → HTTP 400 `unsupported_value` (Claude Code's `WebSearch` won't work over Copilot); `GET /v1/files` → HTTP 404 plain text; `POST /v1/messages/batches` → HTTP 404 plain text. The plain-text 404s confirm Copilot's gateway only exposes routes it actually implements. |

### 15.2 Cross-test invariants

These facts are visible across every test; the bridge implementation must accept them as "input shapes we tolerate":

#### 15.2.1 Model ID gets normalized in responses

Whatever model id the request used (dotted, with suffix), the response's `model` field comes back rewritten to "dash + no suffix":

| Request model | Response model |
| --- | --- |
| `claude-sonnet-4.6` | `claude-sonnet-4-6` |
| `claude-haiku-4.5` | `claude-haiku-4-5` |
| `claude-opus-4.7-1m-internal` | `claude-opus-4-7` |

**Bridge implication**: passing the response through to Claude Code shows the dash form in the `model` field. Claude Code itself doesn't use this for model selection (it uses the request-time model id), so passthrough is safe — but the displayed string differs from what was selected. Expected behavior.

#### 15.2.2 Bedrock backend leaks fields

Copilot's backend is AWS Bedrock; every response carries Bedrock-specific fields not in the Anthropic protocol:

```jsonc
// In the message_stop event's data
{
  "amazon-bedrock-invocationMetrics": {
    "firstByteLatency": 981,
    "invocationLatency": 1432,
    "inputTokenCount": 26,
    "outputTokenCount": 37
  },
  "type": "message_stop"
}

// In usage (non-streaming response + message_start events).
// message_delta carries the same scalars but DROPS the nested cache_creation
// — that's per Anthropic spec (BetaMessageDeltaUsage has no cache_creation field),
// not Copilot dropping data. See §4.2.
"usage": {
  "cache_creation": {
    "ephemeral_5m_input_tokens": 0,
    "ephemeral_1h_input_tokens": 5303
  },
  "cache_creation_input_tokens": 5303,
  "cache_read_input_tokens": 0,
  "input_tokens": 14,
  "output_tokens": 4
}
```

The Anthropic protocol's `cache_creation` is flat (just `cache_creation_input_tokens`), but Copilot returns both the nested object and the flat scalar on non-streaming responses and `message_start`. **The bridge passes both through** — the Anthropic SDK ignores the nested `cache_creation` (unknown field), no compatibility issue.

#### 15.2.3 Copilot's own extension fields

| Field | Where | Meaning |
| --- | --- | --- |
| `stop_details: null` | non-streaming response root | Not in Anthropic protocol; Copilot-added (placeholder for future stop-reason details) |
| `copilot_usage: {token_details, total_nano_aiu}` | `message_delta` event data | Copilot's quota ledger ("nano AIU" is a fine-grained billing unit) |
| `copilot_annotations.IPCodeCitations` | `content_block_delta` event (rare; only when public-repo code is involved) | Public-repo code citation warnings |

#### 15.2.4 Stream termination: `[DONE]` is a Copilot addition

Copilot emits an `event: message  data: [DONE]` after `message_stop` — OpenAI-style terminator. Not in the Anthropic spec (see [§8.6](#86-the-done-terminator--must-be-filtered)). Bridge must filter.

### 15.3 Model-specific behavior (capability/reality mismatches)

The `capabilities` field returned by `/models` is not always consistent with actual API behavior. These are hard facts found by the playground:

| Model | capabilities claim | Actual behavior |
| --- | --- | --- |
| `claude-haiku-4.5` | `supports.adaptive_thinking: true` | Request with `thinking:{type:"adaptive"}` returns **HTTP 400** "adaptive thinking is not supported on this model". **Only supports `thinking:{type:"enabled", budget_tokens:N}`** |
| `claude-opus-4.7-1m-internal` | `supports.adaptive_thinking: true`, `thinking_budget=1024..32000` | Request with `thinking:{type:"enabled", budget_tokens:4096}` returns **HTTP 400**. **Only supports `thinking:{type:"adaptive"}` + `output_config:{effort:X}`** |
| `claude-opus-4.7` (vanilla) | `supports.reasoning_effort=[medium]` | Single-effort lock — `effort` must be `medium` |
| `claude-opus-4.7-high` | `supports.reasoning_effort=[high]` | Single-effort lock — `effort` must be `high` |
| `claude-opus-4.7-xhigh` | `supports.reasoning_effort=[xhigh]` | Single-effort lock — `effort` must be `xhigh` |

**Bridge implication**: don't passthrough Claude Code's `thinking` field unmodified. **The preprocessing pipeline must rewrite the `thinking` shape based on the target model**:

- haiku-* → force `thinking:{type:"enabled", budget_tokens:...}`, drop adaptive
- opus-4.7* → force `thinking:{type:"adaptive"}` + `output_config:{effort:...}`, drop explicit budget
- sonnet-* → both shapes work, pass Claude Code's input through unchanged

`caozhiyuan/copilot-api`'s `preprocess.ts:524-561` has similar logic (checking `selectedModel.capabilities.supports.adaptive_thinking` and rewriting), but it **trusts the capabilities field** — which, per this section, is **wrong for haiku**. Our preprocessor needs hard-coded family rules (haiku → explicit, opus-4.7 → adaptive), not capability-flag-based.

### 15.4 Other empirical edge cases

#### Image minimum size

`/v1/messages` enforces a minimum image size (threshold not documented):
- ❌ 2×2 PNG → HTTP 400 `{"message":"Could not process image"}`
- ✅ 100×100 PNG → accepted

Anthropic's docs suggest ≥200px but state no hard minimum. Copilot's threshold is somewhere between. Bridge doesn't need to validate sizes — pass through and let Copilot reject.

#### Web search server tool — NOT supported (2026-05-21)

`tools: [{ type: "web_search_20250305", name: "web_search", max_uses: 1 }]` returns:

```json
HTTP 400
{ "error": { "message": "The use of the web search tool is not supported.", "code": "unsupported_value" } }
```

This is Anthropic's server-side web search (`web_search_20250305`, `web_search_20260209`). The official VS Code Copilot Chat client gates this behind an experiment flag (`references/vscode-copilot-chat-snippets/anthropicProvider.ts:201-211`) — i.e. even VS Code doesn't send it on Copilot by default. Claude Code's `WebSearch` built-in tool is implemented via this exact server tool, so **Claude Code's WebSearch will not work over Copilot.**

**Bridge implications**:
- Letting the 400 bubble through is correct behavior — silently stripping the tool would make WebSearch fail with confusing "tool unknown" errors instead of a clean "not supported."
- A future UX improvement could be to detect `web_search_*` in inbound `tools[]` and return a friendly bridge-level 400 explaining the gap (saves a Copilot round-trip), but the upstream message is already clear.

#### `/v1/messages/count_tokens` — IS supported (2026-05-21)

Contrary to circumstantial evidence from reference impls (which all estimate locally or proxy to Anthropic), Copilot does expose Anthropic's token-counting endpoint:

```json
HTTP 200
{ "input_tokens": 8 }
```

Verified by `CopilotGapProbes.CountTokens_ProbeCopilotUpstream` on a minimal `claude-sonnet-4.6` payload. The bridge's `ClaudeCodeCountTokensEndpoint` (`src/CopilotBridge.Cli/Endpoints/ClaudeCode/ClaudeCodeCountTokensEndpoint.cs:31`) currently returns a hardcoded `{input_tokens:1}` stub — this can be replaced with a passthrough to get real counts. The wire format matches Anthropic's spec exactly, so passthrough is a one-line swap.

#### Cache value non-determinism

The same cache prefix read twice in succession can show **different `cache_read_input_tokens` values** (observed: 5310 vs 5319, a 9-token delta). Likely Bedrock's internal cache compaction or slicing.

**Bridge implication**: don't assert "two responses' cache_read must be equal" — false positives.

#### Tool definition format

The Anthropic Messages API tool schema:

```jsonc
{
  "name": "add_numbers",
  "description": "Add two integers and return their sum.",
  "input_schema": {
    "type": "object",
    "properties": {"a":{"type":"integer"},"b":{"type":"integer"}},
    "required": ["a", "b"]
  }
}
```

Copilot accepts this shape directly (verified by playground tests). No schema rewriting needed in preprocessing — unlike the OpenAI translation path (which requires a `{"type":"function","function":{...}}` wrapper).

#### `anthropic-beta` header acceptance — empirical findings

Tested by `BetaAcceptanceTests` (2026-05-06) against a minimal `claude-sonnet-4.6`
request. **Every value tested returned HTTP 200 — Copilot silently ignores
unknown betas at the header level**, contradicting the earlier guess that
unknown betas get rejected:

| Beta | Status | Notes |
| --- | --- | --- |
| `interleaved-thinking-2025-05-14` | 200 | from `chatEndpoint.ts` whitelist |
| `context-management-2025-06-27` | 200 | from whitelist; pairs with `context_management` field |
| `advanced-tool-use-2025-11-20` | 200 | from whitelist; VS Code's default-on |
| `claude-code-20250219` | 200 | sent by Claude Code 2.1.131; effect unknown |
| `prompt-caching-scope-2026-01-05` | 200 | sent by Claude Code; enables the `cache_control.scope` body field which Copilot **does** reject — so the header is accepted but the feature it gates is not |
| `bogus-nonexistent-beta-99999999` | 200 | confirms Copilot does not validate beta names at all |

**Bridge implication**: filtering the inbound `anthropic-beta` header is **not**
about rejection-prevention (Copilot doesn't reject unknowns). It's about
**matching the official VS Code Copilot Chat client's behavior** so quirks are
easier to triage — if a request misbehaves, we want to be sending the same
header set the upstream is tested against. The real body-field defense (e.g.
stripping `system[*].cache_control.scope`, see §3.6 rule 1) is separate.

The official client (`chatEndpoint.ts:182-215`) does **not** send all 3 betas
unconditionally — each is gated:

```typescript
if (!this.supportsAdaptiveThinking) {                  // older models only
    betaFeatures.push('interleaved-thinking-2025-05-14');
}
if (isAnthropicContextEditingEnabled(this.model, ...)) { // config-gated
    betaFeatures.push('context-management-2025-06-27');
}
if (isAnthropicToolSearchEnabled(this.model, ...)) {     // config-gated
    betaFeatures.push('advanced-tool-use-2025-11-20');
}
```

In other words: M1's `CopilotHeaderFactory` (step 5) should **generate** the
`anthropic-beta` header based on model capability + request shape, not just
passthrough or whitelist-filter Claude Code's. Suggested generation rules for
the bridge:

- `interleaved-thinking-2025-05-14` ← target-model is in `{haiku-*}` (which per
  §15.3 require explicit thinking and not adaptive)
- `context-management-2025-06-27` ← request body has `context_management` field
- `advanced-tool-use-2025-11-20` ← request body has any `tools[].type`
  beginning with `tool_search_tool_*`, **or** (TBD) any `tools[].defer_loading`

The official client also emits an `X-Model-Provider-Preference` header from a
team-internal config flag — not relevant to our usage but noted for fidelity.

#### `max_tokens` truncation behavior

A request with `max_tokens: 16` for a task that can't be completed in 16 tokens results in:
- `stop_reason: "max_tokens"` ✓
- `output_tokens: ~17` (slight overshoot due to tokenizer boundary alignment)

Bridge passthrough is sufficient. Claude Code's `messagesApi.ts:1006-1018` maps `max_tokens` to `FinishedCompletionReason.Length`.

### 15.5 Bridge passthrough strategy — full version

Combining all the empirical findings, the bridge's job at `POST /v1/messages` is:

```
inbound (Claude Code → bridge)
  ↓
1. Drop the Authorization header (Claude Code sends a dummy)
2. Keep anthropic-version unchanged (default 2023-06-01)
3. Filter the inbound anthropic-beta header to the whitelist (the 3 in §15.4)
4. Parse the body and run the preprocessing pipeline:
   a. Model name normalization  (claude-sonnet-4-XXX → claude-sonnet-4.6 etc.)
   b. Rewrite the thinking field by target-model family (the hard rules in §15.3)
   c. Strip system[*].cache_control.scope (Copilot rejects)
   d. Filter assistant thinking blocks (keep only those with a signature)
   e. Fix trailing assistant message
   f. Sanitize IDE tools (mcp__ide__executeCode etc.)
   g. Merge tool_result + adjacent text
   h. Strip "Tool loaded." boundary
   i. Detect compact requests (affects x-initiator)
   j. Infer x-initiator (user / agent)
  ↓
5. Call AuthService.GetCopilotTokenAsync() for the Copilot bearer
6. Inject §3.0.4's 7 official headers + Authorization + anthropic-beta (filtered)
7. If body contains image content → add Copilot-Vision-Request: true
  ↓
upstream (bridge → Copilot)
  ↓
Copilot streams the response back
  ↓
8. SSE passthrough:
   a. Standard Anthropic events (message_start, content_block_*, message_delta, message_stop) — forward unchanged
   b. data:[DONE] event → drop
   c. Copilot extension fields (stop_details, nested cache_creation, copilot_usage, amazon-bedrock-invocationMetrics) — preserve verbatim (Anthropic SDK tolerates unknown fields)
  ↓
outbound (bridge → Claude Code)
```

**13 steps, 4 I/O boundaries, ~250 lines of C#**. Plus `/v1/models` (mirror Copilot's `/models`, filter to `/v1/messages`-supporting) and `/v1/messages/count_tokens` (M1 returns `{"input_tokens":1}`), and that's the complete bridge endpoint.

The playground tests are the "known-good terminal state" of this passthrough chain — once the bridge is built, the first thing to do is run all playground tests through the bridge and confirm they still pass. That's the regression gate.

---

## 16. Claude Code's API surface (verified against `claude-code-2.1.88`)

Decompiled source at `Q:/MyProjects/claude-code-sourcemap/restored-src/`. File-line citations below refer to that path. The aim of this section is to bound the bridge's surface area: knowing exactly what Claude Code calls means knowing exactly what the bridge needs to handle — and what is dead code.

### 16.1 What Claude Code calls via `ANTHROPIC_BASE_URL`

The Anthropic SDK client is constructed in `src/services/api/client.ts` and uses whatever `ANTHROPIC_BASE_URL` resolves to. Only three SDK methods are called from Claude Code's own code (verified by grep on `anthropic\.(beta\.)?(messages|models|completions|files|skills)\.`):

| SDK call | Endpoint | Site(s) | Bridge status |
| --- | --- | --- | --- |
| `anthropic.beta.messages.create(...)` (streaming + non-streaming) | `POST /v1/messages` | `src/services/api/claude.ts:555` (API verification), `:864` (non-stream fallback), `:727` (streaming via `queryModel()`), `src/services/tokenEstimation.ts:302` (token estimation probe) | ✅ implemented |
| `anthropic.beta.messages.countTokens(...)` | `POST /v1/messages/count_tokens` | `src/services/tokenEstimation.ts:172` | ⚠️ stub — Copilot supports it, bridge returns `{input_tokens:1}`. See [§15.4 count_tokens probe](#-v1messagescount_tokens--is-supported-2026-05-21) |
| `anthropic.models.list({ betas })` | `GET /v1/models` | `src/utils/model/modelCapabilities.ts:93` | ✅ implemented, **but never hit in practice** — see §16.2 |

**Plus one non-SDK path that *also* uses `ANTHROPIC_BASE_URL`**: `src/services/api/filesApi.ts:32-37` uses raw axios with `process.env.ANTHROPIC_BASE_URL ?? process.env.CLAUDE_CODE_API_BASE_URL ?? 'https://api.anthropic.com'`. Endpoints hit:

| Method + path | Site | Trigger | Bridge status |
| --- | --- | --- | --- |
| `GET /v1/files/{id}/content` | `filesApi.ts:137` | File attachment download at session startup (BriefTool / teleport) | ❌ not implemented |
| `POST /v1/files` | `filesApi.ts:385` | Upload — `src/tools/BriefTool/upload.ts:248` (BriefTool), `src/utils/teleport/gitBundle.ts` (teleport git bundle) | ❌ not implemented |
| `GET /v1/files` | `filesApi.ts:646` | File listing | ❌ not implemented |

### 16.2 Gating: `/v1/models` is dead code on the bridge

`src/utils/model/modelCapabilities.ts:46-51` gates the entire `refreshModelCapabilities()` call behind three checks:

```ts
function isModelCapabilitiesEligible(): boolean {
  if (process.env.USER_TYPE !== 'ant') return false
  if (getAPIProvider() !== 'firstParty') return false
  if (!isFirstPartyAnthropicBaseUrl()) return false
  return true
}
```

A Claude Code instance pointed at the bridge fails the `isFirstPartyAnthropicBaseUrl()` check (and almost always the other two). **`anthropic.models.list()` is never called when Claude Code's base URL is custom.** The bridge's `GET /cc/v1/models` endpoint exists but won't be exercised by this flow.

Implication: don't invest time hardening `/cc/v1/models`. Either remove it once we're sure no other tool hits it, or keep it as a passive convenience for `curl`-based debugging.

### 16.3 SDK endpoints Claude Code *doesn't* call (not gaps)

Grep on `anthropic\.beta\.(skills|messages\.batches)|\.completions\.create|anthropic\.files\.` against `src/` returns zero matches:

- `POST /v1/messages/batches` (and the rest of the Batches API) — not used; probed at HTTP 404 plain text on Copilot anyway
- `/v1/skills/*` — not used (it's a beta SDK surface; Claude Code's `skills` feature is local, not server-side)
- `POST /v1/complete` — legacy completions API, not used
- `/v1/files/*` via the SDK — Claude Code uses raw axios instead (see §16.1)

These are SDK surface area, not Claude Code surface area. No bridge work needed.

### 16.4 Claude Code endpoints that *don't* go through `ANTHROPIC_BASE_URL`

Anything below uses a different base URL constant. The bridge sees none of these — they hit Anthropic's claude.ai / console / MCP / telemetry endpoints directly. Listed here so future "we saw Claude Code call X" reports can be ruled out fast.

| Endpoint | Base URL source | File:line |
| --- | --- | --- |
| `POST /v1/oauth/token` | `getOauthConfig().TOKEN_URL` (claude.ai or platform.claude.com) | `src/constants/oauth.ts:91, 127, 163, 214` |
| `/v1/mcp/*`, `/v1/toolbox/shttp/mcp/*` | `getOauthConfig().MCP_PROXY_URL` (`mcp-proxy.anthropic.com`) | `src/constants/oauth.ts:102-103, 141, 172` |
| `/v1/code/sessions/*` | claude.ai | `src/constants/product.ts:57` |
| `/v1/sessions`, `/v1/session_ingress/*`, `/v1/environment_providers/*` | teleport base URL (claude.ai cloud) | `src/utils/teleport.tsx:556, 631, 822, 1096`; `src/utils/teleport/api.ts:201, 209, 294, 369, 432`; `src/utils/teleport/environments.ts:45, 88` |
| `POST /v1/traces`, `POST /v1/logs` | OTel exporter URL | `src/utils/telemetry/instrumentation.ts:367, 371` |

### 16.5 Bridge gap summary

| Surface | Bridge action | Reasoning |
| --- | --- | --- |
| `POST /v1/messages` | ✅ implemented (passthrough) | Hot path; verified by full playground suite |
| `POST /v1/messages/count_tokens` | ✅ implemented (passthrough, 2026-05-21) | Copilot returns real counts ([§15.4 probe](#-v1messagescount_tokens--is-supported-2026-05-21)); bridge forwards body raw via `ICopilotClient.PostCountTokensAsync`. Old `{input_tokens:1}` stub removed |
| `GET /v1/models` | 🪦 dead code under bridge flow | Gated by `isFirstPartyAnthropicBaseUrl()`; never hit. Can remove |
| `GET /v1/files/{id}/content`, `POST /v1/files`, `GET /v1/files` | ❌ not implemented; consider friendly 404 | Copilot returns plain-text 404; Claude Code's axios path will fail at the parse step. If we want clean UX when BriefTool / teleport is triggered, return `{"error": {"type": "not_supported", "message": "Files API is not supported on the Copilot backend."}}` from the bridge instead of forwarding |
| `web_search_20250305` server tool in `tools[]` | ❌ bridge passthrough returns Copilot's clear 400 | Acceptable as-is; future UX nicety is to detect and short-circuit at the bridge (see [§15.4 web search probe](#web-search-server-tool--not-supported-2026-05-21)) |
| Anthropic Batches / Skills / Completions / Admin Usage / claude.ai-side endpoints | n/a | Either not called by Claude Code (§16.3) or doesn't route via `ANTHROPIC_BASE_URL` (§16.4) |

**Net** (post 2026-05-21): `count_tokens` now passthrough — see §16.5 row. Remaining: one cleanup option (remove dead `/cc/v1/models`), one optional polish (bridge-side 404 for `/v1/files/*` with a helpful body). The bridge's coverage of what Claude Code actually sends is otherwise complete.

### 16.6 How Claude Code emits `output_config.effort` (verified 2026-05-21)

The bridge's effort-routing design ([§15.3 capability mismatches](#-153-model-specific-behavior-capabilityreality-mismatches), `src/CopilotBridge.Cli/Pipeline/Routing/CopilotModelRegistry.cs:50-60`) handles `model + effort` correctly, but **Claude Code rarely sends effort in the shape the bridge expects.** Tracing `restored-src/src/utils/effort.ts` + `services/api/claude.ts:440-466`:

**Vocabulary** (`effort.ts:13-18`): `EFFORT_LEVELS = ['low', 'medium', 'high', 'max']`. **No `xhigh`.** `parseEffortValue('xhigh')` returns undefined; `convertEffortValueToLevel` coerces unknown strings to `'high'`. The CLI `/effort` command, `--effort` flag, env `CLAUDE_CODE_EFFORT_LEVEL`, and settings.json `effortLevel` all funnel through these 4 values.

**Per-model gate** (`effort.ts:23-49`, `modelSupportsEffort()`):
```ts
if (m.includes('opus-4-6') || m.includes('sonnet-4-6')) return true
if (m.includes('haiku') || m.includes('sonnet') || m.includes('opus')) return false
// default-true only for 1P (api.anthropic.com)
```
**Opus 4.7 falls into the second branch and returns false** — Claude Code refuses to set `output_config.effort` on opus-4.7 requests (`claude.ts:447` early-returns from `configureEffortParams`). Override: `process.env.CLAUDE_CODE_ALWAYS_ENABLE_EFFORT=1`.

**Max clamp** (`effort.ts:53-65, 162-165`): `modelSupportsMaxEffort()` is `true` only for `opus-4-6`. Any other model's `max` gets downgraded to `high` before being sent. So even with `ALWAYS_ENABLE_EFFORT=1`, you can't get `max` on the wire for opus-4.7.

#### What the bridge actually sees, by user path

| User does in Claude Code | Wire to bridge | Bridge → Copilot |
| --- | --- | --- |
| Default usage of opus-4.7 (any effort UI selection) | `model: claude-opus-4-7-...`, **no `effort` field** | normalize → `claude-opus-4.7`, `ApplyEffortRouting` returns `(model, false)` (no effort to strip), Copilot uses model's default = medium |
| `CLAUDE_CODE_ALWAYS_ENABLE_EFFORT=1` + selects `max` | `model: opus-4-7-...`, `effort: "high"` (clamped) | EffortAware: high → `-high` variant, effort stripped. Final: `claude-opus-4.7-high` |
| Sets `ANTHROPIC_MODEL=claude-opus-4.7-xhigh` directly | `model: claude-opus-4-7-xhigh`, no `effort` | normalize keeps `-xhigh`, not in EffortAware table, default behavior keeps model. Final: `claude-opus-4.7-xhigh` |
| SDK `extraBodyParams.output_config.effort = "xhigh"` (only reachable via the SDK, not Claude Code UI) | `model: opus-4-7-...`, `effort: "xhigh"` | EffortAware: xhigh → `-xhigh` variant, effort stripped. Final: `claude-opus-4.7-xhigh` |

**Practical conclusion**: for a user who wants opus-4.7 xhigh, the supported path is **`ANTHROPIC_MODEL=claude-opus-4.7-xhigh`** — direct variant selection. The "base + effort" path is gated off by `modelSupportsEffort` returning false. The bridge's `EffortAware["claude-opus-4.7"]` table is correct but exercised mainly by the env-var bypass or SDK callers, not Claude Code's normal UI.

**Two follow-ups worth tracking**:
- `claude-sonnet-4.6` (passes `modelSupportsEffort` via the `sonnet-4-6` branch) sends `output_config.effort` from Claude Code's UI by default. Our bridge currently strips this field for sonnet (no entry in `EffortAware`). If Copilot's `claude-sonnet-4.6` accepts effort directly (different from opus-4.7's variant-suffix model), stripping is wasteful and worth re-probing.
- Same question for `claude-opus-4-6` once / if Copilot adds it.
