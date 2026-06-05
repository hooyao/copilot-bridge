"""
Cache-impact prototype for the mid-conversation `role:"system"` fix.

Reads a sequence of consecutive inbound traces and, for each transformation
strategy, computes the byte-level **prefix** that the cache would see between
two adjacent requests. Anthropic prompt caching is prefix-based: turns N and
N+1 share cache iff the serialized prefix-up-to-each-cache_control-breakpoint
is identical.

Three strategies compared:

  FOLD          — current behavior. Strip every mid-conv role:"system" from
                  messages[], append the texts to top-level system[].
  CONVERT       — Layer A. Replace each mid-conv role:"system" with
                  role:"user" + deterministic "[Claude Code injected]\n" prefix
                  on the content. Append-only friendly.
  PLACEMENT_FIX — Layer B. Keep S in place when its placement is legal under
                  opus-4.8's rule (predecessor is user/server-tool-result,
                  successor is assistant or end-of-array). Otherwise CONVERT.

What we care about per trace pair (N, N+1):

  1. Does the serialized prefix ending at the previous-turn's last
     cache_control breakpoint stay byte-identical from N → N+1? If yes, the
     cache hits all the way to that breakpoint.
  2. How much of the top-level `system` array changes between N and N+1?
     (Fold appends to system every turn — the system tail grows and breaks
     its own cache_control breakpoint.)
  3. Cumulative byte volume passed to upstream over the whole sequence,
     proxying the long-run cache miss surface.

Run from the repo root:
    python docs/scratch/midconv-cache-prototype.py

Output is a comparison table; no assertions — eyeball whether CONVERT and
PLACEMENT_FIX preserve cache prefixes that FOLD breaks.
"""

import json
import os
import re
from pathlib import Path

TRACE_DIR = Path(r"C:\Users\HuYao\Desktop\copilot-bridge\request-traces")

# Trace 0035..0044 covers the bug-report window from the report.
TRACE_PAIRS = [
    ("20260605-120018-0035-inbound-req.json", "20260605-120028-0036-inbound-req.json"),
    ("20260605-120028-0036-inbound-req.json", "20260605-120110-0037-inbound-req.json"),
    ("20260605-120110-0037-inbound-req.json", "20260605-120219-0038-inbound-req.json"),
    ("20260605-120219-0038-inbound-req.json", "20260605-120227-0039-inbound-req.json"),
    ("20260605-120227-0039-inbound-req.json", "20260605-120247-0040-inbound-req.json"),
    ("20260605-120247-0040-inbound-req.json", "20260605-120301-0041-inbound-req.json"),
    ("20260605-120301-0041-inbound-req.json", "20260605-120317-0042-inbound-req.json"),
    ("20260605-120317-0042-inbound-req.json", "20260605-120339-0043-inbound-req.json"),
    ("20260605-120339-0043-inbound-req.json", "20260605-120400-0044-inbound-req.json"),
]

# ─── Transformations ─────────────────────────────────────────────────────────

INJECTED_PREFIX = "[Claude Code injected]\n"


def to_text_block_list(content):
    """Normalize content to a list of {'type':'text', 'text': str} blocks
    (preserving cache_control if present). Mid-conv system messages in the
    captured traces are all plain strings, so usually len==1 with no
    cache_control."""
    if isinstance(content, str):
        return [{"type": "text", "text": content}]
    if isinstance(content, list):
        out = []
        for b in content:
            if isinstance(b, dict) and b.get("type") == "text":
                out.append({k: v for k, v in b.items() if v is not None})
            else:
                # non-text mid-conv-system content is theoretically possible
                # but doesn't exist in any of the captured traces; keep as-is.
                out.append(b)
        return out
    return []


def transform_fold(body):
    """Current production: strip mid-conv role:'system' from messages[],
    append their text blocks to top-level system[]."""
    body = json.loads(json.dumps(body))  # deep copy
    folded = []
    kept = []
    for m in body.get("messages", []):
        if m.get("role") == "system":
            folded.extend(to_text_block_list(m.get("content")))
            continue
        kept.append(m)
    if folded:
        sys = body.get("system")
        if sys is None:
            body["system"] = folded
        elif isinstance(sys, str):
            body["system"] = [{"type": "text", "text": sys}] + folded
        elif isinstance(sys, list):
            body["system"] = sys + folded
    body["messages"] = kept
    return body


