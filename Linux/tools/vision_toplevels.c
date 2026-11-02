#define _POSIX_C_SOURCE 200809L
#include <stdbool.h>
#include <stdint.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <wayland-client.h>
#include "ext-foreign-toplevel-list-v1-client-protocol.h"
#include "hyprland-toplevel-mapping-v1-client-protocol.h"

struct window {
    struct ext_foreign_toplevel_handle_v1 *handle;
    struct hyprland_toplevel_window_mapping_handle_v1 *mapping;
    uint64_t address;
    struct window *next;
    char *identifier, *title, *app_id;
    bool closed, complete;
};
struct state {
    struct ext_foreign_toplevel_list_v1 *list;
    struct hyprland_toplevel_mapping_manager_v1 *mapping;
    struct window *windows;
    bool capability_only, has_list, has_source, has_copy, has_mapping;
};
static void replace(char **field, const char *value) {
    char *copy = strdup(value);
    if (!copy) exit(2);
    free(*field); *field = copy;
}
static void closed(void *data, struct ext_foreign_toplevel_handle_v1 *handle) {
    (void)handle; ((struct window *)data)->closed = true;
}
static void done(void *data, struct ext_foreign_toplevel_handle_v1 *handle) {
    (void)handle; ((struct window *)data)->complete = true;
}
static void title(void *data, struct ext_foreign_toplevel_handle_v1 *handle, const char *value) {
    (void)handle; replace(&((struct window *)data)->title, value);
}
static void app_id(void *data, struct ext_foreign_toplevel_handle_v1 *handle, const char *value) {
    (void)handle; replace(&((struct window *)data)->app_id, value);
}
static void identifier(void *data, struct ext_foreign_toplevel_handle_v1 *handle, const char *value) {
    (void)handle; replace(&((struct window *)data)->identifier, value);
}
static const struct ext_foreign_toplevel_handle_v1_listener handle_listener = {
    .closed = closed, .done = done, .title = title, .app_id = app_id, .identifier = identifier
};
static void mapped(void *data, struct hyprland_toplevel_window_mapping_handle_v1 *handle,
                   uint32_t high, uint32_t low) {
    (void)handle; ((struct window *)data)->address = ((uint64_t)high << 32) | low;
}
static void mapping_failed(void *data, struct hyprland_toplevel_window_mapping_handle_v1 *handle) {
    (void)handle; ((struct window *)data)->address = 0;
}
static const struct hyprland_toplevel_window_mapping_handle_v1_listener mapping_listener = {
    .window_address = mapped, .failed = mapping_failed
};
static void toplevel(void *data, struct ext_foreign_toplevel_list_v1 *list,
                     struct ext_foreign_toplevel_handle_v1 *handle) {
    (void)list;
    struct state *state = data;
    struct window *window = calloc(1, sizeof(*window));
    if (!window) exit(2);
    window->handle = handle; window->next = state->windows; state->windows = window;
    ext_foreign_toplevel_handle_v1_add_listener(handle, &handle_listener, window);
}
static void finished(void *data, struct ext_foreign_toplevel_list_v1 *list) {
    (void)data; (void)list;
}
static const struct ext_foreign_toplevel_list_v1_listener list_listener = {
    .toplevel = toplevel, .finished = finished
};
static void global(void *data, struct wl_registry *registry, uint32_t name,
                   const char *interface, uint32_t version) {
    (void)version;
    struct state *state = data;
    if (!strcmp(interface, "ext_foreign_toplevel_list_v1")) {
        state->has_list = true;
        if (!state->capability_only) {
            state->list = wl_registry_bind(registry, name, &ext_foreign_toplevel_list_v1_interface, 1);
            ext_foreign_toplevel_list_v1_add_listener(state->list, &list_listener, state);
        }
    }
    if (!strcmp(interface, "hyprland_toplevel_mapping_manager_v1")) {
        state->has_mapping = true;
        if (!state->capability_only)
            state->mapping = wl_registry_bind(registry, name, &hyprland_toplevel_mapping_manager_v1_interface, 1);
    }
    if (!strcmp(interface, "ext_foreign_toplevel_image_capture_source_manager_v1")) state->has_source = true;
    if (!strcmp(interface, "ext_image_copy_capture_manager_v1")) state->has_copy = true;
}
static void removed(void *data, struct wl_registry *registry, uint32_t name) {
    (void)data; (void)registry; (void)name;
}
static const struct wl_registry_listener registry_listener = { .global = global, .global_remove = removed };
static void string(const char *value) {
    putchar('"');
    for (const unsigned char *p = (const unsigned char *)(value ? value : ""); *p; ++p) {
        if (*p == '"' || *p == '\\') { putchar('\\'); putchar(*p); }
        else if (*p < 32) printf("\\u%04x", *p);
        else putchar(*p);
    }
    putchar('"');
}
int main(int argc, char **argv) {
    struct state state = {0};
    if (argc == 2 && !strcmp(argv[1], "--capabilities")) state.capability_only = true;
    else if (argc != 1) return 2;
    alarm(4);
    struct wl_display *display = wl_display_connect(NULL);
    if (!display) return 3;
    struct wl_registry *registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &registry_listener, &state);
    if (wl_display_roundtrip(display) < 0) return 3;
    if (state.capability_only) {
        printf("{\"toplevel_list\":%s,\"capture_source\":%s,\"copy_capture\":%s,\"address_mapping\":%s}\n",
            state.has_list ? "true" : "false", state.has_source ? "true" : "false",
            state.has_copy ? "true" : "false", state.has_mapping ? "true" : "false");
    } else {
        if (!state.list || !state.mapping || !state.has_source || !state.has_copy) {
            fputs("Exact window capture protocols unavailable\n", stderr); return 77;
        }
        if (wl_display_roundtrip(display) < 0) return 3;
        for (struct window *w = state.windows; w; w = w->next) {
            if (w->closed) continue;
            w->mapping = hyprland_toplevel_mapping_manager_v1_get_window_for_toplevel(state.mapping, w->handle);
            hyprland_toplevel_window_mapping_handle_v1_add_listener(w->mapping, &mapping_listener, w);
        }
        if (wl_display_roundtrip(display) < 0) return 3;
        for (struct window *w = state.windows; w; w = w->next) {
            if (w->closed || !w->complete || !w->address || !w->identifier || !w->app_id || !w->title) continue;
            fputs("{\"identifier\":", stdout); string(w->identifier);
            printf(",\"address\":\"0x%" PRIx64 "\"", w->address);
            fputs(",\"app_id\":", stdout); string(w->app_id);
            fputs(",\"title\":", stdout); string(w->title); puts("}");
        }
        ext_foreign_toplevel_list_v1_stop(state.list);
        wl_display_roundtrip(display);
    }
    while (state.windows) {
        struct window *w = state.windows; state.windows = w->next;
        if (w->mapping) hyprland_toplevel_window_mapping_handle_v1_destroy(w->mapping);
        ext_foreign_toplevel_handle_v1_destroy(w->handle);
        free(w->identifier); free(w->title); free(w->app_id); free(w);
    }
    if (state.list) ext_foreign_toplevel_list_v1_destroy(state.list);
    if (state.mapping) hyprland_toplevel_mapping_manager_v1_destroy(state.mapping);
    wl_registry_destroy(registry); wl_display_disconnect(display);
    return 0;
}
