#include <sys/ioctl.h>

/* Darwin arm64 passes variadic ioctl arguments on the stack. Keep the managed
 * entry point fixed-arity and let the native compiler perform that ABI call. */
int netratel_set_window_size(int fd, unsigned short rows, unsigned short cols)
{
    struct winsize size = { .ws_row = rows, .ws_col = cols, .ws_xpixel = 0, .ws_ypixel = 0 };
    return ioctl(fd, TIOCSWINSZ, &size);
}
