#!/bin/bash
# Builds Lua 5.1.5 for Android arm64-v8a from source with the NDK.
#
# Environment:
#   ANDROID_NDK_ROOT  path to the NDK (required; no hardcoded developer paths)
#   ANDROID_API       target API level (default 24)
#
# Output: thirdparty/lua-android/lib/liblua51.so, then copied into
#         OpenRA.Android/jniLibs/arm64-v8a/.
#
# The Lua source is downloaded from the official archives if absent.
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$HERE/.." && pwd)"

: "${ANDROID_NDK_ROOT:?Set ANDROID_NDK_ROOT to your Android NDK path}"
API="${ANDROID_API:-24}"

SRC="$HERE/lua-5.1.5"
BUILD="$HERE/lua-build-arm64"
OUT="$HERE/lua-android"
JNILIBS="$REPO_ROOT/OpenRA.Android/jniLibs/arm64-v8a"

if [ ! -d "$SRC" ]; then
	curl -L -o "$HERE/lua-5.1.5.tar.gz" https://www.lua.org/ftp/lua-5.1.5.tar.gz
	tar -xzf "$HERE/lua-5.1.5.tar.gz" -C "$HERE"
fi

HOST="${ANDROID_NDK_HOST:-$(uname -s | tr '[:upper:]' '[:lower:]')-$(uname -m)}"
TOOLCHAIN="$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/$HOST"
[ -d "$TOOLCHAIN" ] || { echo "NDK toolchain not found at $TOOLCHAIN (set ANDROID_NDK_HOST if it differs)" >&2; exit 1; }

export PATH="$TOOLCHAIN/bin:$PATH"
TARGET=aarch64-linux-android
export CC="$TOOLCHAIN/bin/${TARGET}${API}-clang"
export AR="$TOOLCHAIN/bin/llvm-ar"
export RANLIB="$TOOLCHAIN/bin/llvm-ranlib"
export MYCFLAGS="-DLUA_USE_LINUX -fPIC"
export MYLDFLAGS="-Wl,-z,max-page-size=16384"
export LDFLAGS="$MYLDFLAGS"

[ -x "$CC" ] || { echo "NDK clang not found at $CC" >&2; exit 1; }

rm -rf "$BUILD" "$OUT"
mkdir -p "$BUILD" "$OUT/lib" "$JNILIBS"

# Build the Lua core objects manually to avoid the posix/Readline dependency of the full
# Makefile target.
cd "$SRC"
make clean >/dev/null 2>&1 || true

CORE="lapi lcode ldebug ldo ldump lfunc lgc llex lmem lobject lopcodes lparser lstate lstring ltable ltm lundump lvm lzio"
LIB="lauxlib lbaselib ldblib liolib lmathlib loslib ltablib lstrlib loadlib linit"
OBJS=""
for f in $CORE $LIB; do
	"$CC" $MYCFLAGS -c "src/$f.c" -o "$BUILD/$f.o"
	OBJS="$OBJS $BUILD/$f.o"
done

# Link into a shared library named lua51.so so the Eluant P/Invoke ("lua51") resolves on Android.
"$CC" -shared $MYLDFLAGS -Wl,-soname,liblua51.so -o "$OUT/lib/liblua51.so" $OBJS -lm -ldl

cp "$OUT/lib/liblua51.so" "$JNILIBS/liblua51.so"

echo "=== built: $JNILIBS/liblua51.so ==="
ls -lh "$JNILIBS/liblua51.so"
