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
                <div class="card period-row" onclick="openPeriodDetail('${start}')">
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
                </div>`;
        }).join('');
    } catch (err) {
        listEl.innerHTML = `<div class="empty-state">
            The Weeks &amp; Months backend endpoint isn't set up yet.<br>
            <span style="font-family:var(--font-mono);font-size:11px;">Add the code from periods-endpoint.cs to DashboardServerService.cs</span>
        </div>`;
    }
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

    document.getElementById('period-detail-title').textContent = label;
    document.getElementById('period-detail-sub').textContent = `#${rank ?? '–'} of ${totalPeriods ?? '–'} ${periodType}s · ${formatHours((totalMins || 0) / 60)}`;

    const chosenAfkMins = d.totalAfkMinutes ?? d.TotalAfkMinutes;
    const blocks = [
        prev ? { tag: 'Previous', obj: prev } : null,
        { tag: 'This ' + (periodType === 'week' ? 'Week' : 'Month'), obj: { totalFocusMinutes: totalMins, totalAfkMinutes: chosenAfkMins, label }, current: true },
        next ? { tag: 'Next', obj: next } : null,
        current ? { tag: 'Current ' + (periodType === 'week' ? 'Week' : 'Month'), obj: current } : null
    ].filter(Boolean);

    const compareHtml = blocks.map(b => {
        const mins = b.obj.totalFocusMinutes ?? b.obj.TotalFocusMinutes ?? 0;
        const afkMins = b.obj.totalAfkMinutes ?? b.obj.TotalAfkMinutes;
        const lbl = b.obj.label ?? b.obj.Label ?? b.tag;
        const afkLine = afkMins !== undefined
            ? `<div style="font-size:11px;color:var(--rose);margin-top:6px;">AFK ${formatTime(afkMins)}</div>`
            : '';
        return `
            <div class="card compare-block ${b.current ? 'is-current' : ''}">
                <div class="card-label">${b.tag}</div>
                <div class="stat-value mono" style="margin-top:6px;">${formatHours((mins || 0) / 60)}</div>
                <div style="font-size:11px;color:var(--text-faint);margin-top:4px;">${lbl}</div>
                ${afkLine}
            </div>`;
    }).join('');

    const appsHtml = topApps.length === 0 ? `<div class="empty-state">No app data.</div>` : topApps.map((a, i) => `
        <div class="lb-row app-link" onclick="openDrilldown('${a.appName ?? a.AppName}')">
            <div class="lb-rank">${i + 1}</div>
            <div class="lb-name">${a.appName ?? a.AppName}</div>
            <div class="lb-time">${formatTime(a.focusedMinutes ?? a.FocusedMinutes ?? 0)}</div>
        </div>`).join('');

    const catsHtml = topCategories.length === 0 ? `<div class="empty-state">No category data.</div>` : topCategories.map((c, i) => `
        <div class="lb-row">
            <div class="lb-rank">${i + 1}</div>
            <div class="lb-name"><span class="cat-swatch" style="background:${catColor(c.category ?? c.Category)};display:inline-block;margin-right:8px;"></span>${c.category ?? c.Category}</div>
            <div class="lb-time">${formatTime(c.focusedMinutes ?? c.FocusedMinutes ?? 0)}</div>
        </div>`).join('');

    document.getElementById('period-detail-body').innerHTML = `
        <div class="compare-row">${compareHtml}</div>
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