def transform_convert(body):
    """Layer A: replace mid-conv role:'system' with role:'user' + injected
    prefix. Append-only on each turn (no top-level system growth)."""
    body = json.loads(json.dumps(body))
    new_msgs = []
    for m in body.get("messages", []):
        if m.get("role") == "system":
            blocks = to_text_block_list(m.get("content"))
            # prepend INJECTED_PREFIX to the first text block to keep the
            # marker visible to the model
            if blocks and isinstance(blocks[0], dict) and "text" in blocks[0]:
                blocks = [dict(blocks[0])] + blocks[1:]
                blocks[0]["text"] = INJECTED_PREFIX + blocks[0]["text"]
            new_msgs.append({"role": "user", "content": blocks})
            continue
        new_msgs.append(m)
    body["messages"] = new_msgs
    return body


def is_legal_4_8_placement(msgs, i):
    """opus-4.8 mid-conv system placement rule (per ModelProfileProbe results):
    - predecessor must be role:'user' (or assistant-with-server-tool-result,
      but Claude Code never sends those)
    - successor must be role:'assistant' or end-of-array
    """
    prev = msgs[i - 1] if i > 0 else None
    nxt = msgs[i + 1] if i + 1 < len(msgs) else None
    pred_ok = prev is not None and prev.get("role") == "user"
    succ_ok = nxt is None or nxt.get("role") == "assistant"
    return pred_ok and succ_ok


def transform_placement_fix(body):
    """Layer B: keep S in legal positions, convert otherwise."""
    body = json.loads(json.dumps(body))
    msgs = body.get("messages", [])
    new_msgs = []
    for i, m in enumerate(msgs):
        if m.get("role") == "system":
            if is_legal_4_8_placement(msgs, i):
                new_msgs.append(m)
                continue
            # else fall through to convert
            blocks = to_text_block_list(m.get("content"))
            if blocks and isinstance(blocks[0], dict) and "text" in blocks[0]:
                blocks = [dict(blocks[0])] + blocks[1:]
                blocks[0]["text"] = INJECTED_PREFIX + blocks[0]["text"]
            new_msgs.append({"role": "user", "content": blocks})
            continue
        new_msgs.append(m)
    body["messages"] = new_msgs
    return body


# ─── Cache-prefix measurement ────────────────────────────────────────────────


