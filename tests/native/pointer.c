/* Drive the isolated seat through the compositor's virtual pointer protocol. */
#include <stdio.h>
#include <string.h>
#include <time.h>
#include <wayland-client.h>
#include "virtual-pointer.h"

static struct zwlr_virtual_pointer_manager_v1 *manager;

static void global(void *data, struct wl_registry *registry, unsigned int name,
                   const char *interface, unsigned int version) {
    (void)data;
    (void)version;
    if (strcmp(interface, "zwlr_virtual_pointer_manager_v1") == 0)
        manager = wl_registry_bind(registry, name,
                                   &zwlr_virtual_pointer_manager_v1_interface, 1);
}

static void removed(void *data, struct wl_registry *registry, unsigned int name) {
    (void)data;
    (void)registry;
    (void)name;
}

int main(void) {
    struct wl_display *display = wl_display_connect(NULL);
    if (display == NULL) {
        fputs("Cannot connect the test pointer to Wayland\n", stderr);
        return 1;
    }
    struct wl_registry *registry = wl_display_get_registry(display);
    const struct wl_registry_listener listener = {global, removed};
    wl_registry_add_listener(registry, &listener, NULL);
    if (wl_display_roundtrip(display) < 0 || manager == NULL) {
        fputs("The test compositor has no virtual pointer protocol\n", stderr);
        return 1;
    }
    struct zwlr_virtual_pointer_v1 *pointer =
        zwlr_virtual_pointer_manager_v1_create_virtual_pointer(manager, NULL);
    if (wl_display_roundtrip(display) < 0) {
        fputs("The compositor did not create the test pointer\n", stderr);
        return 1;
    }
    puts("ready");
    fflush(stdout);
    char command[256];
    unsigned int sequence = 0;
    while (fgets(command, sizeof(command), stdin) != NULL) {
        unsigned int x, y, width, height;
        struct timespec now;
        if (clock_gettime(CLOCK_MONOTONIC, &now) < 0) {
            perror("Cannot read the test pointer clock");
            return 1;
        }
        unsigned int milliseconds = (unsigned int)(now.tv_sec * 1000 + now.tv_nsec / 1000000);
        if (sscanf(command, "move %u %u %u %u", &x, &y, &width, &height) == 4 && width && height) {
            zwlr_virtual_pointer_v1_motion_absolute(pointer, milliseconds, x, y, width, height);
        } else if (strcmp(command, "press\n") == 0) {
            zwlr_virtual_pointer_v1_button(pointer, milliseconds, 272, WL_POINTER_BUTTON_STATE_PRESSED);
        } else if (strcmp(command, "release\n") == 0) {
            zwlr_virtual_pointer_v1_button(pointer, milliseconds, 272, WL_POINTER_BUTTON_STATE_RELEASED);
        } else {
            fputs("Invalid test pointer command\n", stderr);
            return 1;
        }
        zwlr_virtual_pointer_v1_frame(pointer);
        if (wl_display_roundtrip(display) < 0) {
            fputs("The compositor rejected a test pointer event\n", stderr);
            return 1;
        }
        printf("done %u\n", ++sequence);
        fflush(stdout);
    }
    zwlr_virtual_pointer_v1_destroy(pointer);
    zwlr_virtual_pointer_manager_v1_destroy(manager);
    wl_registry_destroy(registry);
    wl_display_flush(display);
    wl_display_disconnect(display);
    return 0;
}
