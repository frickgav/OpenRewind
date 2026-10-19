#!/usr/bin/env python3
"""Start the app as an AI app does (`<exe> mcp`, stdin/stdout as pipes) and talk MCP to it.

    OPENREWIND_HOME=/tmp/x python3 scripts/mcp-check.py dist/OpenRewind.app/Contents/MacOS/OpenRewind off
    OPENREWIND_HOME=/tmp/x python3 scripts/mcp-check.py dist/OpenRewind.app/Contents/MacOS/OpenRewind on

"off" expects the Settings switch to be off (tools ask to turn it on); "on" expects it on and reads
the history in OPENREWIND_HOME. Exits non-zero on failure. The Windows check is mcp-check-windows.ps1.
"""
import json
import subprocess
import sys
import threading


def main():
    exe, mode = sys.argv[1], sys.argv[2]
    for flag in (["mcp"], ["--mcp"]) if mode == "off" else (["mcp"],):
        p = subprocess.Popen([exe] + flag, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
        threading.Thread(target=lambda: sys.stderr.write(p.stderr.read()), daemon=True).start()

        def ask(msg):
            p.stdin.write(json.dumps(msg) + "\n")
            p.stdin.flush()
            result = {}
            t = threading.Thread(target=lambda: result.setdefault("line", p.stdout.readline()), daemon=True)
            t.start()
            t.join(20)
            line = result.get("line")
            if not line:
                p.kill()
                sys.exit(f"no reply to {msg}")
            return json.loads(line)

        init = ask({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                    "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "ci", "version": "1"}}})
        assert init["result"]["protocolVersion"] == "2025-06-18", init
        p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        tools = [t["name"] for t in ask({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})["result"]["tools"]]
        assert "search_screen" in tools, tools
        r = ask({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "activity_summary", "arguments": {"start": "today"}}})["result"]
        text = r["content"][0]["text"]
        if mode == "off":
            assert r["isError"] and "Allow AI Apps" in text, r
        else:
            assert not r["isError"] and "Nothing was recorded" in text, r
        p.stdin.close()
        if p.wait(10) != 0:
            sys.exit(f"exit code {p.returncode}")
        print(f"✓ {' '.join(flag)} ({mode}): {text[:80]}")


if __name__ == "__main__":
    main()
