/* ==========================================================
   TAB: OVERVIEW (default tab)
   Timestamp selector (Day/Week/Month/Year) drives everything.
   ========================================================== */

function setOverviewScope(scope, btnEl) {
    selectedScope = scope;
    document.querySelectorAll('#overview-scope button').forEach(b => b.classList.toggle('active', b === btnEl));
    loadOverview();
}

// Range actually summed by the backend for each scope, so the label always
// matches the number shown (Week/Month/Year are "to date", not full periods).
function computeScopeRange(scope, dateStr) {
    const target = parseDateStr(dateStr);
    if (scope === 'day') return { start: target, end: target };
    if (scope === 'week') return { start: mondayOf(target), end: target };
    if (scope === 'month') { const s = new Date(target); s.setDate(s.getDate() - 29); return { start: s, end: target }; }
    const s = new Date(target); s.setDate(s.getDate() - 364); return { start: s, end: target };
}

function renderRangeLabel(scope, dateStr) {
    const { start, end } = computeScopeRange(scope, dateStr);
    const label = document.getElementById('ov-range-label');
    if (scope === 'day') { label.textContent = `${DAY_NAMES[isoDow(start)]}, ${fmtDateEU(start)}`; return; }
    label.textContent = `${fmtDateEU(start)} → ${fmtDateEU(end)}`;
}

async function loadOverview() {
    const scope = getSelectedScope();
    const dateStr = getSelectedDate();
    renderRangeLabel(scope, dateStr);

    const activityLabel = document.getElementById('ov-activity-label');
    activityLabel.textContent = {
        day: "Today's Timeline",
        week: "This Week's Activity",
        month: "Last 30 Days",
        year: "Last 365 Days"
    }[scope];

    try {
        const [ovRes, lbRes] = await Promise.all([
            fetch(`/api/overview?date=${dateStr}`),
            fetch(`/api/leaderboard?timeframe=${scope}&date=${dateStr}`)
        ]);
        const ov = await ovRes.json();
        const leaderboard = await lbRes.json();

        renderComparisonBlock(scope, ov);
        renderCategoryBar(leaderboard);
        renderOverviewLeaderboards(leaderboard);
        await renderActivityBody(scope, dateStr, ov);
    } catch (err) {
        console.error('Overview load failed', err);
    }
}

function renderComparisonBlock(scope, ov) {
    const focusMap = {
        day: { cur: ov.focusToday, prev: ov.prevFocusToday, label: 'yesterday' },
        week: { cur: ov.focusWeek, prev: ov.prevFocusWeek, label: 'last week' },
        month: { cur: ov.focusMonth, prev: ov.prevFocusMonth, label: 'last month' },
        year: { cur: ov.focusYear, prev: ov.prevFocusYear, label: 'last year' }
    };
    const { cur, prev, label } = focusMap[scope];
    const scopeLabel = { day: 'today', week: 'this week', month: 'this month', year: 'this year' }[scope];

    document.getElementById('ov-hero-label').textContent = `Focus ${scopeLabel}`;
    document.getElementById('ov-hero-value').textContent = formatHours(cur || 0);
    document.getElementById('ov-hero-trend').innerHTML = trendPill(cur || 0, prev || 0, label);

    const hoverTip = `${label.charAt(0).toUpperCase() + label.slice(1)}: ${formatHours(prev || 0)}`;
    const heroEl = document.getElementById('ov-hero-block');
    heroEl.onmousemove = (e) => showTooltip(e, hoverTip);
    heroEl.onmouseleave = hideTooltip;

    // AFK per scope. Backend field names: AfkToday / AfkWeek / AfkMonth / AfkYear
    // (Week/Month/Year require the afk-overview-patch.cs addition — falls back
    // to a dash with an explanatory tooltip if those fields aren't there yet.)
    const afkMap = {
        day: ov.afkToday,
        week: ov.afkWeek,
        month: ov.afkMonth,
        year: ov.afkYear
    };
    const afkVal = afkMap[scope];
    const afkEl = document.getElementById('ov-afk-value');
    const afkCard = document.getElementById('ov-afk-card');
    if (afkVal !== undefined && afkVal !== null) {
        afkCard.style.opacity = '1';
        afkEl.textContent = formatHours(afkVal || 0);
        afkCard.onmousemove = null;
        afkCard.onmouseleave = null;
    } else {
        afkCard.style.opacity = '0.5';
        afkEl.textContent = '—';
        afkCard.onmousemove = (e) => showTooltip(e, `AFK for "${scope}" needs the afk-overview-patch.cs addition on the backend.`);
        afkCard.onmouseleave = hideTooltip;
    }
}

