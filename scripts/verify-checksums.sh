#!/bin/bash
# Verify JimsProxy release checksums
# Usage: ./verify-checksums.sh [download-directory]
#
# Downloads checksums-sha256.txt from the latest release and verifies
# all JimsProxy release archives in the specified directory (default: current dir).

set -e

DIR="${1:-.}"
REPO="jameopotato/jimsproxy"
CHECKSUMS="checksums-sha256.txt"

echo "=== JimsProxy Release Checksum Verifier ==="
echo ""

# Download checksums file if not present
if [ ! -f "$DIR/$CHECKSUMS" ]; then
    echo "Downloading $CHECKSUMS from latest release..."
    gh release download --repo "$REPO" --pattern "$CHECKSUMS" --dir "$DIR" 2>/dev/null \
        || curl -sL "$(gh release view --repo "$REPO" --json assets --jq ".assets[] | select(.name==\"$CHECKSUMS\") | .url")" -o "$DIR/$CHECKSUMS" 2>/dev/null
    if [ ! -f "$DIR/$CHECKSUMS" ]; then
        echo "ERROR: Could not download $CHECKSUMS"
        echo "Make sure 'gh' CLI is installed or download the file manually from:"
        echo "  https://github.com/$REPO/releases/latest"
        exit 1
    fi
fi

echo "Verifying checksums in: $DIR"
echo ""

cd "$DIR"
FAIL=0

while IFS='  ' read -r expected_hash filename; do
    # Skip empty lines
    [ -z "$filename" ] && continue

    if [ ! -f "$filename" ]; then
        echo "SKIP: $filename (not found)"
        continue
    fi

    actual_hash=$(sha256sum "$filename" | cut -d' ' -f1)
    if [ "$actual_hash" = "$expected_hash" ]; then
        echo "  OK: $filename"
    else
        echo "FAIL: $filename"
        echo "  Expected: $expected_hash"
        echo "  Actual:   $actual_hash"
        FAIL=1
    fi
done < "$CHECKSUMS"

echo ""
if [ "$FAIL" -eq 0 ]; then
    echo "All checksums verified successfully."
else
    echo "WARNING: One or more checksums did not match!"
    echo "The files may have been tampered with or corrupted during download."
    exit 1
fi
