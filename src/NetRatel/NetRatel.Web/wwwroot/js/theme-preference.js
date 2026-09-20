window.netratelThemePreference = (() => {
    const storageKey = "netratel.theme.preference";

    const normalize = (value) => {
        const mode = typeof value === "string" ? value.trim().toLowerCase() : "";
        return mode === "light" || mode === "dark" || mode === "system" ? mode : "system";
    };

    const read = () => {
        try {
            return normalize(window.localStorage.getItem(storageKey));
        } catch {
            return "system";
        }
    };

    const systemIsDark = () => {
        try {
            return Boolean(window.matchMedia?.("(prefers-color-scheme: dark)").matches);
        } catch {
            return false;
        }
    };

    const getResolvedMode = () => {
        const stored = read();
        return stored === "system" ? (systemIsDark() ? "dark" : "light") : stored;
    };

    const apply = (isDarkMode) => {
        const mode = isDarkMode ? "dark" : "light";
        document.documentElement.dataset.netratelTheme = mode;
        document.documentElement.style.colorScheme = mode;
    };

    apply(getResolvedMode() === "dark");

    return {
        get: read,
        set: (value) => {
            const mode = normalize(value);
            try {
                if (mode === "system") {
                    window.localStorage.removeItem(storageKey);
                } else {
                    window.localStorage.setItem(storageKey, mode);
                }
            } catch {
                // Storage is optional presentation state; the current document still updates.
            }

            apply(getResolvedMode() === "dark");
        },
        snapshot: () => ({ mode: read(), isDark: getResolvedMode() === "dark" }),
        apply,
    };
})();
