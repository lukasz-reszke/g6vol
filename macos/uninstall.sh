#!/bin/sh
# Stops g6vol and removes the app and LaunchAgent.
LABEL=local.g6vol
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null
rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"
rm -rf "$HOME/Applications/g6vol.app"
echo "uninstalled"
