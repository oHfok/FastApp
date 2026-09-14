/* ==========================================================
   TAB: PERIODS (Days / Weeks / Months / Years)
   Toggle between the four period types (default Weeks). Each
   entry: range, total focus, most-used app, rank vs all other
   periods of that type. Click -> detail subpage comparing to
   prev/next/current period. Day's "Daily Activity" section is
   the Timeline ribbon (session-by-session), not a heatmap —
   breaking a single day down into "days" would be circular.
   Backed by /api/periods + /api/period-detail.
   ========================================================== */

let periodType = 'week'; // 'day' | 'week' | 'month' | 'year'

// Recent (server order, newest first) / Best / Worst by total focus. Sorting
// is done client-side against the already-fetched list rather than a second
// request -- the server already returns every period's total, so re-sorting
// is instant and never needs to hit the network.
let periodSort = 'recent';
// The last-fetched list, each entry carrying its own prevFocusMinutes
// (computed once, while still in date order) so "vs Previous" keeps meaning
// "the period right before this one" no matter which order Best/Worst later
// displays them in.
let periodListData = [];

// Window Activity card state (Day period detail) — held here rather than
// re-fetched, since the raw titled sessions for the open day are already in
// hand; search/sort just re-filter and re-render the row list in place.
let waSessions = [];
let waSearch = '';
let waSort = 'newest'; // 'newest' | 'oldest'

// Identifies whichever period-detail is currently open ("day:2026-04-07"),
// so a poll refresh can tell "still looking at the same period" (keep
// Window Activity's search/sort as-is) apart from "just navigated to a new
// one" (reset them). Includes periodType, not just the date, since a Week
// and a Day can share the same raw startDate string.
let currentDetailKey = null;

function setPeriodType(type, btnEl) {
    periodType = type;
    document.querySelectorAll('#period-toggle button').forEach(b => b.classList.toggle('active', b === btnEl));
    showPeriodList();
    loadPeriodList();
}

function showPeriodList() {
    document.getElementById('period-list-view').style.display = 'block';
    document.getElementById('period-detail-view').style.display = 'none';
}

function setPeriodSort(sort, btnEl) {
    periodSort = sort;
    document.querySelectorAll('#period-sort-toggle button').forEach(b => b.classList.toggle('active', b === btnEl));
    renderPeriodList();
}

async function loadPeriodList(silent) {
    const listEl = document.getElementById('period-list');
    if (!silent && isEmptyContainer(listEl)) listEl.innerHTML = loadingRowsHtml(5);
    try {
        const periods = await apiFetch(`/api/periods?type=${periodType}`,
                                      { signal: abortableSignal('periods') });

        if (!periods || periods.length === 0) {
            periodListData = [];
            listEl.innerHTML = `<div class="empty-state">No ${periodType}s recorded yet.</div>`;
            return;
        }

        // The list arrives newest-first and the server already drops periods
        // with no activity, so periods[i + 1] is the previous period that had
        // any -- computed once here, before Best/Worst gets a chance to
        // reorder the array for display.
        periodListData = periods.map((p, i) => ({
            ...p,
            prevFocusMinutes: periods[i + 1] ? (periods[i + 1].totalFocusMinutes ?? 0) : null
        }));
        renderPeriodList();
    } catch (err) {
        // A failed silent poll shouldn't blow away an already-valid list with
        // an error message — just log it and leave what's on screen alone.
        if (silent) { console.error('Periods poll refresh failed', err); return; }
        listEl.innerHTML = errorStateHtml(
            `Couldn't load ${periodType}s`,
            'FastApp is running but this list did not come back. It usually means the app is busy or was restarting.',
            'retryPeriodList'
        );
    }
}

