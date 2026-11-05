const viewers = new Map();
const dropPoints = new Map();
export function geometry(id) {
    const el = document.getElementById(id);
    if (!el) throw new Error("Flow canvas is unavailable.");
    return [el.scrollLeft, el.scrollTop, el.clientWidth, el.clientHeight, el.scrollWidth, el.scrollHeight];
}
export function restore(id, x, y) {
    const el = document.getElementById(id);
    if (el) { el.scrollLeft = x; el.scrollTop = y; }
}
export async function attach(id, x, y, reference) {
    detach(id);
    // Native behavior components initialize their module after the first render.
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const el = document.getElementById(id);
    if (!el) return;
    restore(id, x, y);
    let timer;
    const handler = () => {
        clearTimeout(timer);
        timer = setTimeout(() => reference.invokeMethodAsync("ViewportChanged", el.scrollLeft, el.scrollTop).catch(() => {}), 150);
    };
    el.addEventListener("scroll", handler, { passive: true });
    const workspace = el.closest('.flow-workspace');
    let paletteDrag = false;
    const start = event => {
        paletteDrag = !!event.target.closest?.('.flow-palette-handle') && workspace?.contains(event.target);
        dropPoints.delete(id);
    };
    const record = event => {
        if (!paletteDrag) return;
        const pointer = event.changedTouches?.[0] ?? event;
        const point = pointInCanvas(el, pointer.clientX, pointer.clientY);
        // Snapshot the native content origin before Mud's asynchronous commit/rerender.
        if (point) dropPoints.set(id, point);
        else dropPoints.delete(id);
        paletteDrag = false;
    };
    const end = () => {
        // A completed drop has already set paletteDrag=false in record. Cancelled
        // drags have no Mud commit and must never leave a coordinate for a later one.
        if (paletteDrag) dropPoints.delete(id);
        paletteDrag = false;
    };
    const nodeAction = event => {
        if (event.target.closest?.('.flow-node-actions')) event.stopPropagation();
    };
    const keys = event => { if (event.key === 'Delete' && !isEditing()) event.preventDefault(); };
    const size = () => {
        if (workspace) workspace.style.setProperty('--flow-workspace-top', `${Math.max(0, workspace.getBoundingClientRect().top)}px`);
    };
    const observer = new ResizeObserver(size);
    if (workspace?.parentElement) observer.observe(workspace.parentElement);
    window.addEventListener('resize', size);
    document.addEventListener('dragstart', start, true);
    document.addEventListener('touchstart', start, { capture: true, passive: true });
    document.addEventListener('drop', record, true);
    document.addEventListener('touchend', record, true);
    document.addEventListener('dragend', end, true);
    el.addEventListener('keydown', keys, true);
    el.addEventListener('pointerdown', nodeAction, true);
    el.addEventListener('mousedown', nodeAction, true);
    el.addEventListener('touchstart', nodeAction, { capture: true, passive: true });
    document.addEventListener('touchcancel', end, true);
    size();
    viewers.set(id, () => {
        clearTimeout(timer); observer.disconnect();
        el.removeEventListener("scroll", handler); el.removeEventListener('keydown', keys, true);
        window.removeEventListener('resize', size);
        document.removeEventListener('dragstart', start, true); document.removeEventListener('touchstart', start, true);
        document.removeEventListener('drop', record, true); document.removeEventListener('touchend', record, true); document.removeEventListener('dragend', end, true);
        el.removeEventListener('pointerdown', nodeAction, true); el.removeEventListener('mousedown', nodeAction, true); el.removeEventListener('touchstart', nodeAction, true);
        document.removeEventListener('touchcancel', end, true);
        dropPoints.delete(id);
    });
}
export function detach(id) { viewers.get(id)?.(); viewers.delete(id); }
export function focus(id) { document.getElementById(id)?.focus(); }
export async function fitPosition(id, marginX, marginY) {
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const el = document.getElementById(id);
    if (!el) return;
    const nodes = [...el.querySelectorAll('.flow-node')].map(node => node.getBoundingClientRect());
    if (!nodes.length) return;
    const r = el.getBoundingClientRect();
    el.scrollLeft += Math.min(...nodes.map(n => n.left)) - r.left - marginX;
    el.scrollTop += Math.min(...nodes.map(n => n.top)) - r.top - marginY;
}

