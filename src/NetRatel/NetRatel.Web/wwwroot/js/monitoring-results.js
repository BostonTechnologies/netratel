// Size the row viewport from the actual wrapped toolbar and pager, including browser zoom.
export function observeResults(element) {
    let current, observer, frame;
    const schedule = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(fit); };
    function fit() {
        if (!current?.isConnected) return;
        const pager = current.parentElement.querySelector('.monitoring-pager');
        const top = current.getBoundingClientRect().top;
        const bottom = pager?.getBoundingClientRect().height ?? 0;
        const available = Math.max(96, window.innerHeight - Math.max(0, top) - bottom - 16);
        const value = `${Math.floor(available)}px`;
        if (current.style.maxHeight !== value) current.style.maxHeight = value;
    }
    function setElement(next) {
        if (current === next) { schedule(); return; }
        observer?.disconnect();
        current = next;
        if (!current) return;
        observer = new ResizeObserver(schedule);
        observer.observe(current.parentElement);
        const heading = current.closest('.monitoring-page')?.querySelector('.monitoring-heading');
        if (heading) observer.observe(heading);
        schedule();
    }
    window.addEventListener('resize', schedule);
    window.addEventListener('scroll', schedule, { passive: true });
    setElement(element);
    return DotNet.createJSObjectReference({
        setElement,
        dispose() { cancelAnimationFrame(frame); observer?.disconnect(); window.removeEventListener('resize', schedule); window.removeEventListener('scroll', schedule); current = null; }
    });
}
