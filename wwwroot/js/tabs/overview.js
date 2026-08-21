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

    // One signal for the whole tab: switching scope quickly aborts the previous
    // set rather than letting a slower earlier response land last and repaint
    // the view with data the user already navigated away from.
    const signal = abortableSignal('overview');

    try {
        const [ov, leaderboard] = await Promise.all([
            apiFetch(`/api/overview?date=${dateStr}`, { signal }),
            apiFetch(`/api/leaderboard?timeframe=${scope}&date=${dateStr}`, { signal })
        ]);

        // Share this payload with the top bar so its 30-second poll doesn't
        // re-request the same (365-day) response for four numbers.
        cacheOverviewPayload(ov);

        renderComparisonBlock(scope, ov);
        renderCategoryBar(leaderboard);
        renderOverviewLeaderboards(leaderboard);
        await renderActivityBody(scope, dateStr, ov, signal);
    } catch (err) {
        if (isAbort(err)) return; // superseded by a newer request — not a failure
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

    // Total PC uptime (focus + AFK + anything else). Shown alongside Focus/AFK
    // so those two numbers read as a share of something, not just in isolation.
    const uptimeMap = {
        day: { cur: ov.totalToday, prev: ov.prevTotalToday, label: 'yesterday' },
        week: { cur: ov.totalWeek, prev: ov.prevTotalWeek, label: 'last week' },
        month: { cur: ov.totalMonth, prev: ov.prevTotalMonth, label: 'last month' },
        year: { cur: ov.totalYear, prev: ov.prevTotalYear, label: 'last year' }
    };
    const { cur: upCur, prev: upPrev, label: upLabel } = uptimeMap[scope];

    document.getElementById('ov-uptime-label').textContent = `Uptime ${scopeLabel}`;
    document.getElementById('ov-uptime-value').textContent = formatHours(upCur || 0);
    document.getElementById('ov-uptime-trend').innerHTML = trendPill(upCur || 0, upPrev || 0, upLabel);

    const upHoverTip = `${upLabel.charAt(0).toUpperCase() + upLabel.slice(1)}: ${formatHours(upPrev || 0)}`;
    const uptimeEl = document.getElementById('ov-uptime-card');
    uptimeEl.onmousemove = (e) => showTooltip(e, upHoverTip);
    uptimeEl.onmouseleave = hideTooltip;

    // AFK per scope. The backend returns all four (AfkToday/Week/Month/Year); a
    // missing value here means the response itself didn't arrive intact, so the
    // card dims to a dash rather than showing a stale or invented number. It used
    // to explain the gap by naming a C# patch file to apply, which was a
    // development leftover that outlived the work it described.
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
        afkCard.onmousemove = (e) => showTooltip(e, 'No AFK figure came back for this range.');
        afkCard.onmouseleave = hideTooltip;
    }
}

function renderCategoryBar(leaderboard) {
    const totals = {};
    (leaderboard || []).forEach(app => {
        const cat = app.category || 'Other';
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
        // Purely a coloured slice — no text inside, so it needs an explicit
        // name or it announces as an unlabelled button.
        const segLabel = `${cat}, ${formatTime(mins)}`;
        return `<div class="cat-bar-seg cat-link" style="width:${pct}%;background:${catColor(cat)}" title="${escapeHtml(segLabel)}" aria-label="${escapeHtml(segLabel)}" data-open-cat="${escapeHtml(cat)}" role="button" tabindex="0"></div>`;
    }).join('');

    legendEl.innerHTML = entries.map(([cat, mins]) => {
        const pct = Math.round((mins / totalMins) * 100);
        return `<div class="cat-legend-item cat-link" data-open-cat="${escapeHtml(cat)}" role="button" tabindex="0"><span class="cat-swatch" style="background:${catColor(cat)}"></span>${escapeHtml(cat)} <span class="mono" style="color:var(--text-faint)">${pct}% · ${formatTime(mins)}</span></div>`;
    }).join('');
}

function renderOverviewLeaderboards(leaderboard) {
    const apps = [...(leaderboard || [])].sort((a, b) => b.focusedMinutes - a.focusedMinutes).slice(0, 8);
    const appsEl = document.getElementById('ov-lb-apps');
    if (apps.length === 0) {
        appsEl.innerHTML = `<div class="empty-state">No app activity for this period.</div>`;
    } else {
        appsEl.innerHTML = apps.map((app, i) => `
            <div class="lb-row app-link" data-open-app="${escapeHtml(app.appName)}" role="button" tabindex="0">
                <div class="lb-rank">${i + 1}</div>
                <div class="lb-name">${escapeHtml(app.appName)}</div>
                <div class="lb-time">${formatTime(app.focusedMinutes)}</div>
            </div>`).join('');
    }

    const catTotals = {};
    (leaderboard || []).forEach(app => {
        const cat = app.category || 'Other';
        catTotals[cat] = (catTotals[cat] || 0) + (app.focusedMinutes || 0);
    });
    const cats = Object.entries(catTotals).sort((a, b) => b[1] - a[1]).slice(0, 8);
    const catsEl = document.getElementById('ov-lb-categories');
    if (cats.length === 0) {
        catsEl.innerHTML = `<div class="empty-state">No category activity for this period.</div>`;
    } else {
        catsEl.innerHTML = cats.map(([cat, mins], i) => `
            <div class="lb-row app-link" data-open-cat="${escapeHtml(cat)}" role="button" tabindex="0">
                <div class="lb-rank">${i + 1}</div>
                <div class="lb-name"><span class="cat-swatch" style="background:${catColor(cat)};display:inline-block;margin-right:8px;"></span>${escapeHtml(cat)}</div>
                <div class="lb-time">${formatTime(mins)}</div>
            </div>`).join('');
    }
}

