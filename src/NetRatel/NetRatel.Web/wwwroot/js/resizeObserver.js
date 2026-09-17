// wwwroot/js/resizeObserver.js
const observers = new Map();

export function observeResize(element, dotNetObjectReference, callbackName = 'OnTerminalResize') {
    // Disconnect existing observer for this element if any
    unobserveResize(element);

    const observer = new ResizeObserver(() => {
        // Use requestAnimationFrame to avoid resize loops and batch calls
        window.requestAnimationFrame(async () => {
            if (observers.has(element)) { // Check if still observing
                 try {
                    await dotNetObjectReference.invokeMethodAsync(callbackName);
                 } catch (error) {
                    console.error("Error invoking .NET resize callback:", error);
                     unobserveResize(element);
                 }
            }
        });
    });

    observers.set(element, { observer, dotNetObjectReference });
    observer.observe(element);

    const viewport = window.visualViewport;
    const notifyViewport = () => {
        window.requestAnimationFrame(() => {
            if (observers.has(element)) {
                dotNetObjectReference.invokeMethodAsync(callbackName).catch(() => unobserveResize(element));
            }
        });
    };
    viewport?.addEventListener('resize', notifyViewport);
    viewport?.addEventListener('scroll', notifyViewport);
    observers.get(element).notifyViewport = notifyViewport;
}

export function unobserveResize(element) {
    if (observers.has(element)) {
        const existing = observers.get(element);
        existing.observer.disconnect();
        window.visualViewport?.removeEventListener('resize', existing.notifyViewport);
        window.visualViewport?.removeEventListener('scroll', existing.notifyViewport);
        observers.delete(element);
    }
}

export function isMobileViewport() {
    return window.matchMedia('(max-width: 600px)').matches;
}