// Velox Core8 renders anchors in viewport pixels and moves .canvas-content for both
// edge expansion and layout offsets. Its DOM bounds include scroll and pan already.
// FlowCanvas converts the resulting native anchor back to canonical coordinates
// using the adapter's current inverse zoom scale.
function pointInCanvas(scroller, x, y) {
    if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
    const bounds = scroller.getBoundingClientRect();
    if (x < bounds.left || x >= bounds.right || y < bounds.top || y >= bounds.bottom) return null;
    const content = scroller.querySelector('.veloxdev-wf-canvas-content');
    if (!content) return null;
    const origin = content.getBoundingClientRect();
    return [x - origin.left, y - origin.top];
}
export function takeDropPosition(id) {
    const point = dropPoints.get(id) ?? null;
    dropPoints.delete(id); // A Mud commit can consume each physical drop only once.
    return point;
}
export function centerPosition(id) {
    const scroller = document.getElementById(id);
    if (!scroller) return null;
    const bounds = scroller.getBoundingClientRect();
    return pointInCanvas(scroller, bounds.left + bounds.width / 2, bounds.top + bounds.height / 2);
}
export function isEditing() {
    const active = document.activeElement;
    return !!active?.closest('input,textarea,select,[contenteditable="true"],[contenteditable=""]');
}

export function focusNodeSettings(panelId, nodeId) {
    const panel = document.getElementById(panelId);
    const node = [...(panel?.querySelectorAll('[data-node-id]') ?? [])].find(item => item.dataset.nodeId === nodeId);
    (node?.querySelector('[data-testid="flow-node-settings"]') ?? panel)?.focus();
}

