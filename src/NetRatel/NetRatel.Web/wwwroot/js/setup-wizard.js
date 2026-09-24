window.netratelSetup = {
    progressKey: "netratel.first-setup-progress.v1",
    initializeInFlight: false,
    pollInFlight: false,
    stopPolling: false,
    progressStartedAt: 0,
    accepted: false,
    canReviewDetails: false,
    setProgress: (title, status, detail, allowCheck = false) => {
        const view = document.getElementById("setup-initializing");
        if (!view) return;
        view.hidden = false;
        const update = (id, value) => {
            const node = document.getElementById(id);
            if (node && node.textContent !== value) node.textContent = value;
        };
        update("setup-initializing-title", title);
        update("setup-initializing-status", status);
        update("setup-initializing-detail", detail);
        const check = document.getElementById("setup-check-again");
        if (check) check.hidden = !allowCheck;
        const review = document.getElementById("setup-review-details");
        if (review) review.hidden = !window.netratelSetup.canReviewDetails;
    },
    saveProgress: () => {
        // Only a short-lived, non-secret marker survives navigation or a Web restart.
        try {
            sessionStorage.setItem(window.netratelSetup.progressKey, JSON.stringify({
                expiresAt: Date.now() + 30 * 60 * 1000,
                accepted: window.netratelSetup.accepted
            }));
        } catch { /* A live tab can still complete without session storage. */ }
    },
    request: async (url, options = {}, timeoutMs = 6000) => {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), timeoutMs);
        try {
            return await fetch(url, { credentials: "same-origin", cache: "no-store", ...options, signal: controller.signal });
        } finally {
            clearTimeout(timer);
        }
    },
    readSetupStatus: async () => {
        const response = await window.netratelSetup.request("/api/v2/setup/status");
        if (!response.ok || !(response.headers.get("content-type") || "").includes("application/json"))
            throw new Error("setup status unavailable");
        const status = await response.json();
        if (typeof status?.isReady !== "boolean" || typeof status?.isRecoveryRequired !== "boolean")
            throw new Error("invalid setup status");
        return status;
    },
    pollSetup: async () => {
        const setup = window.netratelSetup;
        if (setup.pollInFlight) return;
        setup.pollInFlight = true;
        let attempt = 0;
        setup.progressStartedAt ||= Date.now();
        try {
            while (!setup.stopPolling && Date.now() - setup.progressStartedAt < 10 * 60 * 1000) {
                try {
                    const status = await setup.readSetupStatus();
                    if (setup.stopPolling) return;
                    if (status.isRecoveryRequired) {
                        setup.setProgress("Setup recovery required", "NetRatel needs recovery before sign-in.",
                            "Restore the matching database, bootstrap state, and key material. Setup will not create another administrator.", true);
                        return;
                    }
                    if (status.isReady) {
                        setup.accepted = true;
                        setup.saveProgress();
                        setup.setProgress("NetRatel is ready", "Opening sign-in…", "Your setup is complete.");
                        // A ready API can precede the Web login route during a restart.
                        const login = await setup.request("/login", { redirect: "manual" });
                        if (login.ok && (login.headers.get("content-type") || "").includes("text/html")) {
                            window.location.replace("/login");
                            return;
                        }
                    }
                    setup.canReviewDetails = !setup.accepted && status.state === 1 && Date.now() - setup.progressStartedAt >= 60_000;
                    if (setup.accepted) {
                        setup.setProgress("System initializing first setup", "Waiting for services to become ready.",
                            "Your administrator account has been created. NetRatel is preparing your instance. Please be patient; we’ll take you to sign in when it is ready.");
                    } else {
                        setup.setProgress("Checking first setup", "Checking whether your setup was saved.",
                            "The response was interrupted. Do not submit the administrator details again while NetRatel checks its committed state.");
                    }
                } catch {
                    if (setup.stopPolling) return;
                    setup.setProgress(setup.accepted ? "System initializing first setup" : "Checking first setup",
                        "Waiting for NetRatel to reconnect.",
                        "The API is temporarily unavailable. Your setup may already be saved. Do not submit it again.");
                }
                if (Date.now() - setup.progressStartedAt >= 60_000) {
                    setup.setProgress(setup.accepted ? "System initializing first setup" : "Checking first setup",
                        "Still waiting for NetRatel to become ready.",
                        "Your setup may already be saved. Do not submit it again. Check the API service, storage, and setup status if this continues.", true);
                }
                const delay = Math.min(15000, 1500 * 2 ** Math.min(attempt++, 4));
                await new Promise(resolve => setTimeout(resolve, delay + Math.floor(Math.random() * 400)));
            }
            if (setup.stopPolling) return;
            setup.setProgress("Check first setup", "NetRatel has not become ready yet.",
                "Check the API service, storage, and setup status. Use Check again after addressing the problem; do not submit the administrator details again.", true);
        } finally {
            setup.pollInFlight = false;
        }
    },
    resumeProgress: () => {
        if (!document.getElementById("setup-initializing")) return;
        let marker;
        try { marker = JSON.parse(sessionStorage.getItem(window.netratelSetup.progressKey) || "null"); } catch { marker = null; }
        if (!marker || !Number.isFinite(marker.expiresAt) || marker.expiresAt < Date.now()) return;
        const setup = window.netratelSetup;
        setup.accepted = marker.accepted === true;
        setup.stopPolling = false;
        setup.progressStartedAt = Date.now();
        setup.setProgress(setup.accepted ? "System initializing first setup" : "Checking first setup",
            setup.accepted ? "Waiting for services to become ready." : "Checking whether your setup was saved.",
            "NetRatel is checking the committed setup state. Do not submit administrator details again.");
        void setup.pollSetup();
    },
    reviewDetails: async () => {
        const setup = window.netratelSetup;
        if (!setup.canReviewDetails || setup.initializeInFlight) return;
        try {
            const status = await setup.readSetupStatus();
            if (status.state !== 1 || status.isReady || status.isRecoveryRequired) {
                setup.progressStartedAt = Date.now();
                void setup.pollSetup();
                return;
            }
            setup.stopPolling = true;
            try { sessionStorage.removeItem(setup.progressKey); } catch { }
            document.getElementById("setup-initializing").hidden = true;
            const submit = document.querySelector("[data-testid='setup-initialize']");
            if (submit) submit.disabled = false;
            setup.showError("Setup is still configurable. Review the details and re-enter the passphrase before explicitly submitting again.");
        } catch {
            setup.canReviewDetails = false;
            setup.setProgress("Checking first setup", "Cannot confirm the current setup state.",
                "Check the API service and try Check again before reviewing or submitting details.", true);
        }
    },
    value: (selector) => document.querySelector(selector)?.value ?? "",
    clear: (...selectors) => selectors.forEach((selector) => {
        const input = document.querySelector(selector);
        if (input) input.value = "";
    }),
    post: async (url, body) => {
        let response;
        try {
            response = await fetch(url, {
                method: "POST",
                credentials: "same-origin",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(body)
            });
        } catch {
            return { ok: false, payload: null, message: null, status: 0 };
        }
        if (response.ok) {
            let payload = null;
            try { payload = await response.json(); } catch { }
            return { ok: true, payload, message: null, status: response.status };
        }
        let message = null;
        try {
            const problem = await response.json();
            message = problem?.title || problem?.detail || problem?.errors?.setup?.[0] || null;
        } catch { }
        return { ok: false, payload: null, message, status: response.status };
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
            const message = result.status === 0 || result.status >= 500
                ? "Sign-in is temporarily unavailable. Ask an administrator to check the API and shared session-key storage."
                : result.status === 429
                    ? "Too many sign-in attempts. Wait a moment before trying again."
                    : "The sign-in details were not accepted. Try again or contact an instance administrator.";
            window.netratelSetup.showLoginError(message);
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
            window.netratelSetup.showLoginError(result.status === 0 || result.status >= 500
                ? "Sign-in is temporarily unavailable. Ask an administrator to check the API and shared session-key storage."
                : "The authenticator or recovery code was not accepted.");
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
        const setup = window.netratelSetup;
        if (setup.initializeInFlight || setup.pollInFlight) return;
        const tenantName = window.netratelSetup.value("[data-testid='setup-tenant']");
        const displayName = window.netratelSetup.value("[data-testid='setup-display-name']");
        const email = window.netratelSetup.value("[data-testid='setup-email']");
        const password = window.netratelSetup.value("[data-testid='setup-password']");
        const confirmPassword = window.netratelSetup.value("[data-testid='setup-confirm-password']");
        if (!tenantName || !displayName || !email || password.length < 15 || password !== confirmPassword) {
            window.netratelSetup.showError("Provide the tenant, administrator details, and matching passphrase of at least 15 characters.");
            return;
        }
        setup.initializeInFlight = true;
        setup.progressStartedAt = Date.now();
        setup.accepted = false;
        setup.canReviewDetails = false;
        setup.stopPolling = false;
        setup.saveProgress();
        const submit = document.querySelector("[data-testid='setup-initialize']");
        if (submit) submit.disabled = true;
        setup.setProgress("Submitting your setup…", "Submitting administrator details.",
            "NetRatel is validating and saving your first setup.");
        try {
            const response = await setup.request("/api/v2/setup/initialize", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ tenantName, displayName, email, password })
            }, 30000);
            setup.clear("[data-testid='setup-password']", "[data-testid='setup-confirm-password']");
            if (response.ok) {
                setup.accepted = true;
                setup.saveProgress();
            } else if (response.status === 400 || response.status === 422) {
                let message = "Check your setup details and try again.";
                try {
                    const problem = await response.json();
                    message = problem?.errors?.setup?.[0] || problem?.title || message;
                } catch { }
                try { sessionStorage.removeItem(setup.progressKey); } catch { }
                document.getElementById("setup-initializing").hidden = true;
                if (submit) submit.disabled = false;
                setup.showError(message);
                return;
            }
        } catch {
            setup.clear("[data-testid='setup-password']", "[data-testid='setup-confirm-password']");
            // A lost response can follow a committed setup. Reconcile before offering any retry.
        } finally {
            setup.initializeInFlight = false;
        }
        void setup.pollSetup();
    }
};

document.addEventListener("DOMContentLoaded", () => window.netratelSetup.resumeProgress());
window.addEventListener("pageshow", event => {
    if (event.persisted) window.netratelSetup.resumeProgress();
});
document.addEventListener("click", event => {
    if (event.target?.id === "setup-check-again") {
        window.netratelSetup.progressStartedAt = Date.now();
        window.netratelSetup.stopPolling = false;
        void window.netratelSetup.pollSetup();
    }
    if (event.target?.id === "setup-review-details") void window.netratelSetup.reviewDetails();
});

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
