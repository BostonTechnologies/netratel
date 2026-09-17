// A terminal attachment lease is intentionally renewed only after this code
// runs in a real browser. Blazor Server can retain a circuit after the page has
// disappeared, so server-side polling/SSE activity is not attachment evidence.
const heartbeats = new Map();
const heartbeatIntervalMilliseconds = 30_000;
const heartbeatAttemptTimeoutMilliseconds = 12_000;
let nextHeartbeatId = 0;

export function startTerminalAttachmentHeartbeat(dotNetObjectReference, sessionId, generation, attachmentLeaseId) {
    if (!dotNetObjectReference || !sessionId || !generation || !attachmentLeaseId) {
        throw new Error('A terminal attachment heartbeat needs a callback, session ID, generation, and lease fence.');
    }

    const heartbeatId = ++nextHeartbeatId;
    const entry = {
        dotNetObjectReference,
        sessionId,
        generation,
        attachmentLeaseId,
        browserAttachmentId: createBrowserAttachmentId(),
        claimOwnership: true,
        intervalId: null,
        inFlight: null,
        attempt: 0,
        paused: false,
        activationInFlight: false,
        activationAttempt: 0,
        onBeforeUnload: () => stopTerminalAttachmentHeartbeat(heartbeatId),
        onPageHide: null,
        onPageShow: null,
        onBrowserActive: null
    };

    entry.onPageHide = event => {
        if (event.persisted) {
            entry.paused = true;
            stopHeartbeatTimer(entry);
            // Do not let a pre-BFCache callback suppress the first pageshow
            // renewal if its promise never settles while the document is frozen.
            entry.attempt += 1;
            entry.inFlight = null;
            entry.activationAttempt += 1;
            entry.activationInFlight = false;
            return;
        }

        stopTerminalAttachmentHeartbeat(heartbeatId);
    };
    entry.onPageShow = event => {
        if (event.persisted && heartbeats.get(heartbeatId) === entry) {
            entry.paused = false;
            startHeartbeatTimer(heartbeatId, entry, true);
            void invokeBrowserActive(heartbeatId);
        }
    };
    entry.onBrowserActive = () => { void invokeBrowserActive(heartbeatId); };

    heartbeats.set(heartbeatId, entry);
    startHeartbeatTimer(heartbeatId, entry, true);

    // Timers naturally cease with a destroyed document, and these handlers
    // additionally release references for navigation/reload paths that keep a
    // Blazor circuit alive long enough for component disposal to be delayed.
    // A BFCache page is only paused and restarts its browser-owned timer on
    // pageshow; a true unload removes the entry entirely.
    window.addEventListener('pagehide', entry.onPageHide);
    window.addEventListener('pageshow', entry.onPageShow);
    window.addEventListener('beforeunload', entry.onBeforeUnload, { once: true });
    window.addEventListener('focus', entry.onBrowserActive);
    document.addEventListener('visibilitychange', entry.onBrowserActive);
    return heartbeatId;
}

export function stopTerminalAttachmentHeartbeat(heartbeatId) {
    const entry = heartbeats.get(heartbeatId);
    if (!entry) {
        return;
    }

    stopHeartbeatTimer(entry);
    window.removeEventListener('pagehide', entry.onPageHide);
    window.removeEventListener('pageshow', entry.onPageShow);
    window.removeEventListener('beforeunload', entry.onBeforeUnload);
    window.removeEventListener('focus', entry.onBrowserActive);
    document.removeEventListener('visibilitychange', entry.onBrowserActive);
    heartbeats.delete(heartbeatId);
}

function startHeartbeatTimer(heartbeatId, entry, invokeImmediately) {
    if (entry.intervalId !== null || heartbeats.get(heartbeatId) !== entry) {
        return;
    }

    entry.intervalId = window.setInterval(() => {
        void invokeHeartbeat(heartbeatId);
    }, heartbeatIntervalMilliseconds);
    if (invokeImmediately) {
        void invokeHeartbeat(heartbeatId);
    }
}

function stopHeartbeatTimer(entry) {
    if (entry.intervalId !== null) {
        window.clearInterval(entry.intervalId);
        entry.intervalId = null;
    }
}

function invokeHeartbeat(heartbeatId) {
    const entry = heartbeats.get(heartbeatId);
    if (!entry || entry.paused) {
        return Promise.resolve();
    }
    if (entry.inFlight) {
        return entry.inFlight;
    }

    const attempt = ++entry.attempt;
    entry.inFlight = runHeartbeat(heartbeatId, entry, attempt);
    return entry.inFlight;
}

