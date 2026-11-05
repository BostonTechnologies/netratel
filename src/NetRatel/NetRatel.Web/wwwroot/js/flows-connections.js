// Client-local gestures commit through Velox's native Send/Receive adapter commands.
// Pointer frames only update browser SVG/classes; they never capture or dirty a graph.
const canvases = new Map();
export function attach(id, reference) {
    detach(id);
    const panel = document.getElementById(id);
    if (!panel) return;
    let gesture = null, routeDrag = null, routePreview = null, frame = 0, disposed = false, suppressClick = false, keyboardCancelled = false;
    const ghost = () => panel.querySelector('[data-testid="flow-virtual-link"]');
    const message = () => panel.parentElement?.querySelector('[data-testid="flow-connection-message"]');
    const portAt = event => document.elementFromPoint(event.clientX, event.clientY)?.closest('.flow-port');
    const writable = () => panel.dataset.readOnly !== 'true';
    const inverseScale = () => {
        const node = panel.querySelector('.flow-node[data-design-width]');
        const native = node?.closest('[data-veloxdev-node-id]');
        const width = Number.parseFloat(native?.style.width), design = Number.parseFloat(node?.dataset.designWidth);
        return width > 0 && design > 0 ? design / width : Number.parseFloat(panel.dataset.inverseScale) || 1;
    };
    const localPoint = (x, y) => {
        const canvas = panel.querySelector('.veloxdev-wf-canvas'), content = canvas?.querySelector('.veloxdev-wf-canvas-content');
        if (!canvas) return null;
        const origin = canvas.getBoundingClientRect();
        return { x: x - origin.left - (content?.offsetLeft ?? 0), y: y - origin.top - (content?.offsetTop ?? 0) };
    };
    const endpoint = (group, attribute) => {
        const id = group.getAttribute(attribute);
        const slot = id && panel.querySelector('[data-veloxdev-slot-id="' + id + '"]');
        const glyph = slot?.querySelector('.veloxdev-wf-slot-svg') ?? slot;
        if (!glyph) return null;
        const rect = glyph.getBoundingClientRect();
        return localPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
    };
    const routedPath = points => {
        let path = `M ${points[0].x.toFixed(2)},${points[0].y.toFixed(2)}`;
        for (let index = 0; index + 1 < points.length; index++) {
            const a = points[index], b = points[index + 1], previous = points[Math.max(0, index - 1)], next = points[Math.min(points.length - 1, index + 2)];
            let c1 = { x: a.x + (b.x - previous.x) / 6, y: a.y + (b.y - previous.y) / 6 };
            let c2 = { x: b.x - (next.x - a.x) / 6, y: b.y - (next.y - a.y) / 6 };
            if (index === 0) c1 = { x: a.x + Math.max(40, Math.abs(b.x - a.x) / 2), y: a.y };
            if (index + 2 === points.length) c2 = { x: b.x - Math.max(40, Math.abs(b.x - a.x) / 2), y: b.y };
            path += ` C ${c1.x.toFixed(2)},${c1.y.toFixed(2)} ${c2.x.toFixed(2)},${c2.y.toFixed(2)} ${b.x.toFixed(2)},${b.y.toFixed(2)}`;
        }
        return path;
    };
    const syncRoute = group => {
        if (!group?.isConnected || !group.dataset.flowRoute) return;
        let route;
        try { route = JSON.parse(group.dataset.flowRoute); } catch { return; }
        const scale = inverseScale(), source = endpoint(group, 'data-veloxdev-sender-slot'), target = endpoint(group, 'data-veloxdev-receiver-slot');
        if (!source || !target || !Array.isArray(route)) return;
        const points = route.map(point => ({ x: point.X / scale, y: point.Y / scale }));
        const preview = routePreview?.group === group ? routePreview : null;
        if (preview && preview.index >= 0 && preview.index < points.length) points[preview.index] = { x: preview.x, y: preview.y };
        if (![source, ...points, target].every(point => Number.isFinite(point.x) && Number.isFinite(point.y))) return;
        const path = routedPath([source, ...points, target]);
        for (const element of group.querySelectorAll('.flow-connection-line,.flow-connection-hit')) if (element.getAttribute('d') !== path) element.setAttribute('d', path);
        panel.querySelectorAll('.flow-route-handle[data-edge-id="' + group.dataset.flowEdgeId + '"]').forEach(handle => {
            const point = points[Number(handle.dataset.pointIndex)];
            if (point) { handle.setAttribute('cx', point.x); handle.setAttribute('cy', point.y); }
        });
    };
    const syncRoutes = () => panel.querySelectorAll('[data-flow-route]').forEach(syncRoute);
    const cancelRoute = () => {
        if (!routeDrag) return;
        const active = routeDrag; routeDrag = null; routePreview = null;
        active.handle.classList.remove('dragging');
        if (active.handle.hasPointerCapture?.(active.pointerId)) active.handle.releasePointerCapture(active.pointerId);
        syncRoute(active.group);
    };
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
        if (disposed) return;
        if (routeDrag) { syncRoute(routeDrag.group); return; }
        if (!gesture) return;
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
    const clear = (restoreKeyboard = true) => {
        if (frame) cancelAnimationFrame(frame);
        frame = 0; gesture = null;
        ghost()?.setAttribute('hidden', 'hidden');
        clearHighlights();
        if (restoreKeyboard) syncKeyboard();
    };
    const cancel = () => {
        if (!gesture && !panel.dataset.connectionStart) return;
        keyboardCancelled = true;
        clear(false);
        if (!disposed) reference.invokeMethodAsync('CancelPortConnection').catch(() => {});
    };
    const down = event => {
        const active = routeDrag ?? gesture;
        if (active && active.pointerId !== event.pointerId) return;
        const handle = event.target.closest?.('.flow-route-handle');
        if (handle && panel.contains(handle)) {
            if (!writable() || event.button !== 0) return;
            event.preventDefault(); event.stopPropagation();
            const point = localPoint(event.clientX, event.clientY), group = panel.querySelector('[data-flow-edge-id="' + handle.dataset.edgeId + '"][data-flow-route]');
            if (!point || !group) return;
            routeDrag = { handle, group, index: Number(handle.dataset.pointIndex), pointerId: event.pointerId };
            routePreview = { group, index: routeDrag.index, ...point };
            handle.classList.add('dragging'); handle.setPointerCapture(event.pointerId);
            return;
        }
        const source = event.target.closest?.('.flow-port');
        if (!source || !panel.contains(source)) return;
        suppressClick = false; keyboardCancelled = false;
        if (!writable()) { event.preventDefault(); event.stopImmediatePropagation(); return; }
        if (event.pointerType === 'touch' || source.dataset.portDirection !== 'output' || event.button !== 0) return;
        gesture = { source, pointerId: event.pointerId, targets: highlight(source), x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY, moved: false };
        if (message()) message().textContent = 'Drag to a highlighted input. Escape cancels.';
        draw(); // Visible immediately while the pointer is still held down.
    };
    const move = event => {
        if (routeDrag) {
            if (routeDrag.pointerId !== event.pointerId) return;
            const point = localPoint(event.clientX, event.clientY);
            if (point) routePreview = { group: routeDrag.group, index: routeDrag.index, ...point };
            if (!frame) frame = requestAnimationFrame(draw);
            return;
        }
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
        if (routeDrag) {
            if (routeDrag.pointerId !== event.pointerId) return;
            const active = routeDrag, point = localPoint(event.clientX, event.clientY);
            routeDrag = null; active.handle.classList.remove('dragging');
            if (active.handle.hasPointerCapture?.(active.pointerId)) active.handle.releasePointerCapture(active.pointerId);
            if (point && !disposed) reference.invokeMethodAsync('CommitRoutePoint', active.group.dataset.flowEdgeId, active.index, point.x, point.y)
                .then(() => { routePreview = null; if (!disposed) requestAnimationFrame(() => syncRoute(active.group)); })
                .catch(() => { routePreview = null; if (!disposed) { syncRoute(active.group); if (message()) message().textContent = 'The route change could not be confirmed.'; } });
            return;
        }
        if (!gesture || gesture.pointerId !== event.pointerId) return;
        const source = gesture.source.dataset.portNode, gestureSourcePort = gesture.source.dataset.portId, target = portAt(event);
        suppressClick = gesture.moved;
        const valid = target && panel.contains(target) && target.dataset.portDirection === 'input' && gesture.targets.has(target.dataset.portNode);
        clear();
        if (valid && !disposed)
            reference.invokeMethodAsync('CompleteNamedPortConnection', source, gestureSourcePort, target.dataset.portNode, target.dataset.portId).catch(() => {
                if (!disposed && message()) message().textContent = 'The connection could not be confirmed. Reconnect and inspect the draft.';
            });
        else if (target?.dataset.portNode && !disposed)
            reference.invokeMethodAsync('ExplainNamedPortConnection', source, gestureSourcePort, target.dataset.portNode, target.dataset.portId).catch(() => {});
        if (message()) message().textContent = valid ? 'Connecting ports…' : 'Connection cancelled. Drop on a compatible input.';
    };
    const touch = event => {
        const port = event.target.closest?.('.flow-port');
        if (!port || !panel.contains(port)) return;
        event.preventDefault(); event.stopPropagation();
        if (writable() && !gesture && !routeDrag && event.touches.length === 1) port.querySelector('button')?.click();
    };
    const cancelPointer = event => { if (routeDrag?.pointerId === event.pointerId) cancelRoute(); else if (gesture?.pointerId === event.pointerId) cancel(); };
    const keys = event => {
        if (event.key === 'Escape' && routeDrag) { event.preventDefault(); event.stopImmediatePropagation(); cancelRoute(); return; }
        if (event.key !== 'Escape' || (!gesture && !panel.dataset.connectionStart)) return;
        event.preventDefault(); event.stopImmediatePropagation(); cancel();
    };
    const mouseGuard = event => {
        if (event.target.closest?.('.flow-port,.flow-route-handle')) { event.preventDefault(); event.stopImmediatePropagation(); }
    };
    const clickGuard = event => {
        if (!suppressClick && event.target.closest?.('.flow-port')) keyboardCancelled = false;
        if (suppressClick && event.detail !== 0 && event.target.closest?.('.flow-port')) {
            suppressClick = false; event.preventDefault(); event.stopImmediatePropagation();
        }
    };
    const syncKeyboard = () => {
        if (gesture) return;
        clearHighlights();
        if (!panel.dataset.connectionStart) keyboardCancelled = false;
        if (keyboardCancelled) return;
        const source = [...panel.querySelectorAll('.flow-port.output')].find(port => port.dataset.portNode === panel.dataset.connectionStart && port.dataset.portId === panel.dataset.connectionPort);
        if (source && writable()) highlight(source);
    };
    const observer = new MutationObserver(mutations => {
        if (!panel.isConnected) { detach(id); return; }
        for (const mutation of mutations) {
            if (mutation.type === 'attributes' && mutation.attributeName === 'data-flow-route') {
                if (!routeDrag && routePreview?.group === mutation.target) routePreview = null;
                syncRoute(mutation.target);
            }
            if (mutation.type === 'attributes' && mutation.attributeName === 'd' && mutation.target.matches('[data-veloxdev-link-curve]')) {
                // Velox synchronizes the visible curve in the same frame as zoom.
                // Keep its transparent accessible hit path on that exact geometry.
                mutation.target.parentElement?.querySelector('.flow-connection-hit')?.setAttribute('d', mutation.target.getAttribute('d') ?? '');
            }
        }
        if (!writable() && routeDrag) cancelRoute();
        if (!writable() && gesture) cancel();
        syncKeyboard();
    });
    observer.observe(panel, { subtree: true, childList: true, attributes: true, attributeFilter: ['d', 'data-flow-route', 'data-connection-start', 'data-connection-port', 'data-read-only', 'data-inverse-scale'] });
    const layout = () => {
        // The adapter's design card differs from upstream demo sizes. Use each real
        // native node width so glyphs and routed links share the same zoom frame.
        panel.querySelectorAll('.flow-node[data-design-width]').forEach(node => {
            const native = node.closest('[data-veloxdev-node-id]'), width = Number.parseFloat(native?.style.width), design = Number.parseFloat(node.dataset.designWidth);
            if (width > 0 && design > 0) node.style.transform = `scale(${width / design})`;
        });
        syncRoutes();
    };
    const resize = new ResizeObserver(layout); resize.observe(panel);
    document.addEventListener('veloxdev-wf-layout-changed', layout);
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
    window.addEventListener('keydown', keys, true);
    const fullscreen = () => { if (!document.fullscreenElement) { cancelRoute(); cancel(); } };
    document.addEventListener('fullscreenchange', fullscreen);
    const blur = () => { cancelRoute(); cancel(); };
    window.addEventListener('blur', blur);
    canvases.set(id, () => {
        disposed = true;
        if (frame) cancelAnimationFrame(frame);
        observer.disconnect(); removed.disconnect(); resize.disconnect(); clearHighlights();
        document.removeEventListener('veloxdev-wf-layout-changed', layout); document.removeEventListener('fullscreenchange', fullscreen); routeDrag = null; routePreview = null;
        panel.removeEventListener('pointerdown', down, true); panel.removeEventListener('mousedown', mouseGuard, true); panel.removeEventListener('click', clickGuard, true);
        panel.removeEventListener('touchstart', touch, true);
        document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', up);
        document.removeEventListener('pointercancel', cancelPointer); document.removeEventListener('lostpointercapture', cancelPointer);
        window.removeEventListener('keydown', keys, true); window.removeEventListener('blur', blur);
        ghost()?.setAttribute('hidden', 'hidden');
    });
}
export function detach(id) { canvases.get(id)?.(); canvases.delete(id); }