function renderPeriodList() {
    const listEl = document.getElementById('period-list');
    if (!periodListData.length) return;

    const sorted = [...periodListData].sort((a, b) => {
        if (periodSort === 'best') return (b.totalFocusMinutes ?? 0) - (a.totalFocusMinutes ?? 0);
        if (periodSort === 'worst') return (a.totalFocusMinutes ?? 0) - (b.totalFocusMinutes ?? 0);
        return b.startDate.localeCompare(a.startDate); // 'recent': server's own newest-first order
    });

    listEl.innerHTML = sorted.map((p) => {
        const rank = p.rank;
        const label = p.label;
        const start = p.startDate;
        const end = p.endDate;
        const totalMins = p.totalFocusMinutes ?? 0;
        const mostUsed = p.mostUsedApp ?? '—';
        // The right-hand column used to repeat the rank badge ("#4 of 8"),
        // spending one of the row's five slots on a fact the row already
        // stated. It now answers the question the list could not: was this
        // period better or worse than the one before it? The denominator
        // moved into the badge's tooltip so nothing was actually lost.
        const changeHtml = p.prevFocusMinutes != null
            ? trendPill(totalMins, p.prevFocusMinutes)
            : `<span class="trend-pill trend-flat">First on record</span>`;

        const rangeText = periodType === 'week'
            ? `${fmtDateEU(parseDateStr(start))} → ${fmtDateEU(parseDateStr(end))}`
            : periodType === 'day'
                ? DAY_NAMES[isoDow(parseDateStr(start))]
                : label;

        return `
            <div class="card period-card" data-open-period="${escapeHtml(start)}" role="button" tabindex="0">
                <div class="period-row">
                    <div class="period-rank-badge ${rank === 1 ? 'rank-1' : ''}" title="Rank ${rank ?? '–'} of ${p.totalPeriods ?? '–'} by focus time">#${rank ?? '–'}</div>
                    <div class="period-main">
                        <div class="period-label" title="${escapeHtml(label)}">${escapeHtml(label)}</div>
                        <div class="period-range">${escapeHtml(rangeText)}</div>
                    </div>
                    <div class="period-stats">
                        <div class="period-stat">
                            <div class="period-stat-label">Total Focus</div>
                            <div class="period-stat-value" style="color:var(--brass)">${formatTime(totalMins)}</div>
                        </div>
                        <div class="period-stat">
                            <div class="period-stat-label">Most Used</div>
                            <div class="period-stat-value app-link" title="${escapeHtml(mostUsed)}" data-open-app="${escapeHtml(mostUsed)}" role="button" tabindex="0">${escapeHtml(displayAppName(mostUsed))}</div>
                        </div>
                        <div class="period-stat">
                            <div class="period-stat-label">vs Previous</div>
                            <div class="period-stat-value">${changeHtml}</div>
                        </div>
                    </div>
                </div>
            </div>`;
    }).join('');
}

// Daily-activity visual for the period detail page.
// Day: not really a "daily activity" grid at all — a single day has nothing
// to break into days, so this renders the Timeline ribbon instead (the same
// component Overview uses), session-by-session across the 24h track.
// Week: only 7 days, so a bare heatmap square is too thin to read — show a
// labeled day-by-day strip instead (day name, colored block, time under it).
// Month: a real Mon-Sun calendar grid (like GitHub's contribution graph),
// not a flat sequential strip, so the shape of the month is recognizable.
// Year: same square-cell heatmap style as Month, but column-major over the
// whole year rather than weekday-aligned — matches Overview's existing year
// heatmap so the two don't look inconsistent with each other.
function periodHeatmapHtml(days, periodType, daySessions, dayAfkIntervals, dayMusicIntervals) {
    if (periodType === 'day') {
        return `
            <div class="timeline-wrap">
                <div class="timeline-ticks">${timelineTicksHtml(timelineWindow(daySessions, dayAfkIntervals, dayMusicIntervals))}</div>
                <div class="timeline-track">${timelineSegmentsHtml(daySessions, dayAfkIntervals, dayMusicIntervals)}</div>
                <div class="timeline-subrows">${timelineSubRowsHtml(daySessions, dayAfkIntervals, dayMusicIntervals)}</div>
            </div>`;
    }
    if (!days || days.length === 0) return `<div class="empty-state" style="padding:28px 16px;">No activity recorded yet.</div>`;
    if (periodType === 'week') return weekHeatmapHtml(days);
    if (periodType === 'year') return yearHeatmapHtml(days);
    return monthHeatmapHtml(days);
}

