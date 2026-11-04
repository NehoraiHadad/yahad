#!/usr/bin/env bash
# Install the pinned Node on a hosted arm64 runner, at /usr/local like the Pi.
#
# The hosted image already has Node 20 there, installed with n. Unpacking over it
# merges two npm trees, and that npm dies at its first real command with "Class
# extends value undefined". So the old global packages and headers go first.
set -euo pipefail

archive="$RUNNER_TEMP/node.tar.xz"
curl -fsSL "https://nodejs.org/dist/$NODE_VERSION/node-$NODE_VERSION-linux-arm64.tar.xz" -o "$archive"
echo "$NODE_TARBALL_SHA256  $archive" | sha256sum --check --strict
sudo rm -rf /usr/local/lib/node_modules /usr/local/include/node
sudo tar -xJf "$archive" -C /usr/local --strip-components=1 --no-same-owner
test "$(/usr/local/bin/node --version)" = "$NODE_VERSION"
echo "npm $(PATH="/usr/local/bin:$PATH" /usr/local/bin/npm --version)"
