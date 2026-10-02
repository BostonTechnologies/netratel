// Dialog.Result precedes the native provider's DOM removal and focus-trap teardown.
// Observe only this dialog; no timer or repeated focus attempt is needed.
export function waitForDismissal(dialogId) {
    const dialog = document.getElementById("_" + dialogId);
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
