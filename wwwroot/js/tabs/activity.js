/* ==========================================================
   TAB: ACTIVITY
   Raw chronological app-switch feed (ActivityWatch-style),
   paged newest-first via /api/recent-sessions. Grouped under
   day dividers (Today / Yesterday / Weekday, Date).
   ========================================================== */

const ACTIVITY_PAGE_SIZE = 50;
let activityOffset = 0;
let activityTotalCount = 0;
let activityLastDayKey = null;

async function loadActivity() {
    activityOffset = 0;
    activityLastDayKey = null;
    await fetchActivityPage(true);
}

async function loadMoreActivity() {
    await fetchActivityPage(false);
}

async function fetchActivityPage(isFirstPage) {
    const listEl = document.getElementById('activity-list');
    const loadMoreBtn = document.getElementById('activity-load-more');
    if (isFirstPage && isEmptyContainer(listEl)) listEl.innerHTML = loadingRowsHtml(6);
    loadMoreBtn.disabled = true;
    loadMoreBtn.textContent = 'Loading…';

    try {
        const data = await apiFetch(`/api/recent-sessions?limit=${ACTIVITY_PAGE_SIZE}&offset=${activityOffset}`);
        const sessions = data.sessions ?? [];
        activityTotalCount = data.totalCount ?? 0;

        if (isFirstPage) listEl.innerHTML = '';

        if (isFirstPage && sessions.length === 0) {
            listEl.innerHTML = `<div class="empty-state">No activity recorded yet.</div>`;
        } else {
            listEl.insertAdjacentHTML('beforeend', renderActivityRows(sessions));
        }

        activityOffset += sessions.length;
        const hasMore = activityOffset < activityTotalCount;
        loadMoreBtn.style.display = hasMore ? 'inline-flex' : 'none';
        loadMoreBtn.disabled = false;
        loadMoreBtn.textContent = 'Load More';
    } catch (err) {
        console.error('Activity load failed', err);
        if (isFirstPage) listEl.innerHTML = `<div class="empty-state">Couldn't load activity.</div>`;
        loadMoreBtn.disabled = false;
        loadMoreBtn.textContent = 'Load More';
    }
}

function renderActivityRows(sessions) {
    let html = '';
    sessions.forEach(s => {
        const appName = s.appName;
        const cat = s.category ?? 'Other';
        const start = new Date(s.startTime);
        const end = new Date(s.endTime);
        const dur = s.durationMinutes ?? 0;
        const windowTitle = s.windowTitle;

        const dayKey = `${start.getFullYear()}-${start.getMonth()}-${start.getDate()}`;
        if (dayKey !== activityLastDayKey) {
            html += `<div class="activity-day-divider">${activityDayLabel(start)}</div>`;
            activityLastDayKey = dayKey;
        }

        const timeRange = `${pad(start.getHours())}:${pad(start.getMinutes())} &ndash; ${pad(end.getHours())}:${pad(end.getMinutes())}`;
        // Every value below is escaped: window titles are attacker-controllable
        // (any page sets its own title), and app/category names are OS- and
        // user-supplied. The category chip carries its own data-open-cat, which
        // the delegated handler resolves ahead of the row's data-open-app
        // because it sits closer to the click target.
        const titleLine = windowTitle
            ? `<div class="activity-title" title="${escapeHtml(windowTitle)}">${escapeHtml(windowTitle)}</div>`
            : '';
        html += `
            <div class="card activity-row" data-open-app="${escapeHtml(appName)}" role="button" tabindex="0">
                <div class="activity-icon" style="color:${catColor(cat)}">${escapeHtml((appName || '?').charAt(0).toUpperCase())}</div>
                <div class="activity-name-col">
                    <div class="activity-app-name">${escapeHtml(appName)}</div>
                    <div class="activity-cat cat-link" data-open-cat="${escapeHtml(cat)}" role="button" tabindex="0">${escapeHtml(cat)}</div>
                    ${titleLine}
                </div>
                <div class="activity-time-range">${timeRange}</div>
                <div class="activity-duration">${formatTime(dur)}</div>
            </div>`;
    });
    return html;
}

function activityDayLabel(date) {
    const today = parseDateStr(getLocalTodayStr());
    const dOnly = new Date(date.getFullYear(), date.getMonth(), date.getDate());
    const diffDays = Math.round((today - dOnly) / 86400000);
    if (diffDays === 0) return 'Today';
    if (diffDays === 1) return 'Yesterday';
    return `${DAY_NAMES[isoDow(dOnly)]}, ${fmtDateLong(dOnly)}`;
}

Dashboard.tabs.activity = {
    onEnter: loadActivity,
    // loadActivity resets back to page 1 — fine on a fresh tab entry, but
    // doing that on every poll would erase anyone who's clicked "Load More"
    // deeper into their history. Only auto-refresh while still on page 1.
    refresh: () => { if (activityOffset <= ACTIVITY_PAGE_SIZE) loadActivity(); }
};