// Color-only intensity (a flat heatmap square) makes neighboring days look
// almost identical unless you stop and compare shades. Bar height reads
// instantly, so this is a small bar chart: value pinned above, weekday
// label below as the axis, bar height doing the actual comparison.
function weekHeatmapHtml(days) {
    const maxMins = Math.max(...days.map(d => d.focusedMinutes ?? 0), 1);
    const trackHeight = 120;

    const cells = days.map(d => {
        const date = parseDateStr(d.date);
        const mins = d.focusedMinutes ?? 0;
        const intensity = mins / maxMins;
        const barPx = mins > 0 ? Math.max(6, Math.round(intensity * trackHeight)) : 0;
        const bg = heatColor(intensity, 0.55, 0.45);
        return `
            <div class="week-heat-day">
                <div class="week-heat-value">${mins > 0 ? formatTime(mins) : '—'}</div>
                <div class="week-heat-bar-track" style="height:${trackHeight}px"
                     onmousemove="showTooltip(event, '${fmtDateEU(date)}<br>${formatTime(mins)} focused')"
                     onmouseleave="hideTooltip()">
                    <div class="week-heat-bar-fill" style="height:${barPx}px;background:${bg}"></div>
                </div>
                <div class="week-heat-label">${DAY_SHORT[isoDow(date)]}</div>
            </div>`;
    }).join('');

    return `<div class="week-heat-row">${cells}</div>`;
}

function monthHeatmapHtml(days) {
    const maxMins = Math.max(...days.map(d => d.focusedMinutes ?? 0), 1);
    const firstDate = parseDateStr(days[0].date);
    const leadingBlanks = isoDow(firstDate); // days before the 1st lands on its real weekday column

    let cells = '';
    for (let i = 0; i < leadingBlanks; i++) cells += `<div class="month-heat-cell is-empty"></div>`;
    days.forEach(d => {
        const date = parseDateStr(d.date);
        const mins = d.focusedMinutes ?? 0;
        const intensity = mins / maxMins;
        const bg = heatColor(intensity);
        const tip = `${fmtDateEU(date)}<br>${formatTime(mins)} focused`;
        cells += `<div class="month-heat-cell" style="background:${bg}" onmousemove="showTooltip(event, '${tip}')" onmouseleave="hideTooltip()"></div>`;
    });

    const weekdayHeaders = DAY_SHORT.map(d => `<span>${d}</span>`).join('');

    return `
        <div class="month-heat-weekdays">${weekdayHeaders}</div>
        <div class="month-heat-grid">${cells}</div>
        ${heatLegendHtml()}`;
}

// Same cell/legend styling as Month, reusing the .heat-days-grid/.heat-day-cell
// classes (column-major flow already baked into that CSS) rather than the
// weekday-aligned .month-heat-grid — a year has 52+ weekday-aligned columns
// which reads as noise, and this keeps the visual consistent with Overview's
// existing year heatmap instead of inventing a second layout for the same data.
function yearHeatmapHtml(days) {
    const maxMins = Math.max(...days.map(d => d.focusedMinutes ?? 0), 1);
    let cellsHtml = '';
    days.forEach(d => {
        const date = parseDateStr(d.date);
        const mins = d.focusedMinutes ?? 0;
        const intensity = mins / maxMins;
        const bg = heatColor(intensity);
        const tip = `${fmtDateEU(date)}<br>${formatTime(mins)} focused`;
        cellsHtml += `<div class="heat-day-cell" style="background:${bg}" onmousemove="showTooltip(event, '${tip}')" onmouseleave="hideTooltip()"></div>`;
    });
    const cols = Math.ceil(days.length / 7);

    return `
        <div class="heat-days-grid" style="grid-template-columns:repeat(${cols}, minmax(0, 13px));">${cellsHtml}</div>
        ${heatLegendHtml()}`;
}

function windowActivityRowHtml(s) {
    const name = s.appName;
    const cat = s.category ?? 'Other';
    const title = s.windowTitle;
    const start = s.start;
    const end = s.end;
    const dur = s.durationMinutes ?? 0;
    // Window titles are attacker-controllable (any webpage can set its own
    // tab title) — escapeHtml before touching innerHTML, and the click
    // handler reads the name back out of a data-* attribute rather than
    // splicing it into the onclick string, same rule as the Timeline ribbon.
    return `
        <div class="card activity-row" data-open-app="${escapeHtml(name)}" role="button" tabindex="0">
            <div class="activity-icon" style="${avatarStyle(cat)}">${escapeHtml((name || '?').charAt(0).toUpperCase())}</div>
            <div class="activity-name-col">
                <div class="activity-app-name" title="${escapeHtml(name)}">${escapeHtml(displayAppName(name))}</div>
                <div class="activity-title" title="${escapeHtml(title)}">${escapeHtml(title)}</div>
            </div>
            <div class="activity-time-range">${escapeHtml(start)} &ndash; ${escapeHtml(end)}</div>
            <div class="activity-duration">${formatTime(dur)}</div>
        </div>`;
}