async function runHeartbeat(heartbeatId, entry, attempt) {
    try {
        const outcome = await awaitHeartbeatOutcome(entry);
        if (heartbeats.get(heartbeatId) === entry && entry.attempt === attempt) {
            if (outcome.attachmentLeaseId) {
                entry.attachmentLeaseId = outcome.attachmentLeaseId;
            }
            if (outcome.ownershipConfirmed) {
                entry.claimOwnership = false;
            }
        }
        if (outcome.shouldContinue === false &&
            heartbeats.get(heartbeatId) === entry &&
            entry.attempt === attempt) {
            stopTerminalAttachmentHeartbeat(heartbeatId);
        }
    } finally {
        if (heartbeats.get(heartbeatId) === entry && entry.attempt === attempt) {
            entry.inFlight = null;
        }
    }
}

async function invokeBrowserActive(heartbeatId) {
    const entry = heartbeats.get(heartbeatId);
    if (!entry || entry.paused || entry.activationInFlight || document.visibilityState !== 'visible') {
        return;
    }

    entry.activationInFlight = true;
    const attempt = ++entry.activationAttempt;
    try {
        // A browser ownership claim may rotate the lease. Reconcile only after
        // that renewal settles, using its resulting fence rather than the
        // lease captured before a tab/circuit was restored.
        await invokeHeartbeat(heartbeatId);
        if (heartbeats.get(heartbeatId) !== entry || entry.paused ||
            entry.activationAttempt !== attempt || document.visibilityState !== 'visible') {
            return;
        }

        await awaitCallbackOutcome(() => entry.dotNetObjectReference.invokeMethodAsync(
            'OnGatewayTerminalBrowserActive',
            entry.sessionId,
            entry.generation,
            entry.attachmentLeaseId));
    } finally {
        if (heartbeats.get(heartbeatId) === entry && entry.activationAttempt === attempt) {
            entry.activationInFlight = false;
        }
    }
}

async function awaitHeartbeatOutcome(entry) {
    return awaitCallbackOutcome(() => entry.dotNetObjectReference.invokeMethodAsync(
        'OnGatewayTerminalAttachmentHeartbeat',
        entry.sessionId,
        entry.generation,
        entry.attachmentLeaseId,
        entry.browserAttachmentId,
        entry.claimOwnership));
}

async function awaitCallbackOutcome(invoke) {
    let deadlineId;
    const callback = Promise.resolve().then(invoke)
        .then(normalizeHeartbeatResult, () => ({ shouldContinue: true }));
    const deadline = new Promise(resolve => {
        deadlineId = window.setTimeout(() => resolve({ shouldContinue: true }), heartbeatAttemptTimeoutMilliseconds);
    });

    const outcome = await Promise.race([callback, deadline]);
    window.clearTimeout(deadlineId);

    // A circuit reconnect or an overloaded server is not proof the browser is
    // gone. Leave the JS timer running; a later browser-executed attempt can
    // renew before the bounded lease expires. Only an explicit false from .NET
    // (stale generation, close-pending, final, or disposal) stops this owner.
    return outcome;
}

function normalizeHeartbeatResult(result) {
    // Retain a safe retry default if a newer client and an older callback are
    // briefly out of sync during deployment or circuit restoration.
    if (!result || typeof result !== 'object') {
        return { shouldContinue: result !== false };
    }

    return {
        shouldContinue: (result.shouldContinue ?? result.ShouldContinue) !== false,
        ownershipConfirmed: (result.ownershipConfirmed ?? result.OwnershipConfirmed) === true,
        attachmentLeaseId: typeof (result.attachmentLeaseId ?? result.AttachmentLeaseId) === 'string'
            ? (result.attachmentLeaseId ?? result.AttachmentLeaseId)
            : undefined
    };
}

function createBrowserAttachmentId() {
    if (typeof globalThis.crypto?.randomUUID === 'function') {
        return globalThis.crypto.randomUUID();
    }

    if (typeof globalThis.crypto?.getRandomValues === 'function') {
        const words = new Uint32Array(4);
        globalThis.crypto.getRandomValues(words);
        return Array.from(words, word => word.toString(16).padStart(8, '0')).join('');
    }

    // The API also requires an authenticated session and an exact server fence;
    // this fallback only preserves per-runtime separation on older browsers.
    return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}