function renderCategoryBar(leaderboard) {
    const totals = {};
    (leaderboard || []).forEach(app => {
        const cat = app.category || 'Uncategorized';
        totals[cat] = (totals[cat] || 0) + (app.focusedMinutes || 0);
    });
    const entries = Object.entries(totals).sort((a, b) => b[1] - a[1]);
    const totalMins = entries.reduce((s, [, v]) => s + v, 0);

    const barEl = document.getElementById('ov-cat-bar');
    const legendEl = document.getElementById('ov-cat-legend');

    if (totalMins === 0) {
        barEl.innerHTML = `<div class="cat-bar-seg" style="width:100%;background:var(--panel-border)"></div>`;
        legendEl.innerHTML = `<span style="color:var(--text-faint);font-size:12px;">No category data for this period.</span>`;
        return;
    }

    barEl.innerHTML = entries.map(([cat, mins]) => {
        const pct = (mins / totalMins) * 100;
        return `<div class="cat-bar-seg" style="width:${pct}%;background:${catColor(cat)}" title="${cat}: ${formatTime(mins)}"></div>`;
    }).join('');

    legendEl.innerHTML = entries.map(([cat, mins]) => {
        const pct = Math.round((mins / totalMins) * 100);
        return `<div class="cat-legend-item"><span class="cat-swatch" style="background:${catColor(cat)}"></span>${cat} <span class="mono" style="color:var(--text-faint)">${pct}% · ${formatTime(mins)}</span></div>`;
    }).join('');
}

function renderOverviewLeaderboards(leaderboard) {
    const apps = [...(leaderboard || [])].sort((a, b) => b.focusedMinutes - a.focusedMinutes).slice(0, 8);
    const appsEl = document.getElementById('ov-lb-apps');
    if (apps.length === 0) {
        appsEl.innerHTML = `<div class="empty-state">No app activity for this period.</div>`;
    } else {
        appsEl.innerHTML = apps.map((app, i) => `
            <div class="lb-row app-link" onclick="openDrilldown('${app.appName.replace(/'/g, "&#39;")}')">
                <div class="lb-rank">${i + 1}</div>
                <div class="lb-name">${app.appName}</div>
                <div class="lb-time">${formatTime(app.focusedMinutes)}</div>
            </div>`).join('');
    }

    const catTotals = {};
    (leaderboard || []).forEach(app => {
        const cat = app.category || 'Uncategorized';
        catTotals[cat] = (catTotals[cat] || 0) + (app.focusedMinutes || 0);
    });
    const cats = Object.entries(catTotals).sort((a, b) => b[1] - a[1]).slice(0, 8);
    const catsEl = document.getElementById('ov-lb-categories');
    if (cats.length === 0) {
        catsEl.innerHTML = `<div class="empty-state">No category activity for this period.</div>`;
    } else {
        catsEl.innerHTML = cats.map(([cat, mins], i) => `
            <div class="lb-row">
                <div class="lb-rank">${i + 1}</div>
                <div class="lb-name"><span class="cat-swatch" style="background:${catColor(cat)};display:inline-block;margin-right:8px;"></span>${cat}</div>
                <div class="lb-time">${formatTime(mins)}</div>
            </div>`).join('');
    }
}

// --- Activity body: day ribbon, or a heatmap for week/month/year ------------
async function renderActivityBody(scope, dateStr, ov) {
    const body = document.getElementById('ov-activity-body');
    if (scope === 'day') {
        body.innerHTML = `
            <div class="timeline-wrap">
                <div class="timeline-ticks"><span>00:00</span><span>06:00</span><span>12:00</span><span>18:00</span><span>24:00</span></div>
                <div class="timeline-track" id="ov-timeline-track"></div>
            </div>`;
        await renderDayTimeline(dateStr);
    } else if (scope === 'week') {
        body.innerHTML = `<div class="empty-state">Loading…</div>`;
        await renderWeekHeatmap(dateStr);
    } else {
        renderDayHeatmap(scope, dateStr, ov.yearlyHeatmap || ov.YearlyHeatmap || []);
    }
}

