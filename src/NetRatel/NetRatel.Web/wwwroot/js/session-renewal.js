window.netratelSessionRenewal = {
    async renew(correlation) {
        try {
            const response = await fetch(new URL("auth/session/renew", document.baseURI), {
                method: "POST",
                credentials: "same-origin",
                cache: "no-store",
                redirect: "error",
                headers: { "X-NetRatel-Session-Renewal": correlation },
                signal: AbortSignal.timeout(45000)
            });
            return response.status === 204;
        } catch {
            // The server turns a failed renewal into its existing bounded 401
            // path. This bridge never retries or sends the pending API action.
            return false;
        }
    }
};
