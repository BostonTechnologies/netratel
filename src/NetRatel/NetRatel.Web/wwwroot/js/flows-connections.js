// Client-local gestures commit through Velox's native Send/Receive adapter commands.
// Pointer frames only update browser SVG/classes; they never capture or dirty a graph.
const canvases = new Map();
export function attach(id, reference) {
    detach(id);
    const panel = document.getElementById(id);
    if (!panel) return;
    let gesture = null, frame = 0, disposed = false, suppressClick = false;
    const ghost = () => panel.querySelector('[data-testid="flow-virtual-link"]');
    const message = () => panel.parentElement?.querySelector('[data-testid="flow-connection-message"]');
    const portAt = event => document.elementFromPoint(event.clientX, event.clientY)?.closest('.flow-port');
    const writable = () => panel.dataset.readOnly !== 'true';
    const clearHighlights = () => panel.querySelectorAll('.flow-port.compatible,.flow-port.incompatible,.flow-port.pending')
        .forEach(port => port.classList.remove('compatible', 'incompatible', 'pending'));
    const highlight = source => {
        clearHighlights();
        const targets = new Set((source.dataset.compatibleTargets ?? '').split(' ').filter(Boolean));
        source.classList.add('pending');
        panel.querySelectorAll('.flow-port.input').forEach(port => port.classList.add(targets.has(port.dataset.portNode) ? 'compatible' : 'incompatible'));
        return targets;
    };
    const draw = () => {
        frame = 0;
        if (!gesture || disposed) return;
        const path = ghost(), canvas = panel.querySelector('.veloxdev-wf-canvas');
        const content = canvas?.querySelector('.veloxdev-wf-canvas-content');
        const glyph = gesture.source.querySelector('svg');
        if (!path || !canvas || !glyph) return;
        const origin = canvas.getBoundingClientRect(), anchor = glyph.getBoundingClientRect();
        const x = (anchor.left + anchor.width / 2) - origin.left - (content?.offsetLeft ?? 0);
        const y = (anchor.top + anchor.height / 2) - origin.top - (content?.offsetTop ?? 0);
        const ex = gesture.x - origin.left - (content?.offsetLeft ?? 0);
        const ey = gesture.y - origin.top - (content?.offsetTop ?? 0);
        if (![x, y, ex, ey].every(Number.isFinite)) return;
        const pull = Math.max(40, Math.abs(ex - x) / 2);
        path.setAttribute('d', `M ${x},${y} C ${x + pull},${y} ${ex - pull},${ey} ${ex},${ey}`);
        path.removeAttribute('hidden');
    };
    const clear = () => {
        if (frame) cancelAnimationFrame(frame);
        frame = 0; gesture = null;
        ghost()?.setAttribute('hidden', 'hidden');
        clearHighlights();
        syncKeyboard();
    };
    const cancel = () => {
        if (!gesture && !panel.dataset.connectionStart) return;
        clear();
        if (!disposed) reference.invokeMethodAsync('CancelPortConnection').catch(() => {});
    };
    const down = event => {
        const source = event.target.closest?.('.flow-port');
        if (!source || !panel.contains(source)) return;
        if (gesture && gesture.pointerId !== event.pointerId) return;
        suppressClick = false;
        if (!writable()) { event.preventDefault(); event.stopImmediatePropagation(); return; }
        if (event.pointerType === 'touch' || source.dataset.portDirection !== 'output' || event.button !== 0) return;
        gesture = { source, pointerId: event.pointerId, targets: highlight(source), x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY, moved: false };
        if (message()) message().textContent = 'Drag to a highlighted input. Escape cancels.';
        draw(); // Visible immediately while the pointer is still held down.
    };
    const move = event => {
        if (!gesture || gesture.pointerId !== event.pointerId) return;
        gesture.x = event.clientX; gesture.y = event.clientY;
        gesture.moved ||= Math.hypot(event.clientX - gesture.startX, event.clientY - gesture.startY) > 4;
        const target = portAt(event);
        if (message()) message().textContent = target && panel.contains(target)
            ? gesture.targets.has(target.dataset.portNode) && target.dataset.portDirection === 'input'
                ? 'Release to connect these ports.'
                : 'This input is incompatible, occupied, or would create a cycle.'
            : 'Drag to a highlighted input. Escape cancels.';
        if (!frame) frame = requestAnimationFrame(draw);
    };
    const up = event => {
        if (!gesture || gesture.pointerId !== event.pointerId) return;
        const source = gesture.source.dataset.portNode, target = portAt(event);
        suppressClick = gesture.moved;
        const valid = target && panel.contains(target) && target.dataset.portDirection === 'input' && gesture.targets.has(target.dataset.portNode);
        clear();
        if (valid && !disposed)
            reference.invokeMethodAsync('CompletePortConnection', source, target.dataset.portNode).catch(() => {
                if (!disposed && message()) message().textContent = 'The connection could not be confirmed. Reconnect and inspect the draft.';
            });
        else if (target?.dataset.portNode && !disposed)
            reference.invokeMethodAsync('ExplainPortConnection', source, target.dataset.portNode).catch(() => {});
        if (message()) message().textContent = valid ? 'Connecting ports…' : 'Connection cancelled. Drop on a compatible input.';
    };
    const touch = event => {
        const port = event.target.closest?.('.flow-port');
        if (!port || !panel.contains(port)) return;
        event.preventDefault(); event.stopPropagation();
        if (writable() && !gesture && event.touches.length === 1) port.querySelector('button')?.click();
    };
    const cancelPointer = event => { if (gesture?.pointerId === event.pointerId) cancel(); };
    const keys = event => {
        if (event.key !== 'Escape' || (!gesture && !panel.dataset.connectionStart)) return;
        event.preventDefault(); event.stopImmediatePropagation(); cancel();
    };
    const mouseGuard = event => {
        if (event.target.closest?.('.flow-port')) { event.preventDefault(); event.stopImmediatePropagation(); }
    };
    const clickGuard = event => {
        if (suppressClick && event.detail !== 0 && event.target.closest?.('.flow-port')) {
            suppressClick = false; event.preventDefault(); event.stopImmediatePropagation();
        }
    };
    const syncKeyboard = () => {
        if (gesture) return;
        clearHighlights();
        const source = [...panel.querySelectorAll('.flow-port.output')].find(port => port.dataset.portNode === panel.dataset.connectionStart);
        if (source && writable()) highlight(source);
    };
    const observer = new MutationObserver(mutations => {
        if (!panel.isConnected) { detach(id); return; }
        for (const mutation of mutations) {
            if (mutation.type === 'attributes' && mutation.attributeName === 'd' && mutation.target.matches('[data-veloxdev-link-curve]')) {
                // Velox synchronizes the visible curve in the same frame as zoom.
                // Keep its transparent accessible hit path on that exact geometry.
                mutation.target.parentElement?.querySelector('.flow-connection-hit')?.setAttribute('d', mutation.target.getAttribute('d') ?? '');
            }
        }
        if (!writable() && gesture) cancel();
        syncKeyboard();
    });
    observer.observe(panel, { subtree: true, childList: true, attributes: true, attributeFilter: ['d', 'data-connection-start', 'data-read-only'] });
    const removed = new MutationObserver(() => { if (!panel.isConnected) detach(id); });
    removed.observe(document.body, { childList: true, subtree: true });
    panel.addEventListener('pointerdown', down, true);
    panel.addEventListener('mousedown', mouseGuard, true);
    panel.addEventListener('click', clickGuard, true);
    panel.addEventListener('touchstart', touch, { capture: true, passive: false });
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
    document.addEventListener('pointercancel', cancelPointer);
    document.addEventListener('lostpointercapture', cancelPointer);
    // Window capture runs before the shell's document capture handler. Gestures
    // consume Escape first in window fallback; a browser may still end native fullscreen.
    window.addEventListener('keydown', keys, true);
    const fullscreen = () => { if (!document.fullscreenElement) cancel(); };
    document.addEventListener('fullscreenchange', fullscreen);
    window.addEventListener('blur', cancel);
    canvases.set(id, () => {
        disposed = true;
        if (frame) cancelAnimationFrame(frame);
        observer.disconnect(); removed.disconnect(); clearHighlights();
        document.removeEventListener('fullscreenchange', fullscreen);
        panel.removeEventListener('pointerdown', down, true); panel.removeEventListener('mousedown', mouseGuard, true); panel.removeEventListener('click', clickGuard, true);
        panel.removeEventListener('touchstart', touch, true);
        document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', up);
        document.removeEventListener('pointercancel', cancelPointer); document.removeEventListener('lostpointercapture', cancelPointer);
        window.removeEventListener('keydown', keys, true); window.removeEventListener('blur', cancel);
        ghost()?.setAttribute('hidden', 'hidden');
    });
}
export function detach(id) { canvases.get(id)?.(); canvases.delete(id); }
