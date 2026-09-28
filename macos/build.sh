#!/bin/sh
# Builds g6vol.app next to this script.
set -e
cd "$(dirname "$0")"
APP=build/g6vol.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp Info.plist "$APP/Contents/Info.plist"
swiftc -O main.swift -o "$APP/Contents/MacOS/g6vol" \
  -Xlinker -sectcreate -Xlinker __TEXT -Xlinker __info_plist -Xlinker Info.plist
codesign --force --sign - --identifier local.g6vol "$APP"
echo "built $APP"
