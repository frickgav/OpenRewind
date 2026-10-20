#!/usr/bin/env python3
"""Check that the local search server (port 3031 API) never serves what the block list hides.

    OPENREWIND_HOME=/tmp/x python3 scripts/server-check.py dist/OpenRewind.app/Contents/MacOS/OpenRewind

Sets a block list in the app's settings (an app, a word, a site, private windows), stores one
screenshot of each kind it covers plus one it does not (`<exe> ingest`), starts `<exe> serve` and
asks every route that returns frames: only the visible frame may come back, and a blocked frame's
picture is "not found". Then unblocks the app while the server runs (it must show up) and blocks it
again (it must go). Then adds a few hundred frames, most of them hidden, straight into the database and
checks that `total` counts only the visible ones, from the first page on, and that paging never repeats a
frame. Exits non-zero on failure. Windows runs the same checks as unit tests (ServerBlocklistTests.cs).

Mac only: it uses `defaults` for the settings. Your own block list is in the same place (OPENREWIND_HOME
does not move it), so it is saved first and put back at the end.
"""
import json
import os
import plistlib
import sqlite3
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zlib

DOMAIN = "ai.agilityresources.openrewind"
KEYS = ["blockedApps", "blockedKeywords", "blockPrivateWindows", "blockedSites"]
PORT = 3139


def png(path):
    """A plain 64×64 grey picture."""
    w = h = 64
    raw = b"".join(b"\x00" + b"\x80\x80\x80" * w for _ in range(h))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def defaults(*args):
    subprocess.run(["defaults", *args], check=False, capture_output=True)


def export():
    """The app's settings as a dict ({} when it has none)."""
    out = subprocess.run(["defaults", "export", DOMAIN, "-"], capture_output=True)
    return plistlib.loads(out.stdout) if out.returncode == 0 and out.stdout else {}


def restore(saved):
    """Put the block list keys back as they were, keeping every other setting as it is now."""
    now = export()
    for k in KEYS:
        now.pop(k, None)
        if k in saved:
            now[k] = saved[k]
    subprocess.run(["defaults", "import", DOMAIN, "-"], input=plistlib.dumps(now), check=True)


def block(apps):
    defaults("write", DOMAIN, "blockedApps", "-array", *apps)
    defaults("write", DOMAIN, "blockedKeywords", "-array", "Payroll")
    defaults("write", DOMAIN, "blockPrivateWindows", "-bool", "true")
    defaults("write", DOMAIN, "blockedSites", "-array", "chase.com")


BBEDIT = '{id = "com.barebones.bbedit"; name = "BBEdit";}'


def get(path):
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{PORT}{path}", timeout=10) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


def frame_ids(path):
    status, body = get(path)
    assert status == 200, (path, status, body)
    data = json.loads(body)
    return [d["content"]["frame_id"] for d in data["data"] if d["type"] == "OCR"], data["pagination"]["total"], body


def main():
    exe = sys.argv[1]
    image = os.path.join(tempfile.mkdtemp(), "shot.png")
    png(image)
    saved = export()
    try:
        check(exe, image)
    finally:
        restore(saved)


def add_frames(n):
    """n frames with the word "overdraft", oldest first and older than the rest; one in four visible. The
    others are hidden by the blocked app, a blocked word in the title, a blocked site or a private window.
    Returns the visible ids, newest first."""
    home = os.environ.get("OPENREWIND_HOME") or os.path.expanduser("~/.openrewind")
    db = sqlite3.connect(os.path.join(home, "db.sqlite"), timeout=30)
    visible = []
    for i in range(n):
        app, window, url = [("Notes", f"list {i}", ""), ("Safari", f"Payroll {i}", ""),
                            ("Safari", f"Statement {i}", f"https://secure.chase.com/s/{i}"),
                            ("BBEdit" if i % 8 == 3 else "Safari", f"Tab {i} - Private Browsing", "")][i % 4]
        ts = time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(time.time() - 86400 * 400 + i)) + ".000Z"
        cur = db.execute("INSERT INTO frames (timestamp, app_name, window_name, text, browser_url) VALUES (?, ?, ?, ?, ?)",
                         (ts, app, window, f"budget overdraft {i}", url))
        if i % 4 == 0:
            visible.append(cur.lastrowid)
    db.commit()
    db.close()
    return visible[::-1]


