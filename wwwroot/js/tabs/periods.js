/* ==========================================================
   TAB: WEEKS & MONTHS
   Toggle Weeks/Months (default Weeks). Each entry: range, total
   focus, most-used app, rank vs all other periods of that type.
   Click -> detail subpage comparing to prev/next/current period.
   Requires the /api/periods + /api/period-detail endpoints —
   see periods-endpoint.cs for the C# to add.
   ========================================================== */

let periodType = 'week'; // 'week' | 'month'

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

async function loadPeriodList() {
    const listEl = document.getElementById('period-list');
    listEl.innerHTML = `<div class="empty-state">Loading…</div>`;
    try {
        const res = await fetch(`/api/periods?type=${periodType}`);
        if (!res.ok) throw new Error('endpoint missing');
        const periods = await res.json();

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
                : label;

            return `
                <div class="card period-card" onclick="openPeriodDetail('${start}')">
                    <div class="period-row">
                        <div class="period-rank-badge ${rank === 1 ? 'rank-1' : ''}">#${rank ?? '–'}</div>
                        <div class="period-main">
                            <div class="period-label" title="${label}">${label}</div>
                            <div class="period-range">${rangeText}</div>
                        </div>
                        <div class="period-stats">
                            <div class="period-stat">
                                <div class="period-stat-label">Total Focus</div>
                                <div class="period-stat-value" style="color:var(--brass)">${formatTime(totalMins)}</div>
                            </div>
                            <div class="period-stat">
                                <div class="period-stat-label">Most Used</div>
                                <div class="period-stat-value app-link" title="${mostUsed}" onclick="event.stopPropagation(); openDrilldown('${mostUsed.replace(/'/g, "&#39;")}')">${mostUsed}</div>
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
        listEl.innerHTML = `<div class="empty-state">
            The Weeks &amp; Months backend endpoint isn't set up yet.<br>
            <span style="font-family:var(--font-mono);font-size:11px;">Add the code from periods-endpoint.cs to DashboardServerService.cs</span>
        </div>`;
    }
}

// Daily-activity visual for the period detail page.
// Week: only 7 days, so a bare heatmap square is too thin to read — show a
// labeled day-by-day strip instead (day name, colored block, time under it).
// Month: a real Mon-Sun calendar grid (like GitHub's contribution graph),
// not a flat sequential strip, so the shape of the month is recognizable.
function periodHeatmapHtml(days, periodType) {
    if (!days || days.length === 0) return `<div class="empty-state" style="padding:28px 16px;">No activity recorded yet.</div>`;
    return periodType === 'week' ? weekHeatmapHtml(days) : monthHeatmapHtml(days);
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
        const bg = mins > 0 ? `rgba(232,163,61,${0.55 + intensity * 0.45})` : 'var(--bg-raised)';
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
        const bg = intensity > 0 ? `rgba(232,163,61,${0.15 + intensity * 0.75})` : 'var(--bg-raised)';
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
            <span class="heat-legend-swatch" style="background:rgba(232,163,61,0.3)"></span>
            <span class="heat-legend-swatch" style="background:rgba(232,163,61,0.6)"></span>
            <span class="heat-legend-swatch" style="background:rgba(232,163,61,0.9)"></span>
            <span>More</span>
        </div>`;
}

async function openPeriodDetail(startDate) {
    document.getElementById('period-list-view').style.display = 'none';
    document.getElementById('period-detail-view').style.display = 'block';
    document.getElementById('period-detail-body').innerHTML = `<div class="empty-state">Loading…</div>`;

    try {
        const res = await fetch(`/api/period-detail?type=${periodType}&start=${startDate}`);
        if (!res.ok) throw new Error('endpoint missing');
        const d = await res.json();
        renderPeriodDetail(d);
    } catch (err) {
        document.getElementById('period-detail-body').innerHTML = `<div class="empty-state">Couldn't load this period's detail.</div>`;
    }
}

function renderPeriodDetail(d) {
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

    document.getElementById('period-detail-title').textContent = label;
    document.getElementById('period-detail-sub').textContent = `#${rank ?? '–'} of ${totalPeriods ?? '–'} ${periodType}s · ${formatHours((totalMins || 0) / 60)}`;

    const chosenAfkMins = d.totalAfkMinutes ?? d.TotalAfkMinutes;
    const chosenUptimeMins = d.totalUptimeMinutes ?? d.TotalUptimeMinutes;
    const blocks = [
        prev ? { tag: 'Previous', obj: prev } : null,
        { tag: 'This ' + (periodType === 'week' ? 'Week' : 'Month'), obj: { totalFocusMinutes: totalMins, totalAfkMinutes: chosenAfkMins, totalUptimeMinutes: chosenUptimeMins, label }, current: true },
        next ? { tag: 'Next', obj: next } : null,
        current ? { tag: 'Current ' + (periodType === 'week' ? 'Week' : 'Month'), obj: current } : null
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

    const appsHtml = topApps.length === 0 ? `<div class="empty-state">No app data.</div>` : topApps.map((a, i) => `
        <div class="lb-row app-link" onclick="openDrilldown('${a.appName ?? a.AppName}')">
            <div class="lb-rank">${i + 1}</div>
            <div class="lb-name">${a.appName ?? a.AppName}</div>
            <div class="lb-time">${formatTime(a.focusedMinutes ?? a.FocusedMinutes ?? 0)}</div>
        </div>`).join('');

    const catsHtml = topCategories.length === 0 ? `<div class="empty-state">No category data.</div>` : topCategories.map((c, i) => `
        <div class="lb-row app-link" onclick="openCategoryDetail('${(c.category ?? c.Category).replace(/'/g, "&#39;")}')">
            <div class="lb-rank">${i + 1}</div>
            <div class="lb-name"><span class="cat-swatch" style="background:${catColor(c.category ?? c.Category)};display:inline-block;margin-right:8px;"></span>${c.category ?? c.Category}</div>
            <div class="lb-time">${formatTime(c.focusedMinutes ?? c.FocusedMinutes ?? 0)}</div>
        </div>`).join('');

    const heatmapHtml = periodHeatmapHtml(days, periodType);
    const heatmapCardHtml = heatmapHtml ? `
        <div class="card" style="margin-bottom:24px;">
            <div class="card-label" style="margin-bottom:14px;">Daily Activity</div>
            ${heatmapHtml}
        </div>` : '';

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
        </div>`;
}

Dashboard.tabs.periods = { onEnter: () => { showPeriodList(); loadPeriodList(); } };
