#!/bin/bash
#
# Builds the SAFT Android APK on this Mac.
#
# Everything below is here because it was discovered the hard way. The tool locations are not the
# defaults - the Android SDK had to move off a path containing a space, because sdkmanager splits
# its own arguments on spaces and installs into nonsense subdirectories when it finds one. The JDK
# is Homebrew's 17, because the Android build refuses newer ones.
#
# Run it from anywhere:   ./build/build-apk.sh
# The finished APK lands in ./apk/SAFT.apk

set -euo pipefail

cd "$(dirname "$0")/.."   # repo root

# The SDK is not in the repo (.gitignore excludes .tools/). This is where it lives on the
# machine this was built on; point DOTNET_ROOT elsewhere if yours differs.
export DOTNET_ROOT="${DOTNET_ROOT:-/Volumes/Untitled/San Andreas/SAFT/.tools/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export JAVA_HOME="${JAVA_HOME:-/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home}"
export ANDROID_HOME="${ANDROID_HOME:-/Volumes/Untitled/android-sdk}"

for required in "$DOTNET_ROOT/dotnet" "$JAVA_HOME/bin/java" "$ANDROID_HOME/platforms/android-34/android.jar"; do
    if [ ! -e "$required" ]; then
        echo "Missing: $required" >&2
        echo "The toolchain is not where this script expects it. See the comments above." >&2
        exit 1
    fi
done

OUT="$HOME/saft-android-build/SAFT.Droid/bin/Release/net8.0-android34.0"

# The APK is stamped with the moment it was built, in two places that both matter.
#
# The NAME is shown on SAFT's own first line, so the build running on the device can be identified
# from a photograph of the screen. This is not decoration. Twice now a stale artifact has been
# tested and reported on as though it were the new one, and both times it cost a round trip and
# some trust. A build that cannot say which build it is, is not testable.
#
# The CODE has to rise every time or Android may decline to replace the installed copy. Minutes
# since a fixed point gives a number that always increases and stays comfortably inside the range
# Android allows.
STAMP="$(date '+%m-%d %H:%M')"
CODE="$(( ($(date +%s) - 1700000000) / 60 ))"

# The publish folder is emptied first, because of a genuinely mean interaction: the Android build
# writes its APK with a FIXED timestamp of 1 Jan 1981 so that builds are reproducible, and MSBuild
# copies it onward only if it is newer than what is already there. 1981 is never newer than 1981,
# so after the first build that copy is skipped forever and publish/ keeps serving the first APK it
# ever saw. Deleting it means there is nothing stale to serve.
rm -rf "$OUT/publish"

