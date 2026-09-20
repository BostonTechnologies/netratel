window.netratelSetup = {
    value: (selector) => document.querySelector(selector)?.value ?? "",
    clear: (...selectors) => selectors.forEach((selector) => {
        const input = document.querySelector(selector);
        if (input) input.value = "";
    }),
    post: async (url, body) => {
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body)
        });
        if (response.ok) {
            let payload = null;
            try { payload = await response.json(); } catch { }
            return { ok: true, payload, message: null };
        }
        let message = null;
        try {
            const problem = await response.json();
            message = problem?.title || problem?.detail || problem?.errors?.setup?.[0] || null;
        } catch { }
        return { ok: false, payload: null, message };
    },
    showError: (message) => {
        const target = document.getElementById("setup-client-error");
        if (target) {
            target.textContent = message;
            target.hidden = false;
        }
    },
    showLoginError: (message) => {
        const target = document.getElementById("local-login-client-error");
        if (target) {
            target.textContent = message;
            target.hidden = false;
        }
    },
    returnPath: () => {
        const value = new URLSearchParams(window.location.search).get("ReturnUrl");
        return value && value.startsWith("/") && !value.startsWith("//") ? value : "/";
    },
    localLogin: async () => {
        const email = window.netratelSetup.value("[data-testid='local-login-email']");
        const password = window.netratelSetup.value("[data-testid='local-login-password']");
        const rememberMe = document.querySelector("[data-testid='local-login-remember-me']")?.checked ?? false;
        if (!email || !password) {
            window.netratelSetup.showLoginError("Enter your email and password.");
            return;
        }
        const result = await window.netratelSetup.post("/api/v2/local-auth/login", { email, password, rememberMe });
        window.netratelSetup.clear("[data-testid='local-login-password']");
        if (!result.ok) {
            window.netratelSetup.showLoginError("The sign-in details were not accepted. Try again or contact an instance administrator.");
            return;
        }
        if (result.payload?.requiresTwoFactor) {
            window.location.assign(`/login?localMfa=true&ReturnUrl=${encodeURIComponent(window.netratelSetup.returnPath())}`);
            return;
        }
        window.location.assign(window.netratelSetup.returnPath());
    },
    completeTwoFactor: async () => {
        const code = window.netratelSetup.value("[data-testid='local-login-two-factor']");
        if (!code) {
            window.netratelSetup.showLoginError("Enter the authenticator or recovery code.");
            return;
        }
        const result = await window.netratelSetup.post("/api/v2/local-auth/login/two-factor", { code });
        window.netratelSetup.clear("[data-testid='local-login-two-factor']");
        if (!result.ok) {
            window.netratelSetup.showLoginError("The authenticator or recovery code was not accepted.");
            return;
        }
        window.location.assign(window.netratelSetup.returnPath());
    },
    claim: async () => {
        const proof = window.netratelSetup.value("[data-testid='setup-proof']");
        if (!proof) {
            window.netratelSetup.showError("Enter the setup code from the API host.");
            return;
        }
        const result = await window.netratelSetup.post("/api/v2/setup/claim", { proof });
        if (!result.ok) {
            window.netratelSetup.showError(result.message ?? "The setup code could not be accepted.");
            return;
        }
        window.location.reload();
    },
    initialize: async () => {
        const tenantName = window.netratelSetup.value("[data-testid='setup-tenant']");
        const displayName = window.netratelSetup.value("[data-testid='setup-display-name']");
        const email = window.netratelSetup.value("[data-testid='setup-email']");
        const password = window.netratelSetup.value("[data-testid='setup-password']");
        const confirmPassword = window.netratelSetup.value("[data-testid='setup-confirm-password']");
        if (!tenantName || !displayName || !email || password.length < 15 || password !== confirmPassword) {
            window.netratelSetup.showError("Provide the tenant, administrator details, and matching passphrase of at least 15 characters.");
            return;
        }
        const result = await window.netratelSetup.post("/api/v2/setup/initialize", { tenantName, displayName, email, password });
        window.netratelSetup.clear("[data-testid='setup-password']", "[data-testid='setup-confirm-password']");
        if (!result.ok) {
            window.netratelSetup.showError(result.message ?? "NetRatel could not complete setup.");
            return;
        }
        window.location.assign("/login");
    }
};

document.addEventListener("click", (event) => {
    const target = event.target instanceof Element ? event.target : event.target instanceof Node ? event.target.parentElement : null;
    const setupAction = target?.closest("[data-netratel-setup-action]")?.dataset.netratelSetupAction;
    const loginAction = target?.closest("[data-netratel-login-action]")?.dataset.netratelLoginAction;
    if (setupAction === "claim" || setupAction === "initialize") {
        event.preventDefault();
        void window.netratelSetup[setupAction]();
    }
    if (loginAction === "password") {
        event.preventDefault();
        void window.netratelSetup.localLogin();
    }
    if (loginAction === "two-factor") {
        event.preventDefault();
        void window.netratelSetup.completeTwoFactor();
    }
});
