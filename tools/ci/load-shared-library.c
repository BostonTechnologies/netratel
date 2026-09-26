#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv)
{
    if (argc != 2 || argv[1][0] == '\0')
    {
        fprintf(stderr, "usage: %s library-name\n", argv[0]);
        return EXIT_FAILURE;
    }

    dlerror();
    void *handle = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (handle == NULL)
    {
        const char *error = dlerror();
        fprintf(stderr, "dlopen(%s) failed: %s\n", argv[1], error == NULL ? "unknown error" : error);
        return EXIT_FAILURE;
    }

    printf("dlopen(%s) succeeded\n", argv[1]);
    if (dlclose(handle) != 0)
    {
        const char *error = dlerror();
        fprintf(stderr, "dlclose(%s) failed: %s\n", argv[1], error == NULL ? "unknown error" : error);
        return EXIT_FAILURE;
    }

    return EXIT_SUCCESS;
}
