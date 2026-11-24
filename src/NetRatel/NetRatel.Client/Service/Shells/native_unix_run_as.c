#define _DEFAULT_SOURCE
#define _DARWIN_C_SOURCE
#include <errno.h>
#include <grp.h>
#include <inttypes.h>
#include <pwd.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

extern char **environ;

static int fail(void)
{
    fputs("The requested local account could not start the process.\n", stderr);
    return 126;
}

static int resolve(void)
{
    // The account may come from a secret reference. Take it only on stdin.
    char name[258] = { 0 };
    if (fgets(name, sizeof(name), stdin) == NULL) return fail();
    size_t length = strcspn(name, "\n");
    if (length == 0 || length > 256 || name[length] != '\n') return fail();
    name[length] = '\0';

    struct passwd *account = getpwnam(name);
    if (account == NULL) return fail();
    return printf("%" PRIuMAX " %" PRIuMAX "\n",
        (uintmax_t)account->pw_uid, (uintmax_t)account->pw_gid) < 0 ? fail() : 0;
}

static int parse_id(const char *text, uintmax_t *id)
{
    if (text[0] == '\0' || strspn(text, "0123456789") != strlen(text)) return 0;
    char *end;
    errno = 0;
    *id = strtoumax(text, &end, 10);
    return errno == 0 && *end == '\0' && *id < UINT32_MAX;
}

static int set_account_environment(const char *name, const char *home, const char *shell, int terminal)
{
    char **inherited = environ;
    static char *clean_environment[] = { NULL };
    environ = clean_environment;
    if (setenv("HOME", home, 1) != 0 ||
        setenv("USER", name, 1) != 0 ||
        setenv("LOGNAME", name, 1) != 0 ||
        setenv("SHELL", shell, 1) != 0 ||
        setenv("PATH", "/usr/local/bin:/usr/bin:/bin", 1) != 0 ||
        setenv("LANG", "C.UTF-8", 1) != 0) return 0;

    if (terminal) return setenv("TERM", "xterm-256color", 1) == 0;
    for (char **entry = inherited; entry != NULL && *entry != NULL; entry++)
    {
        static const char prefix[] = "NETRATEL_TARGET_PARAM_";
        if (strncmp(*entry, prefix, sizeof(prefix) - 1) == 0)
        {
            char *parameter = strdup(*entry + sizeof(prefix) - 1);
            if (parameter == NULL || putenv(parameter) != 0) return 0;
        }
    }
    return 1;
}

int main(int argc, char **argv)
{
    if (geteuid() != 0) return fail();
    if (argc == 2 && strcmp(argv[1], "--resolve") == 0) return resolve();

    if (argc < 6) return fail();
    int terminal = strcmp(argv[1], "--terminal") == 0;
    if (!terminal && strcmp(argv[1], "--job") != 0) return fail();
    uintmax_t uid, gid;
    if (!parse_id(argv[2], &uid) || !parse_id(argv[3], &gid) || argv[5][0] != '/') return fail();

    struct passwd *account = getpwuid((uid_t)uid);
    if (account == NULL || (uintmax_t)account->pw_gid != gid || account->pw_dir[0] != '/') return fail();
    const char *shell_name = strrchr(account->pw_shell, '/');
    shell_name = shell_name == NULL ? account->pw_shell : shell_name + 1;
    if (terminal && (strcmp(shell_name, "nologin") == 0 || strcmp(shell_name, "false") == 0)) return fail();

    char *name = strdup(account->pw_name);
    char *home = strdup(account->pw_dir);
    char *shell = strdup(account->pw_shell);
    if (name == NULL || home == NULL || shell == NULL) return fail();

    if (initgroups(name, (gid_t)gid) != 0 || setgid((gid_t)gid) != 0 || setuid((uid_t)uid) != 0 ||
        getuid() != (uid_t)uid || geteuid() != (uid_t)uid || getgid() != (gid_t)gid || getegid() != (gid_t)gid)
        return fail();

    if (chdir(argv[4][0] == '\0' ? home : argv[4]) != 0 ||
        access(argv[5], X_OK) != 0 || !set_account_environment(name, home, shell, terminal))
        return fail();
    if (fprintf(stderr, "netratel-run-as-ready %" PRIuMAX " %" PRIuMAX "\n", uid, gid) < 0 ||
        fflush(stderr) != 0) return fail();

    execv(argv[5], &argv[5]);
    return fail();
}