// Filters waSessions by waSearch (app name or title, case-insensitive) and
// orders by waSort, returning just the row HTML (or an empty state).
function windowActivityRowsMarkup() {
    const q = waSearch.trim().toLowerCase();
    let list = waSessions;
    if (q) {
        list = list.filter(s => {
            const name = (s.appName ?? '').toLowerCase();
            const title = (s.windowTitle ?? '').toLowerCase();
            return name.includes(q) || title.includes(q);
        });
    }
    if (list.length === 0) {
        return `<div class="empty-state">No matching activity${q ? ` for "${escapeHtml(waSearch.trim())}"` : ''}.</div>`;
    }
    const sorted = [...list].sort((a, b) => {
        const am = a.startMinutes ?? 0;
        const bm = b.startMinutes ?? 0;
        return waSort === 'oldest' ? am - bm : bm - am;
    });
    return sorted.map(windowActivityRowHtml).join('');
}

function renderWindowActivityRows() {
    const listEl = document.getElementById('wa-list');
    if (listEl) listEl.innerHTML = windowActivityRowsMarkup();
}

function filterWindowActivity(inputEl) {
    waSearch = inputEl.value;
    renderWindowActivityRows();
}

function setWindowActivitySort(sort, btnEl) {
    waSort = sort;
    document.querySelectorAll('#wa-sort-toggle button').forEach(b => b.classList.toggle('active', b === btnEl));
    renderWindowActivityRows();
}

// "What was I actually doing" for people who've opted into window-title
// capture (Settings drawer) — a per-day, title-only feed. The Timeline ribbon
// above answers *when*; this answers *what* (e.g. "on 7 April I was watching
// X" needs the real tab/window title, not just "Chrome, 45m"). Reuses the
// Activity tab's row styling. Renders nothing when the day has no titled
// sessions — capture off, or just a quiet day — so it never shows as a
// half-empty card nagging about a setting most people haven't touched.
// isNewDay=false means this is a poll re-render of the period already on
// screen — keep whatever search/sort the user had set instead of resetting
// it out from under them every ~12s.
function windowActivityHtml(daySessions, isNewDay) {
    waSessions = (daySessions || []).filter(s => s.windowTitle);
    if (isNewDay) {
        waSearch = '';
        waSort = 'newest';
    }
    if (waSessions.length === 0) return '';

    return `
        <div class="card" style="margin-top:24px;">
            <div class="wa-head">
                <div class="card-label">Window Activity</div>
                <div class="wa-controls">
                    <input type="text" class="field" id="wa-search" placeholder="Search titles or apps…" value="${escapeHtml(waSearch)}" oninput="filterWindowActivity(this)">
                    <div class="segmented" id="wa-sort-toggle">
                        <button class="${waSort === 'newest' ? 'active' : ''}" onclick="setWindowActivitySort('newest', this)">Newest</button>
                        <button class="${waSort === 'oldest' ? 'active' : ''}" onclick="setWindowActivitySort('oldest', this)">Oldest</button>
                    </div>
                </div>
            </div>
            <div class="lb-list window-activity-list" id="wa-list">${windowActivityRowsMarkup()}</div>
        </div>`;
}

async function openPeriodDetail(startDate, silent) {
    document.getElementById('period-list-view').style.display = 'none';
    document.getElementById('period-detail-view').style.display = 'block';

    const key = `${periodType}:${startDate}`;
    const isNewDay = key !== currentDetailKey;
    currentDetailKey = key;

    if (!silent) document.getElementById('period-detail-body').innerHTML = loadingRowsHtml(4);

    try {
        const d = await apiFetch(`/api/period-detail?type=${periodType}&start=${startDate}`,
                                 { signal: abortableSignal('period-detail') });
        renderPeriodDetail(d, isNewDay);
    } catch (err) {
        // A failed silent poll shouldn't blow away an already-loaded detail
        // view with an error message — just log it and leave things be.
        if (silent) { console.error('Period detail poll refresh failed', err); return; }
        document.getElementById('period-detail-body').innerHTML = errorStateHtml(
            "Couldn't load this period",
            'The details for this period did not come back. FastApp may be busy or restarting.',
            'retryPeriodDetail'
        );
    }
}

// Named entry points for the failure state's retry button — it calls a global
// by name, so these wrap the real loaders with the arguments they need.
function retryPeriodList() { loadPeriodList(); }
function retryPeriodDetail() {
    if (!currentDetailKey) { showPeriodList(); loadPeriodList(); return; }
    openPeriodDetail(currentDetailKey.slice(currentDetailKey.indexOf(':') + 1));
}

