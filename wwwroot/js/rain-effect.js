(function () {
    let canvas, ctx, drops = [], runs = [], w, h, raf = null;

    function resize() {
        w = canvas.width = window.innerWidth;
        h = canvas.height = window.innerHeight;
    }

    function makeDrop() {
        return {
            x: Math.random() * w,
            y: Math.random() * -h,
            len: 10 + Math.random() * 20,
            speed: 4 + Math.random() * 6,
            drift: 1.2 + Math.random() * 0.6, // slight wind angle
            opacity: 0.15 + Math.random() * 0.35,
            width: Math.random() < 0.15 ? 2 : 1
        };
    }

    // Slow "runs" that cling to the glass and slide, leaving a trail
    function makeRun() {
        return {
            x: Math.random() * w,
            y: Math.random() * h,
            speed: 0.3 + Math.random() * 0.8,
            wobble: Math.random() * Math.PI * 2,
            len: 0,
            maxLen: 30 + Math.random() * 60,
            opacity: 0.1 + Math.random() * 0.2
        };
    }

    function step() {
        ctx.clearRect(0, 0, w, h);

        // Fast streaking rain
        ctx.lineCap = 'round';
        for (const d of drops) {
            ctx.strokeStyle = `rgba(210, 230, 245, ${d.opacity})`;
            ctx.lineWidth = d.width;
            ctx.beginPath();
            ctx.moveTo(d.x, d.y);
            ctx.lineTo(d.x - d.drift * 2, d.y + d.len);
            ctx.stroke();

            d.y += d.speed;
            d.x -= d.drift * 0.4;
            if (d.y > h + d.len) Object.assign(d, makeDrop(), { y: -d.len });
        }

        // Slow sliding runs (the "stuck to glass" droplets)
        for (const r of runs) {
            ctx.strokeStyle = `rgba(220, 235, 250, ${r.opacity})`;
            ctx.lineWidth = 1.4;
            ctx.beginPath();
            ctx.moveTo(r.x + Math.sin(r.wobble) * 2, r.y);
            ctx.lineTo(r.x + Math.sin(r.wobble + 0.5) * 2, r.y - r.len);
            ctx.stroke();

            r.y += r.speed;
            r.wobble += 0.02;
            if (r.len < r.maxLen) r.len += 0.4;
            if (r.y - r.len > h) Object.assign(r, makeRun(), { y: -10, len: 0 });
        }

        raf = requestAnimationFrame(step);
    }

    function start() {
        canvas = document.getElementById('rain-canvas');
        if (!canvas || raf) return;
        ctx = canvas.getContext('2d');
        resize();
        drops = Array.from({ length: 180 }, makeDrop);
        runs = Array.from({ length: 25 }, makeRun);
        window.addEventListener('resize', resize);
        step();
    }

    function stop() {
        if (raf) cancelAnimationFrame(raf);
        raf = null;
    }

    // Only run the animation loop while the rainforest theme is active,
    // so it costs zero CPU in every other theme
    const observer = new MutationObserver(() => {
        document.body.classList.contains('rainforest') ? start() : stop();
    });
    document.addEventListener('DOMContentLoaded', () => {
        observer.observe(document.body, { attributes: true, attributeFilter: ['class'] });
        if (document.body.classList.contains('rainforest')) start();
    });
})();