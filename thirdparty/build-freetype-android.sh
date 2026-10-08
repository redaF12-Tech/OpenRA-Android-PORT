#!/bin/bash
# Builds FreeType for Android arm64-v8a from source with the NDK.
#
# Environment:
#   ANDROID_NDK_ROOT  path to the NDK (required; no hardcoded developer paths)
#   ANDROID_API       target API level (default 24)
#
# Output: thirdparty/freetype-android/lib/libfreetype6.so
#         (named libfreetype6.so to match the engine's DllImport("freetype6") and the
#          AndroidNativeLibrary entry in OpenRA.Android.csproj), then copied into
#          OpenRA.Android/jniLibs/arm64-v8a/.
#
# The FreeType source is cloned from the official git mirror if absent.
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$HERE/.." && pwd)"

: "${ANDROID_NDK_ROOT:?Set ANDROID_NDK_ROOT to your Android NDK path}"
API="${ANDROID_API:-24}"

SRC="$HERE/freetype"
BUILD_DIR="$HERE/freetype-build-arm64"
OUT="$HERE/freetype-android"
JNILIBS="$REPO_ROOT/OpenRA.Android/jniLibs/arm64-v8a"

if [ ! -d "$SRC" ]; then
	git clone --depth 1 --branch VER-2-13-2 https://gitlab.freedesktop.org/freetype/freetype.git "$SRC"
fi

command -v cmake >/dev/null 2>&1 || { echo "cmake is required" >&2; exit 1; }

TOOLCHAIN_FILE="$ANDROID_NDK_ROOT/build/cmake/android.toolchain.cmake"
[ -f "$TOOLCHAIN_FILE" ] || { echo "NDK toolchain file not found at $TOOLCHAIN_FILE" >&2; exit 1; }

rm -rf "$BUILD_DIR" "$OUT"
mkdir -p "$BUILD_DIR" "$OUT" "$JNILIBS"

# NDK r27+ supports Android 15's 16 KB page-size requirement via this linker flag.
cmake -S "$SRC" -B "$BUILD_DIR" \
  -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN_FILE" \
  -DANDROID_ABI=arm64-v8a \
  -DANDROID_PLATFORM=android-"$API" \
  -DANDROID_STL=c++_shared \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-z,max-page-size=16384" \
  -DBUILD_SHARED_LIBS=ON \
  -DFT_DISABLE_BZIP2=ON \
  -DFT_DISABLE_BROTLI=ON \
  -DFT_DISABLE_HARFBUZZ=ON \
  -DFT_DISABLE_PNG=ON \
  -DFT_DISABLE_ZLIB=OFF \
  -DCMAKE_INSTALL_PREFIX="$OUT"

cmake --build "$BUILD_DIR" --config Release -j"$(nproc 2>/dev/null || sysctl -n hw.ncpu)"
cmake --install "$BUILD_DIR" --config Release

# Normalize the output name: OpenRA's P/Invoke uses "freetype6" (matching the Debian-style
# library name the engine already targets on Linux), so the APK must ship libfreetype6.so.
cp "$OUT/lib/libfreetype.so" "$JNILIBS/libfreetype6.so"

echo "=== built: $JNILIBS/libfreetype6.so ==="
ls -lh "$JNILIBS/libfreetype6.so"
