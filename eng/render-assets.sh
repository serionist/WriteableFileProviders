#!/bin/sh
# Renders the PNGs in docs/assets from their SVGs: icon.png, the 128x128 package icon every package packs
# (src/Directory.Build.props), and social-preview.png, the 1280x640 image of the repository's social preview.
#
# Run from the repo root after changing an SVG, with Docker: sh eng/render-assets.sh
# librsvg renders, with the Inter font the wordmark names first, in a throwaway Alpine container.
set -eu
assets="$(cd "$(dirname "$0")/../docs/assets" && pwd)"
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) assets="$(cygpath -w "$assets")" ;; esac
MSYS_NO_PATHCONV=1 docker run --rm -v "$assets:/w" alpine:3.22 sh -c '
  set -eu
  apk add --no-cache rsvg-convert font-inter >/dev/null
  rsvg-convert -w 128 -h 128 /w/icon.svg -o /w/icon.png
  rsvg-convert -w 1280 -h 640 /w/social-preview.svg -o /w/social-preview.png
'
echo "Rendered icon.png and social-preview.png in docs/assets."
