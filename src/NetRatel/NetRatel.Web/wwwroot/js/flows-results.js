// The toolbar and pager wrap with viewport width, zoom and translations.
export function observeResults(element) {
    let current = element, observer, frame;
    const schedule = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(fit); };
    function fit() {
        if (!current?.isConnected) return;
        const container = current.querySelector('.mud-table-container');
        const pager = current.querySelector('.mud-table-pagination');
        if (!container) return;
        const top = container.getBoundingClientRect().top;
        const available = Math.max(96, Math.min(700, window.innerHeight - Math.max(0, top) - (pager?.getBoundingClientRect().height ?? 0) - 16));
        const value = `${Math.floor(available)}px`;
        if (container.style.height !== value) container.style.height = value;
        if (container.style.maxHeight !== value) container.style.maxHeight = value;
    }
    function setElement(next) {
        observer?.disconnect(); current = next;
        if (!current) return;
        observer = new ResizeObserver(schedule);
        observer.observe(current);
        for (const node of current.closest('.flows-page')?.querySelectorAll('.flows-heading,.flows-toolbar,.mud-table-pagination') ?? []) observer.observe(node);
        schedule();
    }
    window.addEventListener('resize', schedule);
    setElement(element);
    return {
        setElement,
        dispose() { cancelAnimationFrame(frame); observer?.disconnect(); window.removeEventListener('resize', schedule); current = null; }
    };
}
