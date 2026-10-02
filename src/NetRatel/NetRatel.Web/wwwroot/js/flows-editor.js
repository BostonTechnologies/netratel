const viewers = new Map();
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
    viewers.set(id, () => { clearTimeout(timer); el.removeEventListener("scroll", handler); });
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
