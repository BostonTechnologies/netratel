#include <pty.h>
#include <unistd.h>

/*
 * The CLR is multithreaded before this call.  Do not return into managed code
 * in the forked child: execute the prepared argv immediately instead.
 */
int netratel_forkpty_exec(
    int *master_fd,
    const char *executable,
    char *const argv[],
    const struct winsize *size)
{
    struct winsize initial_size = *size;
    pid_t pid = forkpty(master_fd, NULL, NULL, &initial_size);
    if (pid == 0)
    {
        execv(executable, argv);
        _exit(127);
    }

    return (int)pid;
}