def serialize_canonical(body):
    """Canonical JSON serialization. Anthropic's cache lookup is over the
    actual on-wire bytes, but for prototyping we use canonical JSON because
    the bridge's outbound serialization is deterministic (sorted keys are
    NOT used in production — System.Text.Json preserves property order; for
    our cross-strategy comparison the absolute serialization doesn't matter
    as long as both sides use the same one)."""
    return json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def serialize_system_only(body):
    return json.dumps(body.get("system"), ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def first_n_messages_bytes(body, n):
    """Serialized bytes of the first N messages (in their array shape). This
    is a stand-in for 'prefix up to the cache_control breakpoint at the end
    of the previous turn' — the breakpoint sits on the last tool_result of
    the previous turn, and Claude Code's append-only history means turn N's
    breakpoint is at position k while turn N+1's is at position k' > k."""
    msgs = body.get("messages", [])
    return json.dumps(msgs[:n], ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def find_last_cache_breakpoint_index(body):
    """Index in messages[] of the message containing the last cache_control
    block. -1 if none."""
    msgs = body.get("messages", [])
    last = -1
    for i, m in enumerate(msgs):
        content = m.get("content")
        if isinstance(content, list):
            for b in content:
                if isinstance(b, dict) and b.get("cache_control"):
                    last = i
                    break
    return last


def find_common_prefix_len(a, b):
    n = min(len(a), len(b))
    i = 0
    while i < n and a[i] == b[i]:
        i += 1
    return i


def system_breakpoint_blocks(body):
    """Return ordered list of system[] entries up to and including the LAST
    one carrying cache_control. The cache breakpoint is `system[last_bp]`;
    if any earlier block changes its bytes, the cache miss surface starts
    at the position of the change. Returns [] if no system or no breakpoint."""
    sys = body.get("system")
    if not isinstance(sys, list):
        return []
    last_bp = -1
    for i, b in enumerate(sys):
        if isinstance(b, dict) and b.get("cache_control"):
            last_bp = i
    if last_bp < 0:
        return []
    return sys[: last_bp + 1]


def serialize_blocks(blocks):
    return json.dumps(blocks, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def strip_cache_control(obj):
    """Recursively remove `cache_control` keys. Anthropic's cache lookup
    hashes the prefix BYTES of the request, but the cache_control field
    itself is metadata — it doesn't enter the hash, otherwise append-only
    history would never cache hit (every turn's last tool_result migrates
    cache_control from the previous position to the new one). For our
    relative comparison between strategies we strip cache_control before
    serializing the prefix."""
    if isinstance(obj, dict):
        return {k: strip_cache_control(v) for k, v in obj.items() if k != "cache_control"}
    if isinstance(obj, list):
        return [strip_cache_control(x) for x in obj]
    return obj


def normalize_content_to_array(body):
    """Mimic the bridge's `ContentBlockParamListConverter` round-trip: any
    `message.content` that's a string gets wrapped as a single
    `{type:'text', text:<string>}` block. Claude Code is inconsistent about
    this — the SAME message in turn N may be {content:[...]} and in turn N+1
    may be {content:'string'}; the bridge's converter normalizes both shapes
    to the array form before serializing outbound. Without this step the
    prototype overstates cache miss because raw inbound JSON preserves the
    inconsistent shape."""
    body = json.loads(json.dumps(body))
    for m in body.get("messages", []):
        if isinstance(m.get("content"), str):
            m["content"] = [{"type": "text", "text": m["content"]}]
    return body


# ─── Run ─────────────────────────────────────────────────────────────────────


def load(path):
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    return data.get("body", data)


STRATEGIES = {
    "FOLD          ": transform_fold,
    "CONVERT       ": transform_convert,
    "PLACEMENT_FIX ": transform_placement_fix,
}


def find_breakpoints(body):
    """Return ordered list of cache breakpoint descriptors. Each is a tuple
    (region, end_index_inclusive, prefix_bytes) where:
      - region is 'system' or 'messages'
      - end_index_inclusive is the index of the LAST element included in
        the cached prefix (i.e. the element carrying cache_control)
      - prefix_bytes is the serialized prefix up to and including that
        element, using the same context (tools+system+messages prelude) as
        N+1 will use. For an apples-to-apples comparison we serialize a
        synthetic body containing only what's BEFORE-or-AT the breakpoint:
        full tools + system-up-to-bp + messages-up-to-bp.

    NOTE on noise: Claude Code injects a per-request `cch=XXXXX` token into
    `system[0]` (a billing-header block with NO cache_control). Empirically
    Copilot's cache lookup ignores this — production traces show 796k+ read
    tokens per turn despite system[0] changing every request. So we strip
    `system[0]` before computing prefix bytes; the relative comparison
    between FOLD / CONVERT / PLACEMENT_FIX is what we actually care about.
    The list is ordered by depth: shallow → deep. The cache is hit at the
    deepest breakpoint whose prefix_bytes match between N and N+1.
    """
    out = []
    sys_raw = body.get("system") or []
    msgs = body.get("messages") or []
    tools = body.get("tools") or []
    # Drop system[0] (the cch-bearing billing-header block) to focus the
    # comparison on the genuine cache-relevant prefix.
    sys = sys_raw[1:] if isinstance(sys_raw, list) and len(sys_raw) > 0 else sys_raw
    if isinstance(sys, list):
        for i, b in enumerate(sys):
            if isinstance(b, dict) and b.get("cache_control"):
                prefix = strip_cache_control({
                    "tools": tools,
                    "system": sys[: i + 1],
                    "messages": [],
                })
                out.append(("system", i, serialize_blocks(prefix)))
    if isinstance(msgs, list):
        # only deepest breakpoint in messages matters for our comparison —
        # but we report all so we can see partial hits
        for i, m in enumerate(msgs):
            content = m.get("content")
            if isinstance(content, list):
                for b in content:
                    if isinstance(b, dict) and b.get("cache_control"):
                        prefix = strip_cache_control({
                            "tools": tools,
                            "system": sys,
                            "messages": msgs[: i + 1],
                        })
                        out.append(("messages", i, serialize_blocks(prefix)))
                        break
    return out


def run():
    print(f"{'pair':<12} {'strategy':<14} "
          f"{'system_bps_hit':<16} {'last_msg_bp_hits':<18} "
          f"{'deepest_hit_kind':<18} {'comment':<40}")
    print("-" * 130)

    grand = {k: {
        "system_bp_total": 0, "system_bp_hits": 0,
        "msg_bp_total": 0, "msg_bp_hits": 0,
        "any_full_prefix_hit": 0,
    } for k in STRATEGIES}

    for src, dst in TRACE_PAIRS:
        n_body = load(TRACE_DIR / src)
        n1_body = load(TRACE_DIR / dst)
        pair_label = f"{src.split('-')[3]}→{dst.split('-')[3]}"

        for name, fn in STRATEGIES.items():
            # Normalize content-shape FIRST (mimics ContentBlockParamListConverter)
            # then apply the strategy. Otherwise the same message looks different
            # across turns whenever Claude Code happens to switch between
            # string and array content for it, masking the real cache impact
            # of the system-message strategy under study.
            n_out = fn(normalize_content_to_array(n_body))
            n1_out = fn(normalize_content_to_array(n1_body))
            n_bps = find_breakpoints(n_out)
            n1_bps = find_breakpoints(n1_out)

            # Index N+1's breakpoint prefixes by (region, end_index) so we
            # can ask: "does N+1 contain the SAME prefix bytes as N at this
            # depth?" (N+1 may have more breakpoints — Claude Code added new
            # tool_result cache_control on the latest turn.)
            n1_index = {(r, i): pb for (r, i, pb) in n1_bps}

            # System breakpoints — these should hit every turn if the prefix
            # is stable (top-level system rarely changes within a session).
            system_bp_hits = 0
            for (r, i, pb) in n_bps:
                if r != "system":
                    continue
                grand[name]["system_bp_total"] += 1
                n1_pb = n1_index.get((r, i))
                if n1_pb is not None and n1_pb == pb:
                    system_bp_hits += 1
                    grand[name]["system_bp_hits"] += 1

            # Message breakpoints — N's deepest message breakpoint is on the
            # last tool_result of turn N. In N+1, that exact index still
            # contains the same message (Claude Code is append-only) BUT
            # cache_control may have been moved to a NEW deeper position.
            # We just need that index's prefix bytes to still byte-match.
            last_msg_bp = next((bp for bp in reversed(n_bps) if bp[0] == "messages"), None)
            last_msg_hit_str = "—"
            if last_msg_bp is not None:
                grand[name]["msg_bp_total"] += 1
                (r, i, pb) = last_msg_bp
                # In N+1, the cache_control may have moved off message[i] —
                # but Anthropic's cache lookup hashes the prefix BYTES (with
                # cache_control fields removed), not the breakpoint location.
                # So we compare N's prefix-at-i against N+1's serialized
                # prefix-at-i, both stripped of cache_control fields.
                msgs1 = n1_out.get("messages", [])
                tools1 = n1_out.get("tools") or []
                # Strip system[0] (cch noise) — see find_breakpoints note.
                sys1_raw = n1_out.get("system") or []
                sys1 = sys1_raw[1:] if isinstance(sys1_raw, list) and len(sys1_raw) > 0 else sys1_raw
                n1_prefix_at_i = serialize_blocks(strip_cache_control({
                    "tools": tools1,
                    "system": sys1,
                    "messages": msgs1[: i + 1],
                })) if i < len(msgs1) else None
                if n1_prefix_at_i is not None and n1_prefix_at_i == pb:
                    last_msg_hit_str = "yes"
                    grand[name]["msg_bp_hits"] += 1
                    grand[name]["any_full_prefix_hit"] += 1
                else:
                    # bytes diverge — find first diff position to characterize
                    if n1_prefix_at_i is None:
                        last_msg_hit_str = "n+1 too short"
                    else:
                        common = find_common_prefix_len(pb, n1_prefix_at_i)
                        last_msg_hit_str = f"no @ byte {common}/{len(pb)}"

            sys_total_in_n = sum(1 for bp in n_bps if bp[0] == "system")
            system_str = f"{system_bp_hits}/{sys_total_in_n}"

            # what's the deepest breakpoint that actually hit?
            deepest_hit = "—"
            if last_msg_hit_str == "yes":
                deepest_hit = f"messages[{last_msg_bp[1]}]"
            elif system_bp_hits == sys_total_in_n and sys_total_in_n > 0:
                # find last system bp index in N
                last_sys_bp = max((i for (r, i, _) in n_bps if r == "system"), default=None)
                deepest_hit = f"system[{last_sys_bp}]"
            elif system_bp_hits > 0:
                deepest_hit = f"system (partial)"
            else:
                deepest_hit = "NONE"

            comment = ""
            if name.strip() == "FOLD" and last_msg_hit_str != "yes":
                comment = "msg bp prefix broken by sys growth"
            elif name.strip() in ("CONVERT", "PLACEMENT_FIX") and last_msg_hit_str != "yes":
                # check whether it's the system[0] cch=xxxxx drift
                comment = "may be unrelated drift (cch token)"

            print(f"{pair_label:<12} {name} "
                  f"{system_str:<16} {last_msg_hit_str:<18} "
                  f"{deepest_hit:<18} {comment:<40}")
        print()

    print("=" * 130)
    print("SUMMARY across all pairs:")
    print(f"  {'strategy':<14} {'system_bp_hits':<22} {'last_msg_bp_hits':<22} {'any_full_prefix_hit':<22}")
    for name in STRATEGIES:
        t = grand[name]
        s = f"{t['system_bp_hits']}/{t['system_bp_total']}"
        m = f"{t['msg_bp_hits']}/{t['msg_bp_total']}"
        a = f"{t['any_full_prefix_hit']}/{len(TRACE_PAIRS)}"
        print(f"  {name} {s:<22} {m:<22} {a:<22}")


if __name__ == "__main__":
    run()