// --- Activity body: day ribbon, or a heatmap for week/month/year ------------
async function renderActivityBody(scope, dateStr, ov, signal) {
    const body = document.getElementById('ov-activity-body');
    if (scope === 'day') {
        body.innerHTML = `
            <div class="timeline-wrap">
                <div class="timeline-ticks"><span>00:00</span><span>06:00</span><span>12:00</span><span>18:00</span><span>24:00</span></div>
                <div class="timeline-track" id="ov-timeline-track"></div>
            </div>`;
        await renderDayTimeline(dateStr, signal);
    } else if (scope === 'week') {
        body.innerHTML = `<div class="empty-state">Loading…</div>`;
        await renderWeekHeatmap(dateStr, ov, signal);
    } else {
        renderDayHeatmap(scope, dateStr, ov.yearlyHeatmap || ov.YearlyHeatmap || []);
    }
}

async function renderDayTimeline(dateStr, signal) {
    try {
        const sessions = await apiFetch(`/api/timeline?date=${dateStr}`, { signal });
        const track = document.getElementById('ov-timeline-track');
        if (!track) return;
        track.innerHTML = timelineSegmentsHtml(sessions);
    } catch (err) { if (!isAbort(err)) console.error(err); }
}

// Week scope: a per-day focus bar chart (same component the Weeks & Months
// detail page uses, for visual consistency) on top of an hour-by-day heatmap
// (7 rows x 24 cols), both built from the same range the Week focus number
// covers (Monday of that week -> selected date).
async function renderWeekHeatmap(dateStr, ov, signal) {
    const body = document.getElementById('ov-activity-body');
    const target = parseDateStr(dateStr);
    const monday = mondayOf(target);

    // Day-total bars come straight from the yearly heatmap series /api/overview
    // already returned — no extra fetch needed, just slice out this week.
    // Indexed by date first: this used to run .find() over the full 365-entry
    // series once per day drawn, and the same pattern in the year heatmap below
    // meant ~133,000 comparisons per render, repeating on every poll.
    const yearlyHeatmap = (ov && (ov.yearlyHeatmap || ov.YearlyHeatmap)) || [];
    const byDate = new Map(yearlyHeatmap.map(x =>
        [x.date || x.Date, x.focusedMinutes ?? x.FocusedMinutes ?? 0]));

    const weekDays = [];
    for (let i = 0; i < 7; i++) {
        const d = new Date(monday); d.setDate(monday.getDate() + i);
        if (d > target) break;
        const ds = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
        weekDays.push({ date: ds, focusedMinutes: byDate.get(ds) ?? 0 });
    }
    const dayBarsHtml = weekDays.length ? `
        <div class="card-label" style="margin-bottom:14px;">Daily Focus</div>
        ${weekHeatmapHtml(weekDays)}
        <div class="ov-week-divider"></div>` : '';

    // One request for the whole 7x24 grid. This was previously seven parallel
    // /api/timeline calls — one per day — re-issued on every 12-second poll.
    let grid = Array.from({ length: 7 }, () => new Array(24).fill(0));
    try {
        const data = await apiFetch(`/api/week-heatmap?date=${dateStr}`, { signal });
        const returned = data.grid ?? data.Grid;
        if (Array.isArray(returned) && returned.length === 7) grid = returned;
    } catch (err) {
        if (isAbort(err)) return;
        console.error('Week heatmap load failed', err);
    }

    const maxMins = Math.max(...grid.flat(), 1);

    let cellsHtml = '';
    for (let day = 0; day < 7; day++) {
        for (let hour = 0; hour < 24; hour++) {
            const mins = grid[day][hour];
            const intensity = mins / maxMins;
            const bg = heatColor(intensity);
            const tip = `${DAY_SHORT[day]} at ${pad(hour)}:00<br>${formatTime(mins)} focused`;
            cellsHtml += `<div class="heat-cell" style="background:${bg}" onmousemove="showTooltip(event, '${tip}')" onmouseleave="hideTooltip()"></div>`;
        }
    }

    body.innerHTML = `
        ${dayBarsHtml}
        <div class="card-label" style="margin-bottom:14px;">Hour-by-Hour</div>
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

    // Indexed once instead of a linear .find() per cell — at year scope that
    // was 365 scans of a 365-entry array on every render, every poll tick.
    const byDate = new Map(heatData.map(x =>
        [x.date || x.Date, x.focusedMinutes ?? x.FocusedMinutes ?? 0]));

    let cellsHtml = '';
    for (let i = 0; i < days; i++) {
        const d = new Date(oldest);
        d.setDate(oldest.getDate() + i);
        const ds = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
        const mins = byDate.get(ds) ?? 0;
        const intensity = mins / maxMins;
        const bg = heatColor(intensity);
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
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.3)}"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.6)}"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.9)}"></span>
            <span>More</span>
        </div>`;
}

Dashboard.tabs.overview = { onEnter: loadOverview, refresh: loadOverview };
