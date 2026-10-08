#!/bin/bash
# Builds OpenAL Soft for Android arm64-v8a from source with the NDK.
#
# Environment:
#   ANDROID_NDK_ROOT  path to the NDK (required; no hardcoded developer paths)
#   ANDROID_API       target API level (default 24)
#
# Output: thirdparty/openal-android/lib/libopenal.so, copied into
#         OpenRA.Android/jniLibs/arm64-v8a/ as libsoft_oal.so
#         (OpenAL-CS P/Invokes "soft_oal"; the APK must ship libsoft_oal.so).
#
# The OpenAL Soft source is cloned from the official repository if absent.
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$HERE/.." && pwd)"

: "${ANDROID_NDK_ROOT:?Set ANDROID_NDK_ROOT to your Android NDK path}"
API="${ANDROID_API:-24}"

SRC="$HERE/openal-soft"
BUILD_DIR="$HERE/openal-build-arm64"
OUT="$HERE/openal-android"
JNILIBS="$REPO_ROOT/OpenRA.Android/jniLibs/arm64-v8a"

if [ ! -d "$SRC" ]; then
	git clone --depth 1 --branch 1.23.1 https://github.com/kcat/openal-soft.git "$SRC"
fi

command -v cmake >/dev/null 2>&1 || { echo "cmake is required" >&2; exit 1; }

TOOLCHAIN_FILE="$ANDROID_NDK_ROOT/build/cmake/android.toolchain.cmake"
[ -f "$TOOLCHAIN_FILE" ] || { echo "NDK toolchain file not found at $TOOLCHAIN_FILE" >&2; exit 1; }

rm -rf "$BUILD_DIR" "$OUT"
mkdir -p "$BUILD_DIR" "$OUT" "$JNILIBS"

cmake -S "$SRC" -B "$BUILD_DIR" \
  -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN_FILE" \
  -DANDROID_ABI=arm64-v8a \
  -DANDROID_PLATFORM=android-"$API" \
  -DANDROID_STL=c++_static \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-z,max-page-size=16384" \
  -DALSOFT_BACKEND_OSS=OFF \
  -DALSOFT_BACKEND_OPENSL=ON \
  -DALSOFT_BACKEND_WAVE=OFF \
  -DALSOFT_EXAMPLES=OFF \
  -DALSOFT_TESTS=OFF \
  -DALSOFT_UTILS=OFF \
  -DALSOFT_INSTALL=ON \
  -DCMAKE_INSTALL_PREFIX="$OUT"

cmake --build "$BUILD_DIR" --config Release -j"$(nproc 2>/dev/null || sysctl -n hw.ncpu)"
cmake --install "$BUILD_DIR" --config Release

cp "$OUT/lib/libopenal.so" "$JNILIBS/libsoft_oal.so"

echo "=== built: $JNILIBS/libsoft_oal.so ==="
ls -lh "$JNILIBS/libsoft_oal.so"
echo "=== verify no unexpected shared dependencies ==="
readelf -d "$JNILIBS/libsoft_oal.so" 2>/dev/null | grep NEEDED || llvm-readelf -d "$OUT/lib/libopenal.so" | grep NEEDED || true
