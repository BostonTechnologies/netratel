// An inline MudDialog closes asynchronously. Its native focus trap must leave
// the DOM before restoring a launcher that was disabled during detail loading.
export function waitForDismissal(bodyId) {
    const dialog = document.getElementById(bodyId)?.closest(".mud-dialog");
    if (!dialog) return Promise.resolve();

    return new Promise(resolve => {
        const observer = new MutationObserver(() => {
            if (!dialog.isConnected) {
                observer.disconnect();
                resolve();
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });
        if (!dialog.isConnected) {
            observer.disconnect();
            resolve();
        }
    });
}

export function restoreFocus(...elementIds) {
    for (const id of elementIds) {
        const element = id ? document.getElementById(id) : null;
        if (!element || !element.isConnected || element.matches(":disabled, [aria-disabled='true']") ||
            element.getClientRects().length === 0) continue;

        element.focus({ preventScroll: true });
        if (document.activeElement === element) return;
    }
}
