#!/bin/bash
# Builds, aligns, signs, installs, and launches the OpenRA Android APK on a connected device.
# All tool paths come from environment variables — no hardcoded developer-machine paths.
#
# Environment:
#   DOTNET_ROOT       .NET SDK location (default: $HOME/.dotnet)
#   JAVA_HOME         JDK 17 location (required by the Android SDK tools)
#   ANDROID_SDK_ROOT  Android SDK location (default: $HOME/Android/Sdk or $HOME/Library/Android/sdk)
#   ANDROID_NDK_ROOT  Android NDK location (required to build the native libraries)
#   ANDROID_SERIAL    (optional) target device for adb; without it, adb picks the only device
#   ANDROID_LINK_MODE (optional) SdkOnly (default) or None — None avoids the linker's peak
#                    memory use on small machines, at the cost of a larger APK
set -e

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$HERE/.." && pwd)"
cd "$REPO_ROOT"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

if [ -z "${ANDROID_SDK_ROOT:-}" ]; then
	for candidate in "$HOME/Android/Sdk" "$HOME/Library/Android/sdk"; do
		if [ -d "$candidate" ]; then ANDROID_SDK_ROOT="$candidate"; break; fi
	done
fi
export ANDROID_SDK_ROOT="${ANDROID_SDK_ROOT:?Set ANDROID_SDK_ROOT to your Android SDK path}"
export ANDROID_HOME="$ANDROID_SDK_ROOT"
export ANDROID_NDK_ROOT="${ANDROID_NDK_ROOT:?Set ANDROID_NDK_ROOT to your Android NDK path}"

ADB="$ANDROID_SDK_ROOT/platform-tools/adb"
BUILD_TOOLS_VERSION=$(ls "$ANDROID_SDK_ROOT/build-tools" | sort -V | tail -1)
BUILD_TOOLS="$ANDROID_SDK_ROOT/build-tools/$BUILD_TOOLS_VERSION"
[ -d "$BUILD_TOOLS" ] || { echo "No build-tools found under $ANDROID_SDK_ROOT/build-tools" >&2; exit 1; }
KEYSTORE="${ANDROID_KEYSTORE:-$HOME/.android/debug.keystore}"

echo "=== build APK ==="
BUILD_LOG=$(mktemp)
# -m:1 keeps peak memory bounded (ILLink is memory-hungry and gets OOM-killed when
# multiple MSBuild nodes run in parallel on small machines).
# ANDROID_LINK_MODE defaults to the SDK's Release default (SdkOnly); set it to None on
# machines with little RAM, where the linker process itself may be OOM-killed.
BUILD_RC=0
dotnet build OpenRA.Android/OpenRA.Android.csproj -c Release -f net9.0-android -p:BuildForAndroid=true \
	-m:1 "-p:AndroidLinkMode=${ANDROID_LINK_MODE:-SdkOnly}" > "$BUILD_LOG" 2>&1 || BUILD_RC=$?
grep -E "error|Build succeeded|Build FAILED" "$BUILD_LOG" | tail -5
if [ $BUILD_RC -ne 0 ]; then echo "BUILD FAILED (rc=$BUILD_RC)"; rm -f "$BUILD_LOG"; exit 1; fi
rm -f "$BUILD_LOG"

APK="OpenRA.Android/obj/Release/android/bin/net.openra.android.apk"
[ -f "$APK" ] || { echo "APK not found at $APK"; exit 1; }

echo "=== align + sign ==="
"$BUILD_TOOLS/zipalign" -f -p 4 "$APK" "${APK%.apk}-aligned.apk"

# Create the standard debug keystore if it doesn't exist yet (fresh CI/dev machines).
if [ ! -f "$KEYSTORE" ]; then
	KEYTOOL="${JAVA_HOME:-}/bin/keytool"
	[ -x "$KEYTOOL" ] || KEYTOOL=$(command -v keytool || true)
	[ -n "$KEYTOOL" ] || { echo "keytool not found; set JAVA_HOME" >&2; exit 1; }
	mkdir -p "$(dirname "$KEYSTORE")"
	"$KEYTOOL" -genkeypair -v -keystore "$KEYSTORE" -storepass android -keypass android \
		-alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 \
		-dname "CN=Android Debug,O=Android,C=US" >/dev/null 2>&1
	echo "created debug keystore at $KEYSTORE"
fi

"$BUILD_TOOLS/apksigner" sign --ks "$KEYSTORE" --ks-pass pass:android --key-pass pass:android \
  --out "${APK%.apk}-Signed.apk" "${APK%.apk}-aligned.apk"

if ! "$ADB" get-state >/dev/null 2>&1; then
	echo "=== no device connected; APK signed at ${APK%.apk}-Signed.apk ==="
	exit 0
fi

echo "=== install ==="
"$ADB" install -r "${APK%.apk}-Signed.apk" 2>&1 | tail -2

echo "=== launch ==="
"$ADB" logcat -c
ACT=$("$ADB" shell dumpsys package net.openra.android 2>/dev/null | grep -oE 'net.openra.android/crc[0-9a-f]+\.MainActivity' | head -1)
"$ADB" shell am start -n "$ACT" 2>&1 | tail -2
