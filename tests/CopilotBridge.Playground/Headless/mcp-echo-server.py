#!/usr/bin/env python3
"""Minimal stdio MCP server for the headless bridge test suite.

Exposes a single tool: `echo(text)` that returns `ECHO: <text>` so the test can
assert a canary string round-trips Claude Code -> bridge -> Copilot -> Claude Code
when MCP is involved. Implements just the protocol surface Claude Code calls:
initialize, notifications/initialized, tools/list, tools/call.

Transport: stdio with line-delimited JSON-RPC 2.0. No framing headers (MCP's
default stdio mode).
"""

import json
import sys


def respond(req_id, result=None, error=None):
    msg = {"jsonrpc": "2.0", "id": req_id}
    if error is not None:
        msg["error"] = error
    else:
        msg["result"] = result
    sys.stdout.write(json.dumps(msg) + "\n")
    sys.stdout.flush()


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            continue

        method = req.get("method")
        req_id = req.get("id")

        if method == "initialize":
            respond(req_id, {
                "protocolVersion": "2024-11-05",
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "mcp-echo", "version": "0.1.0"},
            })
        elif method == "notifications/initialized":
            # JSON-RPC notification: no response.
            continue
        elif method == "tools/list":
            respond(req_id, {
                "tools": [{
                    "name": "echo",
                    "description": "Echoes the input string back, prefixed with 'ECHO: '.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {"text": {"type": "string"}},
                        "required": ["text"],
                    },
                }]
            })
        elif method == "tools/call":
            params = req.get("params", {}) or {}
            args = params.get("arguments", {}) or {}
            text = args.get("text", "")
            respond(req_id, {
                "content": [{"type": "text", "text": f"ECHO: {text}"}],
                "isError": False,
            })
        elif req_id is not None:
            respond(req_id, error={"code": -32601, "message": f"Method not found: {method}"})


if __name__ == "__main__":
    main()