// A fixed workspace supplies the same layout when the Fullscreen API is unavailable.
// The native root includes Mud's existing providers; never reparent Blazor-owned DOM.
const shells = new Map();
export function attachShell(id, reference) {
    detachShell(id);
    const root = document.getElementById(id);
    if (!root) return;
    const state = { root, reference, focused: false, ownsNative: false, nativePending: false, disposed: false, unsynchronized: false, returnFocus: null, preview: null };
    const notify = () => {
        if (state.disposed) return;
        reference.invokeMethodAsync("FullscreenChanged", state.focused)
            .then(() => { state.unsynchronized = false; })
            .catch(() => { if (!root.isConnected) detachShell(id); else state.unsynchronized = true; });
    };
    state.setFocus = async focused => {
        if (state.disposed || state.focused === focused) return;
        const positions = [...root.querySelectorAll('[id^="flow-scroll-"]')].map(el => [el, el.scrollLeft, el.scrollTop]);
        if (focused) state.returnFocus = document.activeElement;
        state.focused = focused;
        root.classList.toggle('focused', focused);
        document.documentElement.classList.toggle('netratel-flow-fullscreen', [...shells.values()].some(shell => shell.focused && !shell.disposed));
        notify();
        // Start this call synchronously from the click handler, before the first await,
        // so native fullscreen has a real browser user activation on a server circuit.
        if (focused && !state.nativePending && !document.fullscreenElement && document.fullscreenEnabled && document.documentElement.requestFullscreen) {
            state.ownsNative = true; state.nativePending = true;
            try {
                await document.documentElement.requestFullscreen();
                if (state.disposed || !state.focused) {
                    state.ownsNative = false;
                    if (document.fullscreenElement === document.documentElement) await document.exitFullscreen();
                }
            }
            catch { state.ownsNative = false; } // Fixed full-window fallback stays active.
            finally { state.nativePending = false; }
        } else if (!focused && state.ownsNative && !state.nativePending) {
            state.ownsNative = false;
            if (document.fullscreenElement === document.documentElement) {
                try { await document.exitFullscreen(); } catch { /* Window mode is already restored. */ }
            }
        }
        requestAnimationFrame(() => {
            if (state.disposed) return;
            for (const [el, x, y] of positions) if (el.isConnected) { el.scrollLeft = x; el.scrollTop = y; }
            if (!focused && state.returnFocus?.isConnected) state.returnFocus.focus({ preventScroll: true });
            window.dispatchEvent(new Event('resize'));
        });
    };
    const click = event => {
        if (state.unsynchronized) notify();
        if (!event.target.closest?.('[data-testid="flow-focus"]')) return;
        event.preventDefault(); event.stopImmediatePropagation();
        void state.setFocus(!state.focused);
    };
    const historyPointer = event => {
        if (state.disposed || root.dataset.historyEnabled !== 'true' || event.button > 0 ||
            !event.target.closest?.('[data-testid="flow-canvas"]')) return;
        // One callback per physical gesture, before Velox's mousedown/move commands.
        reference.invokeMethodAsync("BeginCanvasEditGesture").catch(() => {});
    };
    const historyKey = event => {
        if (state.disposed || root.dataset.historyEnabled !== 'true' || isEditing() || event.altKey ||
            !(event.ctrlKey || event.metaKey) || root.querySelector('dialog[open]')) return;
        const key = event.key.toLowerCase();
        if (key !== 'z' && key !== 'y') return;
        event.preventDefault(); event.stopImmediatePropagation();
        reference.invokeMethodAsync("NavigateGraphHistory", key === 'y' || event.shiftKey).catch(() => {});
    };
    const fullscreen = () => {
        if (state.ownsNative && document.fullscreenElement !== document.documentElement) {
            state.ownsNative = false;
            void state.setFocus(false);
        }
    };
    const escape = event => {
        if (event.key !== 'Escape' || !state.focused) return;
        if (root.querySelector('dialog[open], .flow-properties, .flow-live-link:not([hidden]), .flow-route-handle.dragging, [data-connection-start]:not([data-connection-start=""])')) return;
        const overlayOpen = [...document.querySelectorAll('.mud-dialog-container, .mud-popover-open')]
            .some(el => el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden');
        if (overlayOpen) return;
        event.preventDefault(); event.stopImmediatePropagation();
        void state.setFocus(false);
    };
    // A disconnected server cannot dispose browser listeners. Observe root removal
    // so a replacement component never inherits this shell's body lock or callbacks.
    const observer = new MutationObserver(() => {
        if (!root.isConnected) { detachShell(id); return; }
        // Keep local fullscreen presentation truthful while a circuit reconnects.
        root.classList.toggle('focused', state.focused);
    });
    observer.observe(document.body, { childList: true, subtree: true });
    observer.observe(root, { attributes: true, attributeFilter: ['class'] });
    root.addEventListener('click', click, true);
    root.addEventListener('pointerdown', historyPointer, true);
    root.addEventListener('keydown', historyKey, true);
    document.addEventListener('keydown', escape, true);
    document.addEventListener('fullscreenchange', fullscreen);
    state.dispose = () => {
        state.disposed = true;
        observer.disconnect();
        root.removeEventListener('click', click, true);
        root.removeEventListener('pointerdown', historyPointer, true);
        root.removeEventListener('keydown', historyKey, true);
        document.removeEventListener('keydown', escape, true);
        document.removeEventListener('fullscreenchange', fullscreen);
        if (state.preview) { state.preview.element.removeEventListener('cancel', state.preview.cancel); state.preview.element.close(); }
        document.documentElement.classList.toggle('netratel-flow-fullscreen', [...shells.values()].some(shell => shell !== state && shell.focused && !shell.disposed));
        if (state.ownsNative && document.fullscreenElement === document.documentElement) void document.exitFullscreen().catch(() => {});
    };
    shells.set(id, state);
}
export function setShellFocus(id, focused) { return shells.get(id)?.setFocus(focused); }
export function detachShell(id) { shells.get(id)?.dispose(); shells.delete(id); }
export function showPreview(shellId, previewId) {
    const state = shells.get(shellId), dialog = document.getElementById(previewId);
    if (!state || !dialog || dialog.open) return;
    if (state.preview) state.preview.element.removeEventListener('cancel', state.preview.cancel);
    const cancel = event => {
        event.preventDefault();
        if (!state.disposed) state.reference.invokeMethodAsync("PreviewCancelled").catch(() => {});
    };
    state.preview = { element: dialog, cancel };
    dialog.addEventListener('cancel', cancel);
    dialog.showModal();
}
export function closePreview(shellId, previewId) {
    const state = shells.get(shellId);
    document.getElementById(previewId)?.close();
    state?.root.querySelector('[data-testid="flow-dry-run-toggle"]')?.focus({ preventScroll: true });
}
