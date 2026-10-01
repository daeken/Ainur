#!/usr/bin/env python3
"""A tiny stdio MCP server for tests: N inventory tools plus an `echo_upper` tool, with paginated tools/list."""
import json, sys

N = int(sys.argv[1]) if len(sys.argv) > 1 else 300
TOOLS = [{"name": f"inventory_{i:03d}", "description": f"Count stock in warehouse bin {i} and report shrinkage for audits.",
          "inputSchema": {"type": "object", "properties": {"bin": {"type": "string"}}}} for i in range(N)]
TOOLS.append({"name": "echo_upper", "description": "Return the given text in upper case.",
              "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]}})
PAGE = 100

def send(msg):
    sys.stdout.write(json.dumps(msg) + "\n")
    sys.stdout.flush()

for line in sys.stdin:
    msg = json.loads(line)
    method, mid = msg.get("method"), msg.get("id")
    if mid is None:
        continue
    if method == "initialize":
        send({"jsonrpc": "2.0", "id": mid, "result": {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}}, "serverInfo": {"name": "fake", "version": "1"}}})
    elif method == "tools/list":
        start = int((msg.get("params") or {}).get("cursor") or 0)
        result = {"tools": TOOLS[start:start + PAGE]}
        if start + PAGE < len(TOOLS):
            result["nextCursor"] = str(start + PAGE)
        send({"jsonrpc": "2.0", "id": mid, "result": result})
    elif method == "tools/call":
        p = msg["params"]
        if p["name"] == "echo_upper":
            send({"jsonrpc": "2.0", "id": mid, "result": {"content": [{"type": "text", "text": p["arguments"]["text"].upper()}]}})
        else:
            send({"jsonrpc": "2.0", "id": mid, "result": {"content": [{"type": "text", "text": f"bin {p['arguments'].get('bin')}: 42 units"}]}})
    else:
        send({"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": "method not found"}})
