export function subscribe(component) {
    let timer;
    const visibility = () => {
        if (timer !== undefined && document.visibilityState !== "visible") {
            component.invokeMethodAsync("PauseHiddenMetrics").catch(error => {
                console.debug("Metrics visibility subscription ended", error);
            });
        }
    };
    document.addEventListener("visibilitychange", visibility);
    const setActive = active => {
        if (active && timer === undefined) {
            timer = window.setInterval(() => {
                if (document.visibilityState === "visible") {
                    component.invokeMethodAsync("RefreshVisibleMetrics").catch(error => {
                        console.debug("Metrics refresh ended", error);
                    });
                }
            }, 60_000);
        } else if (!active && timer !== undefined) {
            window.clearInterval(timer);
            timer = undefined;
        }
    };
    return {
        setActive,
        dispose() {
            setActive(false);
            document.removeEventListener("visibilitychange", visibility);
        }
    };
}

export function draw(canvas, tooltip, from, to, maximum, yLabels, xLabels, series) {
    const context = canvas.getContext("2d");
    if (!context) return { dispose() {} };
    const points = series.flatMap(line => line.points).sort((left, right) => left.x - right.x);
    const instruction = "Hover over the chart or use the arrow keys to inspect a point.";
    tooltip.textContent = instruction;
    tooltip.title = "";
    let selected = -1;
    let left = 82;
    let right = 0;
    const x = point => left + ((point - from) / (to - from)) * (right - left);
    const centre = point => (point.x + point.end) / 2;

    const render = () => {
        const bounds = canvas.getBoundingClientRect();
        if (bounds.width <= 0 || bounds.height <= 0) return;
        const ratio = window.devicePixelRatio || 1;
        canvas.width = Math.round(bounds.width * ratio);
        canvas.height = Math.round(bounds.height * ratio);
        context.scale(ratio, ratio);
        const color = getComputedStyle(canvas);
        const top = 14;
        const bottom = bounds.height - 30;
        left = bounds.width < 380 ? 69 : 82;
        right = bounds.width - 17;
        const y = value => bottom - Math.max(0, Math.min(1, value / maximum)) * (bottom - top);
        context.font = "11px " + color.fontFamily;

        for (let index = 0; index < yLabels.length; index++) {
            const level = y(maximum * index / (yLabels.length - 1));
            context.fillStyle = color.getPropertyValue("--mud-palette-text-secondary");
            context.textAlign = "right";
            context.fillText(yLabels[index], left - 8, level + 4);
            context.strokeStyle = color.getPropertyValue("--mud-palette-divider");
            context.beginPath();
            context.moveTo(left, level);
            context.lineTo(right, level);
            context.stroke();
        }
        xLabels.forEach((label, index) => {
            if (bounds.width < 470 && index % 2 === 1) return;
            context.textAlign = index === 0 ? "left" : index === xLabels.length - 1 ? "right" : "center";
            context.fillText(label, left + (right - left) * index / (xLabels.length - 1), bottom + 18);
        });

        series.forEach((line, index) => {
            const stroke = color.getPropertyValue(index === 0 ? "--mud-palette-primary" : "--mud-palette-tertiary").trim();
            context.strokeStyle = stroke;
            context.fillStyle = stroke;
            context.lineWidth = 1.5;
            let previous = null;
            for (const point of line.points) {
                const px = x(centre(point));
                const py = y(point.y);
                context.beginPath();
                if (point.min !== null && point.max !== null && point.min !== point.max) {
                    context.globalAlpha = .42;
                    context.moveTo(px, y(point.min));
                    context.lineTo(px, y(point.max));
                    context.stroke();
                    context.globalAlpha = 1;
                    context.beginPath();
                }
                if (previous?.continuous && point.continuous && previous.end === point.x) {
                    context.moveTo(x(centre(previous)), y(previous.y));
                    context.lineTo(px, py);
                    context.stroke();
                } else {
                    context.fillRect(px - 1.5, py - 1.5, 3, 3);
                }
                previous = point;
            }
        });
        if (points[selected]) {
            context.strokeStyle = color.getPropertyValue("--mud-palette-text-secondary");
            context.setLineDash([3, 3]);
            context.beginPath();
            const axis = x(centre(points[selected]));
            context.moveTo(axis, top);
            context.lineTo(axis, bottom);
            context.stroke();
            context.setLineDash([]);
        }
    };

    const show = index => {
        if (!points.length || selected === index) return;
        selected = index;
        const description = points[index]?.description;
        tooltip.textContent = description ?? instruction;
        tooltip.title = description ?? "";
        if (tooltip.parentElement) tooltip.parentElement.scrollTop = 0;
        render();
    };
    const move = event => {
        const local = event.clientX - canvas.getBoundingClientRect().left;
        let closest = 0;
        for (let index = 1; index < points.length; index++) {
            if (Math.abs(x(centre(points[index])) - local) < Math.abs(x(centre(points[closest])) - local)) closest = index;
        }
        show(closest);
    };
    const keys = event => {
        if (event.key !== "ArrowRight" && event.key !== "ArrowLeft") return;
        event.preventDefault();
        show(Math.max(0, Math.min(points.length - 1, selected + (event.key === "ArrowRight" ? 1 : -1))));
    };
    const reset = () => show(-1);
    const observer = new ResizeObserver(render);
    observer.observe(canvas);
    canvas.addEventListener("pointermove", move);
    canvas.addEventListener("pointerleave", reset);
    canvas.addEventListener("keydown", keys);
    render();
    return {
        dispose() {
            observer.disconnect();
            canvas.removeEventListener("pointermove", move);
            canvas.removeEventListener("pointerleave", reset);
            canvas.removeEventListener("keydown", keys);
        }
    };
}
