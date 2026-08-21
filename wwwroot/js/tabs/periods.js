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
const PERIOD_NOUNS = { day: 'Day', week: 'Week', month: 'Month', year: 'Year' };

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

async function loadPeriodList(silent) {
    const listEl = document.getElementById('period-list');
    if (!silent) listEl.innerHTML = `<div class="empty-state">Loading…</div>`;
    try {
        const periods = await apiFetch(`/api/periods?type=${periodType}`,
                                      { signal: abortableSignal('periods') });

        if (!periods || periods.length === 0) {
            listEl.innerHTML = `<div class="empty-state">No ${periodType}s recorded yet.</div>`;
            return;
        }

        listEl.innerHTML = periods.map(p => {
            const rank = p.rank ?? p.Rank;
            const label = p.label ?? p.Label;
            const start = p.startDate ?? p.StartDate;
            const end = p.endDate ?? p.EndDate;
            const totalMins = p.totalFocusMinutes ?? p.TotalFocusMinutes ?? 0;
            const mostUsed = p.mostUsedApp ?? p.MostUsedApp ?? '—';
            const rangeText = periodType === 'week'
                ? `${fmtDateEU(parseDateStr(start))} → ${fmtDateEU(parseDateStr(end))}`
                : periodType === 'day'
                    ? DAY_NAMES[isoDow(parseDateStr(start))]
                    : label;

            return `
                <div class="card period-card" data-open-period="${escapeHtml(start)}" role="button" tabindex="0">
                    <div class="period-row">
                        <div class="period-rank-badge ${rank === 1 ? 'rank-1' : ''}">#${rank ?? '–'}</div>
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
                                <div class="period-stat-value app-link" title="${escapeHtml(mostUsed)}" data-open-app="${escapeHtml(mostUsed)}" role="button" tabindex="0">${escapeHtml(mostUsed)}</div>
                            </div>
                            <div class="period-stat">
                                <div class="period-stat-label">Ranking</div>
                                <div class="period-stat-value">#${rank ?? '–'} of ${p.totalPeriods ?? p.TotalPeriods ?? '–'}</div>
                            </div>
                        </div>
                    </div>
                </div>`;
        }).join('');
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
function periodHeatmapHtml(days, periodType, daySessions) {
    if (periodType === 'day') {
        return `
            <div class="timeline-wrap">
                <div class="timeline-ticks"><span>00:00</span><span>06:00</span><span>12:00</span><span>18:00</span><span>24:00</span></div>
                <div class="timeline-track">${timelineSegmentsHtml(daySessions)}</div>
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
    const maxMins = Math.max(...days.map(d => d.focusedMinutes ?? d.FocusedMinutes ?? 0), 1);
    const trackHeight = 120;

    const cells = days.map(d => {
        const date = parseDateStr(d.date ?? d.Date);
        const mins = d.focusedMinutes ?? d.FocusedMinutes ?? 0;
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
    const maxMins = Math.max(...days.map(d => d.focusedMinutes ?? d.FocusedMinutes ?? 0), 1);
    const firstDate = parseDateStr(days[0].date ?? days[0].Date);
    const leadingBlanks = isoDow(firstDate); // days before the 1st lands on its real weekday column

    let cells = '';
    for (let i = 0; i < leadingBlanks; i++) cells += `<div class="month-heat-cell is-empty"></div>`;
    days.forEach(d => {
        const date = parseDateStr(d.date ?? d.Date);
        const mins = d.focusedMinutes ?? d.FocusedMinutes ?? 0;
        const intensity = mins / maxMins;
        const bg = heatColor(intensity);
        const tip = `${fmtDateEU(date)}<br>${formatTime(mins)} focused`;
        cells += `<div class="month-heat-cell" style="background:${bg}" onmousemove="showTooltip(event, '${tip}')" onmouseleave="hideTooltip()"></div>`;
    });

    const weekdayHeaders = DAY_SHORT.map(d => `<span>${d}</span>`).join('');

    return `
        <div class="month-heat-weekdays">${weekdayHeaders}</div>
        <div class="month-heat-grid">${cells}</div>
        <div class="heat-legend">
            <span>Less</span>
            <span class="heat-legend-swatch" style="background:var(--bg-raised)"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.3)}"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.6)}"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.9)}"></span>
            <span>More</span>
        </div>`;
}

// Same cell/legend styling as Month, reusing the .heat-days-grid/.heat-day-cell
// classes (column-major flow already baked into that CSS) rather than the
// weekday-aligned .month-heat-grid — a year has 52+ weekday-aligned columns
// which reads as noise, and this keeps the visual consistent with Overview's
// existing year heatmap instead of inventing a second layout for the same data.
function yearHeatmapHtml(days) {
    const maxMins = Math.max(...days.map(d => d.focusedMinutes ?? d.FocusedMinutes ?? 0), 1);
    let cellsHtml = '';
    days.forEach(d => {
        const date = parseDateStr(d.date ?? d.Date);
        const mins = d.focusedMinutes ?? d.FocusedMinutes ?? 0;
        const intensity = mins / maxMins;
        const bg = heatColor(intensity);
        const tip = `${fmtDateEU(date)}<br>${formatTime(mins)} focused`;
        cellsHtml += `<div class="heat-day-cell" style="background:${bg}" onmousemove="showTooltip(event, '${tip}')" onmouseleave="hideTooltip()"></div>`;
    });
    const cols = Math.ceil(days.length / 7);

    return `
        <div class="heat-days-grid" style="grid-template-columns:repeat(${cols}, minmax(0, 13px));">${cellsHtml}</div>
        <div class="heat-legend">
            <span>Less</span>
            <span class="heat-legend-swatch" style="background:var(--bg-raised)"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.3)}"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.6)}"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.9)}"></span>
            <span>More</span>
        </div>`;
}

function windowActivityRowHtml(s) {
    const name = s.appName ?? s.AppName;
    const cat = s.category ?? s.Category ?? 'Other';
    const title = s.windowTitle ?? s.WindowTitle;
    const start = s.start ?? s.Start;
    const end = s.end ?? s.End;
    const dur = s.durationMinutes ?? s.DurationMinutes ?? 0;
    // Window titles are attacker-controllable (any webpage can set its own
    // tab title) — escapeHtml before touching innerHTML, and the click
    // handler reads the name back out of a data-* attribute rather than
    // splicing it into the onclick string, same rule as the Timeline ribbon.
    return `
        <div class="card activity-row" data-open-app="${escapeHtml(name)}" role="button" tabindex="0">
            <div class="activity-icon" style="color:${catColor(cat)}">${escapeHtml((name || '?').charAt(0).toUpperCase())}</div>
            <div class="activity-name-col">
                <div class="activity-app-name">${escapeHtml(name)}</div>
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
            const name = (s.appName ?? s.AppName ?? '').toLowerCase();
            const title = (s.windowTitle ?? s.WindowTitle ?? '').toLowerCase();
            return name.includes(q) || title.includes(q);
        });
    }
    if (list.length === 0) {
        return `<div class="empty-state">No matching activity${q ? ` for "${escapeHtml(waSearch.trim())}"` : ''}.</div>`;
    }
    const sorted = [...list].sort((a, b) => {
        const am = a.startMinutes ?? a.StartMinutes ?? 0;
        const bm = b.startMinutes ?? b.StartMinutes ?? 0;
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
    waSessions = (daySessions || []).filter(s => s.windowTitle ?? s.WindowTitle);
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

    if (!silent) document.getElementById('period-detail-body').innerHTML = `<div class="empty-state">Loading…</div>`;

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
    const label = d.label ?? d.Label;
    const totalMins = d.totalFocusMinutes ?? d.TotalFocusMinutes ?? 0;
    const rank = d.rank ?? d.Rank;
    const totalPeriods = d.totalPeriods ?? d.TotalPeriods;
    const prev = d.previous ?? d.Previous;
    const next = d.next ?? d.Next;
    const current = d.current ?? d.Current;
    const topApps = d.topApps ?? d.TopApps ?? [];
    const topCategories = d.topCategories ?? d.TopCategories ?? [];
    const days = d.days ?? d.Days ?? [];
    const daySessions = d.daySessions ?? d.DaySessions ?? [];

    document.getElementById('period-detail-title').textContent = label;
    document.getElementById('period-detail-sub').textContent = `#${rank ?? '–'} of ${totalPeriods ?? '–'} ${periodType}s · ${formatHours((totalMins || 0) / 60)}`;

    const chosenAfkMins = d.totalAfkMinutes ?? d.TotalAfkMinutes;
    const chosenUptimeMins = d.totalUptimeMinutes ?? d.TotalUptimeMinutes;
    const periodNoun = PERIOD_NOUNS[periodType] || 'Period';
    const blocks = [
        prev ? { tag: 'Previous', obj: prev } : null,
        { tag: 'This ' + periodNoun, obj: { totalFocusMinutes: totalMins, totalAfkMinutes: chosenAfkMins, totalUptimeMinutes: chosenUptimeMins, label }, current: true },
        next ? { tag: 'Next', obj: next } : null,
        current ? { tag: 'Current ' + periodNoun, obj: current } : null
    ].filter(Boolean);

    // Focus is the headline number. Uptime/AFK used to be two more raw-number
    // lines stacked underneath — three numbers the reader had to do math on to
    // relate to each other. A share-of-uptime bar shows that relationship
    // directly (how much of the bar is filled = how much of your time online
    // was focused), with one caption line underneath for the exact figures.
    const compareHtml = blocks.map(b => {
        const mins = b.obj.totalFocusMinutes ?? b.obj.TotalFocusMinutes ?? 0;
        const afkMins = b.obj.totalAfkMinutes ?? b.obj.TotalAfkMinutes;
        const uptimeMins = b.obj.totalUptimeMinutes ?? b.obj.TotalUptimeMinutes;
        const lbl = b.obj.label ?? b.obj.Label ?? b.tag;

        let barHtml = '';
        if (uptimeMins > 0) {
            const focusPct = Math.min(100, (mins / uptimeMins) * 100);
            const afkPct = Math.min(100 - focusPct, ((afkMins || 0) / uptimeMins) * 100);
            barHtml = `
                <div class="compare-bar">
                    <div class="compare-bar-seg" style="width:${focusPct}%;background:var(--brass)"></div>
                    <div class="compare-bar-seg" style="width:${afkPct}%;background:var(--rose)"></div>
                </div>
                <div class="compare-bar-caption"><span style="color:var(--rose)">${formatTime(afkMins || 0)} AFK</span> · ${formatTime(uptimeMins)} online</div>`;
        }

        // Color the headline number brass to match the bar's "focus" segment,
        // and fold "Focused" into the existing sub-label line rather than
        // adding a whole new line just to say what the number is.
        return `
            <div class="card compare-block ${b.current ? 'is-current' : ''}">
                <div class="card-label">${b.tag}</div>
                <div class="stat-value mono" style="margin-top:6px;color:var(--brass)">${formatHours((mins || 0) / 60)}</div>
                <div style="font-size:11px;color:var(--text-faint);margin-top:4px;">Focused · ${lbl}</div>
                ${barHtml}
            </div>`;
    }).join('');

    const appsHtml = topApps.length === 0 ? `<div class="empty-state">No app data.</div>` : topApps.map((a, i) => {
        const appName = a.appName ?? a.AppName ?? '';
        return `
        <div class="lb-row app-link" data-open-app="${escapeHtml(appName)}" role="button" tabindex="0">
            <div class="lb-rank">${i + 1}</div>
            <div class="lb-name">${escapeHtml(appName)}</div>
            <div class="lb-time">${formatTime(a.focusedMinutes ?? a.FocusedMinutes ?? 0)}</div>
        </div>`;
    }).join('');

    const catsHtml = topCategories.length === 0 ? `<div class="empty-state">No category data.</div>` : topCategories.map((c, i) => {
        const cat = c.category ?? c.Category ?? 'Other';
        return `
        <div class="lb-row app-link" data-open-cat="${escapeHtml(cat)}" role="button" tabindex="0">
            <div class="lb-rank">${i + 1}</div>
            <div class="lb-name"><span class="cat-swatch" style="background:${catColor(cat)};display:inline-block;margin-right:8px;"></span>${escapeHtml(cat)}</div>
            <div class="lb-time">${formatTime(c.focusedMinutes ?? c.FocusedMinutes ?? 0)}</div>
        </div>`;
    }).join('');

    const heatmapHtml = periodHeatmapHtml(days, periodType, daySessions);
    const heatmapCardLabel = periodType === 'day' ? 'Timeline' : 'Daily Activity';
    const heatmapCardHtml = heatmapHtml ? `
        <div class="card" style="margin-bottom:24px;">
            <div class="card-label" style="margin-bottom:14px;">${heatmapCardLabel}</div>
            ${heatmapHtml}
        </div>` : '';
    const windowActivityCardHtml = periodType === 'day' ? windowActivityHtml(daySessions, isNewDay) : '';

    document.getElementById('period-detail-body').innerHTML = `
        <div class="compare-row">${compareHtml}</div>
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
