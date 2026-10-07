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
