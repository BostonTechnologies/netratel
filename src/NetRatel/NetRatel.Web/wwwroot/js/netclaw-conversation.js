const active = new WeakMap();
const displayKey = "netratel.chat.display";

export function readDisplay() {
    try {
        const value = JSON.parse(localStorage.getItem(displayKey) || "null");
        if (!value || typeof value !== "object" || Array.isArray(value)) return null;
        const fields = ["showTimestamps", "showCallIds", "expandDetails", "showUsage", "showProcessing", "showCompaction", "showSubagents"];
        return Object.fromEntries(fields.filter(key => typeof value[key] === "boolean").map(key => [key, value[key]]));
    }
    catch { return null; }
}
export function saveDisplay(value) {
    try { localStorage.setItem(displayKey, JSON.stringify(value)); }
    catch { /* Keep the live preference if browser storage is disabled. */ }
}

export function setFullscreen(surface, enabled, reference) {
    if (!surface) return;
    const previous = active.get(surface);
    const transcript = surface.querySelector(".netclaw-scroll-area");
    const scrollTop = transcript?.scrollTop;
    surface.classList.toggle("netclaw-fullscreen", enabled);
    if (transcript) requestAnimationFrame(() => { transcript.scrollTop = scrollTop; });
    if (!enabled) {
        if (!previous) return;
        document.removeEventListener("keydown", previous.keydown);
        document.removeEventListener("focusin", previous.focusin);
        previous.observer.disconnect();
        for (const [element, inert] of previous.background) element.inert = inert;
        document.body.style.overflow = previous.overflow;
        active.delete(surface);
        if (previous.trigger?.isConnected) previous.trigger.focus({ preventScroll: true });
        return;
    }
    if (previous) return;
    const background = [];
    // Inert the surrounding workspace. MudBlazor portal hosts remain available for nested dialogs/menus.
    for (let node = surface; node?.parentElement && node !== document.body; node = node.parentElement) {
        for (const sibling of node.parentElement.children) {
            if (sibling === node || sibling.tagName === "SCRIPT" || sibling.matches(".mud-popover-provider, .mud-dialog-provider") || sibling.querySelector(".mud-popover-provider, .mud-dialog-provider")) continue;
            background.push([sibling, sibling.inert]);
            sibling.inert = true;
        }
    }
    const nestedOverlay = () => Array.from(document.querySelectorAll(".mud-dialog-container, .mud-popover-open")).some(x => x.getClientRects().length > 0 && !x.matches(".mud-tooltip") && !x.querySelector('[role="tooltip"], .mud-tooltip'));
    const focusables = () => Array.from(surface.querySelectorAll('button:not([disabled]), a[href], input:not([disabled]), textarea:not([disabled]), select:not([disabled]), summary, [tabindex="0"]')).filter(x => x.getClientRects().length > 0);
    const keydown = event => {
        if (event.defaultPrevented || nestedOverlay()) return;
        if (event.key === "Escape") {
            const settings = surface.querySelector(".netclaw-display-panel");
            if (settings) { reference.invokeMethodAsync("CloseDisplaySettings"); surface.querySelector(".netclaw-display-trigger")?.focus(); event.preventDefault(); return; }
            event.preventDefault();
            reference.invokeMethodAsync("RestoreConversation");
        } else if (event.key === "Tab") {
            const items = focusables();
            const first = items[0], last = items[items.length - 1];
            if (!first) { event.preventDefault(); surface.focus(); }
            else if (event.shiftKey && (document.activeElement === first || !surface.contains(document.activeElement))) { event.preventDefault(); last.focus(); }
            else if (!event.shiftKey && (document.activeElement === last || !surface.contains(document.activeElement))) { event.preventDefault(); first.focus(); }
        }
    };
    const focusin = event => { if (!surface.contains(event.target) && !nestedOverlay()) (focusables()[0] || surface).focus(); };
    const observer = new MutationObserver(() => {
        if (!surface.isConnected) setFullscreen(surface, false, reference);
    });
    observer.observe(document.body, { childList: true, subtree: true });
    active.set(surface, { background, keydown, focusin, observer, trigger: surface.querySelector('[data-testid="conversation-fullscreen"]') || document.activeElement, overflow: document.body.style.overflow });
    document.body.style.overflow = "hidden";
    document.addEventListener("keydown", keydown);
    document.addEventListener("focusin", focusin);
    requestAnimationFrame(() => surface.querySelector('[data-testid="conversation-fullscreen"]')?.focus({ preventScroll: true }));
}
