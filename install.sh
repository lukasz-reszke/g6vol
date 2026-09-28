#!/bin/sh
# Installs g6vol.app into ~/Applications and starts it at login via a LaunchAgent.
set -e
cd "$(dirname "$0")"
[ -d build/g6vol.app ] || ./build.sh
LABEL=local.g6vol
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
mkdir -p "$HOME/Applications" "$HOME/Library/LaunchAgents" "$HOME/Library/Logs"
rm -rf "$HOME/Applications/g6vol.app"
cp -R build/g6vol.app "$HOME/Applications/"
cat > "$PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>Label</key>
	<string>$LABEL</string>
	<key>ProgramArguments</key>
	<array>
		<string>$HOME/Applications/g6vol.app/Contents/MacOS/g6vol</string>
	</array>
	<key>RunAtLoad</key>
	<true/>
	<key>KeepAlive</key>
	<dict>
		<key>SuccessfulExit</key>
		<false/>
	</dict>
	<key>ThrottleInterval</key>
	<integer>10</integer>
	<key>ProcessType</key>
	<string>Interactive</string>
	<key>StandardOutPath</key>
	<string>$HOME/Library/Logs/g6vol.log</string>
	<key>StandardErrorPath</key>
	<string>$HOME/Library/Logs/g6vol.log</string>
</dict>
</plist>
PLIST
launchctl bootstrap "gui/$(id -u)" "$PLIST"
echo "installed; log: ~/Library/Logs/g6vol.log"
