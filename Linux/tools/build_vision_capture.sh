#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:?Provide an owned capture helper output directory}"
[[ ! -e "$OUT" ]] || { echo 'Refusing existing capture helper output'; exit 1; }
mkdir -p "$OUT"
PROTO="$(pkg-config --variable=pkgdatadir wayland-protocols)/staging/ext-foreign-toplevel-list/ext-foreign-toplevel-list-v1.xml"
wayland-scanner client-header "$PROTO" "$OUT/ext-foreign-toplevel-list-v1-client-protocol.h"
wayland-scanner private-code "$PROTO" "$OUT/ext-foreign-toplevel-list-v1-protocol.c"
MAPPING="$(pkg-config --variable=pkgdatadir hyprland-protocols)/protocols/hyprland-toplevel-mapping-v1.xml"
wayland-scanner client-header "$MAPPING" "$OUT/hyprland-toplevel-mapping-v1-client-protocol.h"
wayland-scanner private-code "$MAPPING" "$OUT/hyprland-toplevel-mapping-v1-protocol.c"
wayland-scanner private-code "$ROOT/tools/protocols/wlr-foreign-toplevel-management-unstable-v1.xml" \
    "$OUT/wlr-foreign-toplevel-management-unstable-v1-protocol.c"
cc -std=c11 -O2 -Wall -Wextra -Werror -I"$OUT" $(pkg-config --cflags wayland-client) \
    "$ROOT/tools/vision_toplevels.c" "$OUT/"*-protocol.c \
    $(pkg-config --libs wayland-client) -o "$OUT/vision-toplevels"
printf 'Built exact-window capture discovery helper: %s\n' "$OUT/vision-toplevels"