def check(exe, image):
    block([BBEDIT])

    def ingest(*args):
        out = subprocess.run([exe, "ingest", image, *args], capture_output=True, text=True, timeout=120)
        line = out.stdout.strip().splitlines()[-1] if out.stdout.strip() else ""
        assert line.startswith("stored frame "), (args, out.stdout, out.stderr)
        return int(line.split()[-1])

    visible = ingest("--app", "Notes", "--window", "shopping list")
    hidden = {
        "blocked app": ingest("--app", "BBEdit", "--window", "draft.txt"),
        "blocked site": ingest("--app", "Safari", "--window", "Statements", "--url", "https://secure.chase.com/accounts"),
        "blocked word in the title": ingest("--app", "Safari", "--window", "Payroll - Acme"),
        "blocked word in the address": ingest("--app", "Safari", "--window", "Docs", "--url", "https://docs.example.com/payroll/2026"),
        "private window": ingest("--app", "Safari", "--window", "Search - Private Browsing"),
        "saved while blocked": ingest("--app", "Notes", "--window", "", "--blocked"),
    }

    server = subprocess.Popen([exe, "serve", "--port", str(PORT)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        for _ in range(60):
            try:
                if get("/health")[0] == 200:
                    break
            except OSError:
                pass
            time.sleep(0.5)
        else:
            sys.exit("the server did not start")

        for path in ["/search", "/search?content_type=ocr", "/search?start_time=1d%20ago", "/search?limit=100&offset=0"]:
            ids, total, body = frame_ids(path)
            assert ids == [visible], (path, ids, hidden)
            assert total == 1, (path, total)
            for word in [b"BBEdit", b"chase", b"Payroll", b"payroll", b"Private Browsing"]:
                assert word not in body, (path, word, body)
        for path in ["/search?app_name=BBEdit", "/search?window_name=Payroll", "/search?app_name=Safari"]:
            ids, total, _ = frame_ids(path)
            assert ids == [] and total == 0, (path, ids, total)

        status, _ = get(f"/frames/{visible}")
        assert status == 200, ("visible frame", status)
        for why, fid in hidden.items():
            status, body = get(f"/frames/{fid}")
            assert status == 404, (why, fid, status)
            assert json.loads(body)["error"] == "frame not found", (why, body)

        status, body = get("/apps")
        apps = [a["app_name"] for a in json.loads(body)["data"]]
        assert "BBEdit" not in apps and "Notes" in apps, apps

        # `serve` reads the block list again for each request: unblock BBEdit, then block it again.
        block([])
        for _ in range(20):
            if hidden["blocked app"] in frame_ids("/search")[0]:
                break
            time.sleep(0.5)
        else:
            sys.exit("an unblocked app did not come back")
        assert get(f"/frames/{hidden['blocked app']}")[0] == 200
        block([BBEDIT])
        for _ in range(20):
            if frame_ids("/search")[0] == [visible]:
                break
            time.sleep(0.5)
        else:
            sys.exit("a newly blocked app still comes back")
        assert get(f"/frames/{hidden['blocked app']}")[0] == 404

        # The total counts only what can be shown, on the first page too: otherwise comparing it with the
        # results would tell whether blocked screenshots contain a word.
        many = add_frames(400)
        for limit in [1, 40]:
            ids, total, _ = frame_ids(f"/search?q=overdraft&limit={limit}")
            assert total == len(many), (limit, total, len(many))
            assert ids == many[:limit], (limit, ids, many[:limit])
        got = []
        for offset in range(0, len(many), 30):
            ids, total, _ = frame_ids(f"/search?q=overdraft&limit=30&offset={offset}")
            assert total == len(many), (offset, total)
            got += ids
        assert got == many, (len(got), len(set(got)), len(many))
        print(f"✓ local server: frame {visible} shown; {len(hidden)} blocked frames left out of /search, /frames and /apps; "
              f"total {len(many)} of 400 at every offset")
    finally:
        server.terminate()
        server.wait(10)


if __name__ == "__main__":
    main()