function renderPeriodDetail(d, isNewDay) {
    const label = d.label;
    const totalMins = d.totalFocusMinutes ?? 0;
    const rank = d.rank;
    const totalPeriods = d.totalPeriods;
    const prev = d.previous;
    const next = d.next;
    const current = d.current;
    const topApps = d.topApps ?? [];
    const topCategories = d.topCategories ?? [];
    const days = d.days ?? [];
    const daySessions = d.daySessions ?? [];
    const dayAfkIntervals = d.dayAfkIntervals ?? [];
    const dayMusicIntervals = d.dayMusicIntervals ?? [];

    document.getElementById('period-detail-title').textContent = label;
    document.getElementById('period-detail-sub').textContent = `#${rank ?? '–'} of ${totalPeriods ?? '–'} ${periodType}s · ${formatHours((totalMins || 0) / 60)}`;

    const chosenAfkMins = d.totalAfkMinutes;
    const chosenUptimeMins = d.totalUptimeMinutes ?? 0;
    const chosenMusicMins = d.totalMusicMinutes;
    // Whether the period being viewed is the one still in progress right
    // now -- NOT the same thing as "current is null": that also happens
    // when today genuinely has no tracked data yet (just opened the
    // dashboard before the first flush), which would otherwise slap a
    // "so far" and a live dot on an ordinary finished day from months ago.
    // Bucketed the same way the server buckets StartDate for each type, so
    // a Tuesday this week and today (also this week) resolve to the same key.
    function periodStartKey(dateStr, type) {
        const dt = parseDateStr(dateStr);
        if (type === 'day') return dateStr;
        if (type === 'month') return `${dt.getFullYear()}-${pad(dt.getMonth() + 1)}-01`;
        if (type === 'year') return `${dt.getFullYear()}-01-01`;
        const mon = mondayOf(dt);
        return `${mon.getFullYear()}-${pad(mon.getMonth() + 1)}-${pad(mon.getDate())}`;
    }
    const isLive = periodStartKey(d.startDate, periodType) === periodStartKey(getLocalTodayStr(), periodType);

    // Focus/AFK/music as a share of uptime -- same math Overview's own hero
    // dial uses, so "how full is the ring" means the same thing in both
    // places instead of Periods inventing a second way to say it.
    let focusPct = 0, afkPct = 0, musicPct = 0;
    if (chosenUptimeMins > 0) {
        focusPct = Math.min(100, (totalMins / chosenUptimeMins) * 100);
        afkPct = Math.min(100 - focusPct, ((chosenAfkMins || 0) / chosenUptimeMins) * 100);
        musicPct = Math.min(100, ((chosenMusicMins || 0) / chosenUptimeMins) * 100);
    }
    const PD_DIAL_SIZE = 210, PD_DIAL_R = 93;
    const PD_DIAL_CIRC = 2 * Math.PI * PD_DIAL_R;
    const dialOffset = (pct) => PD_DIAL_CIRC * (1 - Math.max(0, Math.min(100, pct)) / 100);
    const dialCircle = (cls, pct) =>
        `<circle class="${cls}" cx="${PD_DIAL_SIZE / 2}" cy="${PD_DIAL_SIZE / 2}" r="${PD_DIAL_R}"
            style="stroke-dasharray:${PD_DIAL_CIRC};stroke-dashoffset:${dialOffset(pct)};opacity:${pct > 0.5 ? 1 : 0}"></circle>`;

    const heroLiveDot = isLive ? '<span class="status-dot" style="margin-right:7px;vertical-align:1px;"></span>' : '';
    const heroTrend = prev ? trendPill(totalMins, prev.totalFocusMinutes ?? 0, 'previous') : '';
    const heroMusicCaption = (chosenMusicMins != null && chosenMusicMins > 0)
        ? ` · <span style="color:var(--violet)">${formatTime(chosenMusicMins)} music</span>` : '';
    const heroCaption = chosenUptimeMins > 0
        ? `<span style="color:var(--rose)">${formatTime(chosenAfkMins || 0)} AFK</span>${heroMusicCaption} · ${formatTime(chosenUptimeMins)} online${isLive ? ' so far' : ''}`
        : '';
    const heroMusicStat = (chosenMusicMins || 0) > 0
        ? `<div class="pd-stat"><div class="card-label" style="color:var(--violet)">Music</div><div class="stat-value" style="color:var(--violet)">${formatHours(chosenMusicMins / 60)}</div></div>`
        : '';

    // The period being viewed as one ring (the same dial language Overview's
    // hero uses for "today"), not one card among three or four equal boxes
    // competing for the same attention. Previous/Next/Current used to each
    // get a full card repeating this card's own layout for a single number
    // nobody was here to read closely -- they're a strip of compact,
    // clickable chips below the ring instead (see pd-nav below).
    const heroHtml = `
        <div class="card pd-hero">
            <div class="pd-dial">
                <svg class="pd-dial-svg" viewBox="0 0 ${PD_DIAL_SIZE} ${PD_DIAL_SIZE}" aria-hidden="true">
                    <circle class="pd-dial-track" cx="${PD_DIAL_SIZE / 2}" cy="${PD_DIAL_SIZE / 2}" r="${PD_DIAL_R}"></circle>
                    ${dialCircle('pd-dial-afk', focusPct + afkPct)}
                    ${dialCircle('pd-dial-focus', focusPct)}
                    ${dialCircle('pd-dial-music', musicPct)}
                </svg>
                <div class="pd-dial-face">
                    <div class="card-label">${heroLiveDot}${escapeHtml(label)}</div>
                    <div class="stat-value mono" style="color:var(--brass)">${formatHours(totalMins / 60)}</div>
                    ${heroTrend ? `<div style="margin-top:4px;">${heroTrend}</div>` : ''}
                </div>
            </div>
            <div class="pd-hero-side">
                <div class="pd-hero-stats">
                    <div class="pd-stat"><div class="card-label">Uptime</div><div class="stat-value">${formatHours(chosenUptimeMins / 60)}</div></div>
                    <div class="pd-stat"><div class="card-label" style="color:var(--rose)">AFK</div><div class="stat-value" style="color:var(--rose)">${formatHours((chosenAfkMins || 0) / 60)}</div></div>
                    ${heroMusicStat}
                </div>
                ${heroCaption ? `<div class="compare-bar-caption" style="margin-top:10px;">${heroCaption}</div>` : ''}
            </div>
        </div>`;

    // Previous always points at the period right before this one. The
    // forward direction points at Next when a completed one exists, and
    // falls back to Current (today's still-live period) when it doesn't --
    // the common case when viewing the most recently completed period, where
    // "next" and "now" are the same bucket. The rarer case -- deep in
    // history, where a completed Next AND a separately live Current both
    // exist -- gets its own third chip rather than silently dropping one.
    const prevChip = prev ? { start: prev.startDate, tag: 'Previous', label: prev.label, mins: prev.totalFocusMinutes ?? 0 } : null;
    const forwardObj = next || current;
    const forwardChip = forwardObj ? {
        start: forwardObj.startDate,
        tag: (!next && current) ? 'Now · so far' : 'Next',
        label: forwardObj.label,
        mins: forwardObj.totalFocusMinutes ?? 0,
        live: !next && !!current
    } : null;
    const nowChip = (next && current && next.startDate !== current.startDate)
        ? { start: current.startDate, tag: 'Now · so far', label: current.label, mins: current.totalFocusMinutes ?? 0, live: true }
        : null;

    function navChipHtml(chip, dir) {
        if (!chip) return '';
        const liveDot = chip.live ? '<span class="status-dot pd-nav-dot"></span>' : '';
        return `
            <div class="pd-nav-chip pd-nav-${dir}" data-open-period="${escapeHtml(chip.start)}" role="button" tabindex="0">
                ${dir === 'left' ? `<span class="pd-nav-arrow">&larr;</span>` : ''}
                <div class="pd-nav-text">
                    <div class="pd-nav-label">${liveDot}${escapeHtml(chip.tag)}</div>
                    <div class="pd-nav-period" title="${escapeHtml(chip.label)}">${escapeHtml(chip.label)}</div>
                </div>
                <div class="pd-nav-value">${formatTime(chip.mins)}</div>
                ${dir === 'right' ? `<span class="pd-nav-arrow">&rarr;</span>` : ''}
            </div>`;
    }

    const navHtml = (prevChip || forwardChip || nowChip) ? `
        <div class="pd-nav">
            ${navChipHtml(prevChip, 'left')}
            ${navChipHtml(forwardChip, 'right')}
            ${navChipHtml(nowChip, 'now')}
        </div>` : '';

    // Proportional bars (.lb-bar, the same component Overview's own
    // leaderboards use) instead of bare rank/name/time rows -- the list reads
    // as a chart at a glance instead of needing every number read to compare
    // two entries.
    const appMax = Math.max(...topApps.map(a => a.focusedMinutes ?? 0), 1);
    const appsHtml = topApps.length === 0 ? `<div class="empty-state">No app data.</div>` : topApps.map((a, i) => {
        const appName = a.appName ?? '';
        return `
        <div class="lb-row app-link" data-open-app="${escapeHtml(appName)}" role="button" tabindex="0">
            <div class="lb-rank">${i + 1}</div>
            <div class="lb-name" title="${escapeHtml(appName)}">${escapeHtml(displayAppName(appName))}</div>
            <div class="lb-bar"><div class="lb-bar-fill" style="width:${((a.focusedMinutes ?? 0) / appMax) * 100}%"></div></div>
            <div class="lb-time">${formatTime(a.focusedMinutes ?? 0)}</div>
        </div>`;
    }).join('');

    const catMax = Math.max(...topCategories.map(c => c.focusedMinutes ?? 0), 1);
    const catsHtml = topCategories.length === 0 ? `<div class="empty-state">No category data.</div>` : topCategories.map((c, i) => {
        const cat = c.category ?? 'Other';
        return `
        <div class="lb-row app-link" data-open-cat="${escapeHtml(cat)}" role="button" tabindex="0">
            <div class="lb-rank">${i + 1}</div>
            <div class="lb-name"><span class="cat-swatch" style="background:${catColor(cat)};display:inline-block;margin-right:8px;"></span>${escapeHtml(cat)}</div>
            <div class="lb-bar"><div class="lb-bar-fill" style="width:${((c.focusedMinutes ?? 0) / catMax) * 100}%;background:${catColor(cat)}"></div></div>
            <div class="lb-time">${formatTime(c.focusedMinutes ?? 0)}</div>
        </div>`;
    }).join('');

    const heatmapHtml = periodHeatmapHtml(days, periodType, daySessions, dayAfkIntervals, dayMusicIntervals);
    const heatmapCardLabel = periodType === 'day' ? 'Timeline' : 'Daily Activity';
    const heatmapCardHtml = heatmapHtml ? `
        <div class="card" style="margin-bottom:24px;">
            <div class="card-label" style="margin-bottom:14px;">${heatmapCardLabel}</div>
            ${heatmapHtml}
        </div>` : '';
    const windowActivityCardHtml = periodType === 'day' ? windowActivityHtml(daySessions, isNewDay) : '';

    document.getElementById('period-detail-body').innerHTML = `
        ${heroHtml}
        ${navHtml}
        ${heatmapCardHtml}
        <div class="two-col">
            <div class="card">
                <div class="card-label" style="margin-bottom:12px;">Top Apps</div>
                <div class="lb-list">${appsHtml}</div>
            </div>
            <div class="card">
                <div class="card-label" style="margin-bottom:12px;">Top Categories</div>
                <div class="lb-list">${catsHtml}</div>
            </div>
        </div>
        ${windowActivityCardHtml}`;

    // Day periods render the same ribbon Overview does, so they get the same
    // inline block labels. Has to run after the innerHTML above: the labels are
    // decided on measured pixel width, which does not exist until layout.
    if (periodType === 'day') {
        labelWideTimelineSegments(document.querySelector('#period-detail-body .timeline-track'));
    }
}

// Refreshes whichever half of the tab is actually visible, silently (no
// "Loading…" flash) and without disturbing state a blind onEnter() would
// reset — the open detail view, or Window Activity's search/sort. If the
// user is mid-keystroke in the search box, skip this tick entirely rather
// than yank their cursor out from under them; the next poll will catch up.
function refreshPeriods() {
    const listVisible = document.getElementById('period-list-view').style.display !== 'none';
    if (listVisible) {
        loadPeriodList(true);
        return;
    }
    const searchInput = document.getElementById('wa-search');
    if (searchInput && document.activeElement === searchInput) return;
    if (currentDetailKey) {
        const startDate = currentDetailKey.slice(currentDetailKey.indexOf(':') + 1);
        openPeriodDetail(startDate, true);
    }
}

Dashboard.tabs.periods = { onEnter: () => { showPeriodList(); loadPeriodList(); }, refresh: refreshPeriods };