async function renderDayTimeline(dateStr) {
    try {
        const res = await fetch(`/api/timeline?date=${dateStr}`);
        const sessions = await res.json();
        const track = document.getElementById('ov-timeline-track');
        if (!track) return;

        if (!sessions || sessions.length === 0) {
            track.innerHTML = `<div class="empty-state" style="border:none;background:none;">No sessions recorded for this day.</div>`;
            return;
        }

        const escAttr = (s) => String(s).replace(/'/g, '&#39;');

        track.innerHTML = sessions.map(s => {
            const name = s.appName || s.AppName;
            const cat = s.category || s.Category;
            const startStr = s.start || s.Start;
            const endStr = s.end || s.End;
            const dur = s.durationMinutes ?? s.DurationMinutes ?? 0;
            const startMins = s.startMinutes ?? s.StartMinutes ?? 0;
            const left = (startMins / 1440) * 100;
            const width = Math.max((dur / 1440) * 100, 0.25);
            const tip = `${escAttr(name)}<br>${startStr} &ndash; ${endStr}<br>${formatTime(dur)}`;
            return `<div class="timeline-seg" style="left:${left}%;width:${width}%;background:${catColor(cat)}"
                        onmousemove="showTooltip(event, '${tip}')" onmouseleave="hideTooltip()"
                        onclick="openDrilldown('${escAttr(name)}')"></div>`;
        }).join('');
    } catch (err) { console.error(err); }
}

// Week scope: hour-by-day heatmap (7 rows x 24 cols) built from the same
// range the Week focus number covers (Monday of that week -> selected date).
async function renderWeekHeatmap(dateStr) {
    const body = document.getElementById('ov-activity-body');
    const target = parseDateStr(dateStr);
    const monday = mondayOf(target);

    const dayDates = [];
    for (let i = 0; i < 7; i++) { const d = new Date(monday); d.setDate(monday.getDate() + i); dayDates.push(d); }

    // Grid rows for days after the selected date stay empty (no future data).
    const grid = Array.from({ length: 7 }, () => new Array(24).fill(0));

    try {
        const results = await Promise.all(dayDates.map(async (d, dayIdx) => {
            if (d > target) return null;
            const ds = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
            const res = await fetch(`/api/timeline?date=${ds}`);
            return { dayIdx, sessions: await res.json() };
        }));

        results.forEach(r => {
            if (!r) return;
            (r.sessions || []).forEach(s => {
                const startMins = s.startMinutes ?? s.StartMinutes ?? 0;
                const dur = s.durationMinutes ?? s.DurationMinutes ?? 0;
                const hour = Math.floor(startMins / 60);
                if (hour >= 0 && hour < 24) grid[r.dayIdx][hour] += dur;
            });
        });
    } catch (err) {
        console.error('Week heatmap load failed', err);
    }

    const maxMins = Math.max(...grid.flat(), 1);

    let cellsHtml = '';
    for (let day = 0; day < 7; day++) {
        for (let hour = 0; hour < 24; hour++) {
            const mins = grid[day][hour];
            const intensity = mins / maxMins;
            const bg = intensity > 0
                ? `rgba(232,163,61,${0.15 + intensity * 0.75})`
                : 'var(--bg-raised)';
            const tip = `${DAY_SHORT[day]} at ${pad(hour)}:00<br>${formatTime(mins)} focused`;
            cellsHtml += `<div class="heat-cell" style="background:${bg}" onmousemove="showTooltip(event, '${tip}')" onmouseleave="hideTooltip()"></div>`;
        }
    }

    body.innerHTML = `
        <div class="heat-hours-wrap">
            <div class="heat-day-labels">${DAY_SHORT.map(d => `<span>${d}</span>`).join('')}</div>
            <div class="heat-hours-grid">${cellsHtml}</div>
        </div>
        <div class="heat-hours-ticks"><span>00:00</span><span>06:00</span><span>12:00</span><span>18:00</span><span>24:00</span></div>`;
}

// Month/Year scope: day-cell calendar heatmap, sliced from the same 365-day
// series /api/overview already returns for the yearly heatmap.
function renderDayHeatmap(scope, dateStr, heatData) {
    const body = document.getElementById('ov-activity-body');
    const days = scope === 'month' ? 30 : 365;
    const target = parseDateStr(dateStr);
    const oldest = new Date(target);
    oldest.setDate(oldest.getDate() - (days - 1));

    const maxMins = Math.max(...heatData.map(d => d.focusedMinutes ?? d.FocusedMinutes ?? 0), 1);

    let cellsHtml = '';
    for (let i = 0; i < days; i++) {
        const d = new Date(oldest);
        d.setDate(oldest.getDate() + i);
        const ds = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
        const match = heatData.find(x => (x.date || x.Date) === ds);
        const mins = match ? (match.focusedMinutes ?? match.FocusedMinutes ?? 0) : 0;
        const intensity = mins / maxMins;
        const bg = intensity > 0 ? `rgba(232,163,61,${0.15 + intensity * 0.75})` : 'var(--bg-raised)';
        const tip = `${fmtDateEU(d)}<br>${formatTime(mins)} focused`;
        cellsHtml += `<div class="heat-day-cell" style="background:${bg}" onmousemove="showTooltip(event, '${tip}')" onmouseleave="hideTooltip()"></div>`;
    }

    // Fixed cell caps (not 1fr) so a low column count (month = 5) doesn't stretch
    // cells across the full card width and blow the grid apart.
    const cols = Math.ceil(days / 7);
    const cellCap = scope === 'month' ? 28 : 13;

    body.innerHTML = `
        <div class="heat-days-grid" style="grid-template-columns:repeat(${cols}, minmax(0, ${cellCap}px));">${cellsHtml}</div>
        <div class="heat-legend">
            <span>Less</span>
            <span class="heat-legend-swatch" style="background:var(--bg-raised)"></span>
            <span class="heat-legend-swatch" style="background:rgba(232,163,61,0.3)"></span>
            <span class="heat-legend-swatch" style="background:rgba(232,163,61,0.6)"></span>
            <span class="heat-legend-swatch" style="background:rgba(232,163,61,0.9)"></span>
            <span>More</span>
        </div>`;
}

Dashboard.tabs.overview = { onEnter: loadOverview };