# The packaged APKs go too, for a related but distinct reason.
#
# Every build stamps a new version name, but MSBuild decides whether to repackage by looking at
# whether the SOURCE changed. Change only the version - or the signing key, which is how this was
# found - and it correctly concludes the code is identical and skips packaging, leaving the previous
# APK in place wearing the previous stamp. Removing them means there is nothing to leave in place.
#
# Repackaging costs seconds. Shipping the wrong APK has already cost this project two test cycles.
# The generated manifest goes too. The version name is written into it, and it is cached on the same
# "did the source change" test, so removing only the APKs produced a correctly re-signed package
# still wearing the previous build's stamp.
#
# Only the manifest and the packaged output - NOT the whole generated android/ folder. That folder
# also holds .ll files whose producing step has its own up-to-date check, so deleting them does not
# bring them back; it just fails the build with "Could not open input file: environment.arm64-v8a.ll".
#
# NOT android/bin. Deleting that directory is what shipped six consecutive broken builds: it holds
# the compiled Java stubs for every generated class, but the step that PRODUCES them keeps its
# up-to-date marker somewhere else. Remove the output without invalidating the marker and the
# compile step decides it has nothing to do, packaging proceeds, and out comes a perfectly valid,
# correctly signed APK whose classes.dex contains none of the application's classes. It installs. It
# launches. It dies on "Didn't find class ...MainActivity" before a line of managed code runs, which
# is indistinguishable from a dozen other startup failures and leaves no crash log, because the code
# that writes crash logs is one of the classes that is not there.
#
# The stamp directory goes too. Deleting the generated manifest is not enough on its own: these
# targets decide whether to run by looking at their stamp files, not at whether their output still
# exists. A build where only SAFT.Core changed therefore regenerated nothing, and the guard below
# caught an APK still wearing the previous build's version.
#
# Note it is the stamp/ directory, NOT android/ - that one holds generated .ll files whose producing
# step has its own check, and removing those just fails the build outright.
OBJ_ROOT="$HOME/saft-android-build/SAFT.Droid/obj/Release/net8.0-android34.0"
ANDROID_OBJ="$OBJ_ROOT/android"
rm -f "$OUT"/*.apk
rm -f "$ANDROID_OBJ/AndroidManifest.xml"
rm -rf "$OBJ_ROOT/stamp"

# The release signing key.
#
# It lives OUTSIDE this project on purpose. The signing key is the app's permanent identity to
# Android: install an update signed with a different key and the install fails outright, so every
# user would have to uninstall SAFT and lose their settings to get the new version. Losing this file
# is not recoverable - there is no way to re-issue it - so it does not go anywhere near a folder
# that might get published, and it wants a backup somewhere that is not this SD card.
#
# Before this existed, builds were signed with the auto-generated debug key from ~/.android, which
# would have been fine right up until that file was regenerated on a new machine.
# Outside the repo, deliberately. See the note above.
SIGNING="${SAFT_SIGNING:-/Volumes/Untitled/San Andreas/saft-signing}"
KEYSTORE="$SIGNING/saft-release.keystore"

if [ ! -f "$KEYSTORE" ] || [ ! -f "$SIGNING/password.txt" ]; then
    echo "Missing the release keystore at $KEYSTORE" >&2
    echo "Without it this would silently fall back to the debug key, which cannot ship." >&2
    exit 1
fi

# Read rather than written into this file, so the password is not in a script that could be
# committed by accident.
STOREPASS="$(cat "$SIGNING/password.txt")"

echo "Building $STAMP (code $CODE)..."
dotnet publish src/SAFT.Droid/SAFT.Droid.csproj \
    -c Release \
    -f net8.0-android34.0 \
    -p:AndroidSdkDirectory="$ANDROID_HOME" \
    -p:JavaSdkDirectory="$JAVA_HOME" \
    -p:ApplicationDisplayVersion="$STAMP" \
    -p:ApplicationVersion="$CODE" \
    -p:AndroidKeyStore=true \
    -p:AndroidSigningKeyStore="$KEYSTORE" \
    -p:AndroidSigningKeyAlias=saft \
    -p:AndroidSigningStorePass="$STOREPASS" \
    -p:AndroidSigningKeyPass="$STOREPASS" \
    --nologo

# Intermediates live on the Mac's internal disk rather than this volume - see Directory.Build.props
# for why - so the APK has to be fetched back out to sit next to the source it came from.
BUILT="$OUT/publish/com.divinakra.saft-Signed.apk"

mkdir -p apk
cp "$BUILT" apk/SAFT.apk

# macOS drops a "._" companion file next to anything it copies onto this volume. Nobody wants that
# in the folder they are about to hand to a phone.
rm -f apk/._SAFT.apk

# Last line of defence: read the version back OUT of the finished APK and check it is the one this
# run just built. If the copy went wrong again, this says so here rather than on the device.
PACKAGED="$("$ANDROID_HOME/build-tools/34.0.0/aapt2" dump badging apk/SAFT.apk 2>/dev/null \
    | sed -n "s/.*versionName='\([^']*\)'.*/\1/p")"

if [ "$PACKAGED" != "$STAMP" ]; then
    echo >&2
    echo "STALE APK: built '$STAMP' but apk/SAFT.apk says '$PACKAGED'. Not shipping this." >&2
    exit 1
fi

# And check it went out under the release key rather than the debug one. The build falls back to the
# debug key silently if the signing properties do not take, and a debug-signed APK that reaches
# users is a mistake that cannot be corrected later - only replaced by making everyone uninstall.
SIGNER="$("$ANDROID_HOME/build-tools/34.0.0/apksigner" verify --print-certs apk/SAFT.apk 2>/dev/null \
    | sed -n 's/^Signer #1 certificate DN: //p')"

case "$SIGNER" in
    *"Android Debug"*|"")
        echo >&2
        echo "WRONG KEY: apk/SAFT.apk is signed by '${SIGNER:-nothing}', not the release key." >&2
        exit 1
        ;;
esac

# And check the app's own classes are actually IN it.
#
# This is the guard that six broken builds went out for want of. An APK can be well-formed, correctly
# versioned and correctly signed while containing none of the code that makes it an app - and nothing
# else in this script noticed, because everything else it checks was true.
DEXDIR="$(mktemp -d)"
unzip -o -q apk/SAFT.apk 'classes*.dex' -d "$DEXDIR" 2>/dev/null

# grep -c rather than grep -q, and the count captured rather than tested through a pipeline.
# `strings | grep -q` looks obviously correct and is wrong here: grep -q exits at the first match,
# strings takes SIGPIPE, and `set -o pipefail` turns that success into a failed pipeline - so this
# guard reported an empty APK for a perfectly good one. grep -c reads its input to the end.
FOUND=0
for dex in "$DEXDIR"/classes*.dex; do
    [ -f "$dex" ] || continue
    HITS="$(strings -a "$dex" | grep -c "MainActivity" || true)"
    if [ "$HITS" -gt 0 ]; then FOUND=1; break; fi
done

rm -rf "$DEXDIR"

if [ "$FOUND" -ne 1 ]; then
    echo >&2
    echo "EMPTY APK: apk/SAFT.apk contains no MainActivity. The Java stubs did not make it into" >&2
    echo "the dex. Delete $HOME/saft-android-build/SAFT.Droid/obj entirely and build again." >&2
    exit 1
fi

echo
echo "Done: $(pwd)/apk/SAFT.apk  ($(du -h apk/SAFT.apk | cut -f1))  build $STAMP"
