#!/bin/bash
# Build OpenRewind.app, put it in /Applications, and restart it. One step after each code change.
#
#   scripts/install.sh
#
# Permissions (Screen Recording, Accessibility) stay on as long as the app is signed with the same
# certificate. build-app.sh picks your Developer ID or Apple Development certificate on its own.
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/build-app.sh

echo "→ quitting the running app"
osascript -e 'quit app "OpenRewind"' 2>/dev/null || true
for _ in $(seq 1 20); do pgrep -x OpenRewind >/dev/null || break; sleep 0.5; done
pkill -x OpenRewind 2>/dev/null || true

echo "→ installing to /Applications"
rm -rf /Applications/OpenRewind.app
cp -R dist/OpenRewind.app /Applications/
open /Applications/OpenRewind.app
echo "✓ OpenRewind restarted"
