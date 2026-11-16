// Presentation only: the report remains in its existing script-disabled sandbox.
function reportStyles() {
    const theme = getComputedStyle(document.body);
    const css = (name, fallback) => (theme.getPropertyValue(name).trim() || fallback).replaceAll('<', '\\3c ');
    return `
        body { margin: 0; padding: 14px; font: 13px/1.5 ${css('--mud-typography-default-family', 'system-ui, sans-serif')};
            color: ${css('--mud-palette-text-primary', '#1b2635')}; background: ${css('--mud-palette-surface', '#fff')}; overflow-wrap: anywhere; }
        h1,h2,h3 { font-family: inherit; font-size: 15px; line-height: 1.4; margin: 0 0 10px; }
        h4 { font-size: 13px; margin: 14px 0 6px; } p,dl { margin: 8px 0; }
        dl { display: grid; grid-template-columns: minmax(90px,max-content) minmax(0,1fr); gap: 4px 12px; }
        dt { font-weight: 600; } dd { margin: 0; }
        a { color: ${css('--mud-palette-primary', '#b7482a')}; text-decoration: underline; text-underline-offset: 2px; }
        table { border-collapse: collapse; display: block; overflow: auto; max-width: 100%; }
        th,td { text-align: left; vertical-align: top; padding: 6px 8px; border: 1px solid ${css('--mud-palette-divider', '#d9dce1')}; }
        pre { font: 12px/1.5 ui-monospace,monospace; white-space: pre-wrap; overflow-wrap: anywhere; }
        summary { cursor: pointer; margin: 10px 0 6px; }
        @media(max-width:480px) { dl { grid-template-columns: minmax(0,1fr); } dd { margin-bottom: 6px; } }
    `;
}

export function bindReport(frame, html) {
    let scheduled = 0, previousStyles;
    const dispose = () => { observer.disconnect(); cancelAnimationFrame(scheduled); };
    const render = () => {
        scheduled = 0;
        if (!frame.isConnected) { dispose(); return; }
        const styles = reportStyles();
        if (styles === previousStyles) return;
        previousStyles = styles;
        const report = new DOMParser().parseFromString(html, 'text/html');
        const style = report.createElement('style');
        style.textContent = styles;
        report.head.prepend(style);
        frame.srcdoc = '<!doctype html>' + report.documentElement.outerHTML;
    };
    // MudBlazor replaces theme style text; custom palettes can also change root variables.
    const observer = new MutationObserver(() => {
        if (!scheduled) scheduled = requestAnimationFrame(render);
    });
    observer.observe(document.documentElement, { subtree: true, childList: true, characterData: true,
        attributes: true, attributeFilter: ['class', 'style', 'data-netratel-theme'] });
    render();
    return { dispose };
}
