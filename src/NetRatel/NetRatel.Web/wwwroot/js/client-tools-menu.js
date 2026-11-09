let active;
export function attach(root, receiver, label) {
    const fab = root.querySelector('.mud-fab-menu-container');
    const button = fab?.querySelector(':scope > button');
    const menu = fab?.querySelector('.mud-fab-menu');
    if (!button || !menu) throw new Error('Client tools menu is unavailable');
    const id = `client-tools-${crypto.randomUUID()}`;
    menu.id = id; menu.setAttribute('aria-label', label); menu.setAttribute('role', 'menu');
    button.setAttribute('aria-label', label); button.setAttribute('aria-haspopup', 'menu'); button.setAttribute('aria-controls', id);
    let opened = false, disposed = false;
    function focus() { if (button.isConnected) button.focus({ preventScroll: true }); }
    function close(returnFocus = false) {
        if (!opened) return;
        opened = false; button.setAttribute('aria-expanded', 'false');
        if (menu.matches(':popover-open')) menu.hidePopover();
        menu.removeAttribute('popover'); menu.removeAttribute('style');
        if (active === controller) active = null;
        if (returnFocus) focus();
        if (!disposed) receiver.invokeMethodAsync('Dismiss').catch(() => {});
    }
    function place() {
        const bounds = button.getBoundingClientRect();
        const width = Math.min(260, innerWidth - 16), height = Math.min(menu.scrollHeight || 320, innerHeight - 24);
        const below = innerHeight - bounds.bottom - 8;
        const top = below >= height ? bounds.bottom - 1 : Math.max(8, bounds.top - height + 1);
        // Top-layer rendering escapes every overflow ancestor without moving the Blazor DOM.
        menu.style.cssText = `position:fixed;inset:auto;margin:0;left:${Math.max(8, Math.min(innerWidth - width - 8, bounds.right - width))}px;top:${top}px;width:${width}px;max-height:${innerHeight - top - 8}px;height:auto;padding:8px;display:flex;flex-direction:column;gap:6px;overflow:auto;pointer-events:auto;box-sizing:border-box;background:var(--mud-palette-surface);color:var(--mud-palette-text-primary);border:1px solid var(--mud-palette-divider);border-radius:8px;box-shadow:var(--mud-elevation-8);`;
    }
    function outside(event) { if (opened && !fab.contains(event.target)) close(); }
    function scroll(event) { if (opened && !menu.contains(event.target)) close(); }
    // Touch emits compatibility mouse-enter/leave events after its click in
    // Chromium. Pointer events retain real hover without closing a fresh tap.
    function hover(event) {
        if (!disposed && event.pointerType !== 'touch') receiver.invokeMethodAsync('Hover', event.type === 'pointerenter').catch(() => {});
    }
    function key(event) {
        if (!opened) {
            if (event.target === button && ['ArrowDown', 'ArrowUp'].includes(event.key)) { event.preventDefault(); button.click(); requestAnimationFrame(() => menu.querySelector('button:not(:disabled)')?.focus()); }
            return;
        }
        if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(true); return; }
        if (event.key === 'Tab') { close(); return; }
        const items = [...menu.querySelectorAll('button:not(:disabled)')];
        const current = items.indexOf(document.activeElement);
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 : event.key === 'ArrowDown' ? (current + 1) % items.length : event.key === 'ArrowUp' ? (current + items.length - 1) % items.length : -1;
        if (next >= 0 && items.length) { event.preventDefault(); items[next].focus(); }
    }
    document.addEventListener('pointerdown', outside, true);
    window.addEventListener('scroll', scroll, true); window.addEventListener('resize', scroll);
    fab.addEventListener('keydown', key);
    fab.addEventListener('pointerenter', hover); fab.addEventListener('pointerleave', hover);
    const controller = {
        setOpen(value) {
            if (!value) { close(); return; }
            if (disposed || !button.isConnected) return;
            if (active && active !== controller) active.close();
            active = controller; opened = true; button.setAttribute('aria-expanded', 'true');
            menu.setAttribute('popover', 'manual');
            if (!menu.matches(':popover-open')) menu.showPopover();
            place();
        }, close, focus,
        dispose() {
            disposed = true; close(); document.removeEventListener('pointerdown', outside, true);
            window.removeEventListener('scroll', scroll, true); window.removeEventListener('resize', scroll);
            fab.removeEventListener('keydown', key);
            fab.removeEventListener('pointerenter', hover); fab.removeEventListener('pointerleave', hover);
        }
    };
    return controller;
}
