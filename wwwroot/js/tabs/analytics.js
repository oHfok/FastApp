/* ==========================================================
   ANALYTICS

   A report, not a dashboard. The engine on the desktop side does every
   calculation and hands this page finished observations with their evidence
   attached; nothing here works anything out. That split is the point: an
   insight assembled from three numbers in JavaScript has nothing to show when
   somebody asks why it thinks so.

   So this file only renders. If a sentence here is wrong, the fault is in
   Services/Analytics, which is where it can be tested.
   ========================================================== */

(function () {
    const KIND = {
        change:    { label: 'Something changed', mark: '↗' },
        focus:     { label: 'Focus',             mark: '◎' },
        routine:   { label: 'Your routine',      mark: '⟳' },
        pattern:   { label: 'A pattern',         mark: '◈' },
        discovery: { label: 'New',               mark: '✦' },
        anomaly:   { label: 'Unusual',           mark: '!' }
    };

    let loaded = false;

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    function insightCard(insight) {
        const kind = KIND[insight.kind] || { label: insight.kind, mark: '·' };
        const card = el('article', 'an-insight');

        const head = el('div', 'an-insight-head');
        head.appendChild(el('span', 'an-kind-mark', kind.mark));
        head.appendChild(el('span', 'an-kind', kind.label));
        if (insight.trend) {
            head.appendChild(el('span', 'an-trend an-trend-' + insight.trend,
                insight.trend === 'up' ? 'higher'
                : insight.trend === 'down' ? 'lower'
                : insight.trend));
        }
        card.appendChild(head);

        card.appendChild(el('h3', 'an-insight-title', insight.title));
        if (insight.explanation) card.appendChild(el('p', 'an-insight-body', insight.explanation));

        // The evidence is shown rather than tucked away. Being told something
        // about yourself is worth very little without the measurements that
        // led to it.
        if (insight.evidence && insight.evidence.length) {
            const list = el('ul', 'an-evidence');
            for (const line of insight.evidence) {
                if (line) list.appendChild(el('li', null, line));
            }
            card.appendChild(list);
        }

        if (insight.recommendation) {
            const rec = el('p', 'an-rec');
            rec.appendChild(el('span', 'an-rec-label', 'Worth trying'));
            rec.appendChild(document.createTextNode(insight.recommendation));
            card.appendChild(rec);
        }

        const foot = el('div', 'an-insight-foot');
        foot.appendChild(el('span', null, insight.period || ''));
        foot.appendChild(el('span', 'an-confidence',
            `confidence ${Math.round((insight.confidence || 0) * 100)}%`));
        card.appendChild(foot);
        return card;
    }

    function hourShape(hours) {
        const wrap = el('div', 'an-hours');
        const peak = Math.max(1, ...hours);
        hours.forEach((minutes, hour) => {
            const col = el('div', 'an-hour');
            const bar = el('div', 'an-hour-bar');
            // A floor of 2% so an hour with a little activity is still visible
            // as a mark rather than reading as an hour with none.
            bar.style.height = minutes > 0
                ? Math.max(2, (minutes / peak) * 100) + '%'
                : '0';
            bar.title = `${String(hour).padStart(2, '0')}:00 — ${Math.round(minutes)} min`;
            col.appendChild(bar);
            if (hour % 6 === 0) col.appendChild(el('span', 'an-hour-label', String(hour).padStart(2, '0')));
            wrap.appendChild(col);
        });
        return wrap;
    }

    function render(report) {
        const root = document.getElementById('an-body');
        root.textContent = '';

        if (!report.hasEnoughHistory) {
            root.appendChild(el('p', 'an-empty', report.notYet || 'Nothing recorded yet.'));
            return;
        }

        // --- the week in a number -------------------------------------
        const summary = el('section', 'an-summary');
        const figure = el('div', 'an-figure');
        figure.appendChild(el('div', 'an-figure-value', report.activeTotal));
        figure.appendChild(el('div', 'an-figure-label', 'active over ' + report.period));
        if (report.hasComparison && report.activeChangePercent) {
            const up = report.activeChangePercent > 0;
            figure.appendChild(el('div', 'an-delta ' + (up ? 'up' : 'down'),
                `${up ? '↑' : '↓'} ${Math.abs(report.activeChangePercent)}% a day against your usual`));
        }
        summary.appendChild(figure);
        summary.appendChild(el('p', 'an-headline', report.headline));
        root.appendChild(summary);

        // Said plainly rather than hidden: a page that compares you against
        // yourself is worth nothing until it knows what you are usually like.
        if (report.notYet) root.appendChild(el('p', 'an-learning', report.notYet));

        // --- when the days happen -------------------------------------
        if (report.hourShape && report.hourShape.some(h => h > 0)) {
            const section = el('section', 'an-block');
            section.appendChild(el('h2', 'an-block-title', 'When your days happen'));
            section.appendChild(hourShape(report.hourShape));
            root.appendChild(section);
        }

        // --- what was found -------------------------------------------
        if (report.insights && report.insights.length) {
            const section = el('section', 'an-block');
            section.appendChild(el('h2', 'an-block-title', 'What stood out'));
            const grid = el('div', 'an-insights');
            report.insights.forEach(i => grid.appendChild(insightCard(i)));
            section.appendChild(grid);
            root.appendChild(section);
        } else {
            root.appendChild(el('p', 'an-empty',
                'Nothing stood out this week — your use of the computer looked much like it usually does.'));
        }

        // --- what the time was made of --------------------------------
        if (report.topApps && report.topApps.length) {
            const section = el('section', 'an-block');
            section.appendChild(el('h2', 'an-block-title', 'What the time was made of'));
            const list = el('div', 'an-apps');
            for (const app of report.topApps) {
                const row = el('div', 'an-app');
                row.appendChild(el('span', 'an-app-name', app.name));

                const track = el('span', 'an-app-track');
                const fill = el('span', 'an-app-fill');
                fill.style.width = Math.max(1, app.share) + '%';
                track.appendChild(fill);
                row.appendChild(track);

                row.appendChild(el('span', 'an-app-time', app.time));
                if (app.changePercent) {
                    row.appendChild(el('span',
                        'an-app-change ' + (app.changePercent > 0 ? 'up' : 'down'),
                        `${app.changePercent > 0 ? '+' : ''}${app.changePercent}%`));
                } else {
                    row.appendChild(el('span', 'an-app-change', ''));
                }
                list.appendChild(row);
            }
            section.appendChild(list);
            root.appendChild(section);
        }

        root.appendChild(el('p', 'an-privacy',
            'Everything on this page was worked out on this computer, from data that never left it.'));
    }

    async function load() {
        const root = document.getElementById('an-body');
        root.textContent = '';
        root.appendChild(el('p', 'an-empty', 'Reading your history…'));
        try {
            const res = await fetch('/api/analytics');
            if (!res.ok) throw new Error('The analytics engine returned ' + res.status);
            render(await res.json());
            loaded = true;
        } catch (err) {
            root.textContent = '';
            root.appendChild(el('p', 'an-empty', 'Could not build the report: ' + err.message));
        }
    }

    window.Dashboard = window.Dashboard || {};
    window.Dashboard.tabs = window.Dashboard.tabs || {};
    window.Dashboard.tabs.analytics = {
        // Built once a visit. It reads several weeks of sessions, and nothing in
        // it changes minute to minute.
        onEnter() { if (!loaded) load(); },
        reload: load
    };
})();
