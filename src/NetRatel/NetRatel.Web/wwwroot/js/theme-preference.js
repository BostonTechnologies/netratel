window.netratelThemePreference = (() => {
    const storageKey = "netratel.theme.preference";

    const getResolvedMode = () => {
        const stored = window.localStorage.getItem(storageKey);
        if (stored === "light" || stored === "dark") {
            return stored;
        }

        return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    };

    const apply = (isDarkMode) => {
        const mode = isDarkMode ? "dark" : "light";
        document.documentElement.dataset.netratelTheme = mode;
        document.documentElement.style.colorScheme = mode;
    };

    apply(getResolvedMode() === "dark");

    return {
        get: () => window.localStorage.getItem(storageKey),
        set: (value) => {
            if (value === "system") {
                window.localStorage.removeItem(storageKey);
            } else {
                window.localStorage.setItem(storageKey, value);
            }

            apply(getResolvedMode() === "dark");
        },
        apply,
    };
})();
