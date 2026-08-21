/* ==========================================================
   TAB: ACTIVITY
   Chronological app feed, newest first, paged via
   /api/recent-sessions and grouped under day dividers.

   The backend records a session per focus change, which means
   alt-tabbing between two windows writes a row per switch: of
   the 200 most recent sessions on a real install, 80% were
   under a minute and 89% under two, with start and end times
   that read identically ("11:49 – 11:49"). Finding what you
   were actually doing meant scrolling past mostly noise.

   So the feed shows RUNS rather than raw sessions: consecutive
   sessions of the same app collapse into one row spanning the
   whole stretch, carrying a switch count. Distinct window
   titles inside a run are kept — collapsing the noise must not
   destroy the "what was I doing" detail, which is the reason
   this tab exists.
   ========================================================== */

const ACTIVITY_PAGE_SIZE = 50;
let activityOffset = 0;
let activityTotalCount = 0;

// Raw sessions accumulated across pages. Kept because runs have to be rebuilt
// from the whole set: a run can straddle a page boundary, and merging each page
// in isolation would emit the same run twice.
let activitySessions = [];
let activitySearch = '';
let activityMinMinutes = 1;   // 0 = show every switch

// Collapses consecutive same-app sessions. Input is newest-first, so a run's
// `end` comes from its first member and `start` from its last.
function mergeActivityRuns(sessions) {
    const runs = [];
    sessions.forEach(s => {
        const prev = runs[runs.length - 1];
        const start = new Date(s.startTime);
        const end = new Date(s.endTime);
        const sameDay = prev && prev.start.toDateString() === start.toDateString();

        if (prev && prev.appName === s.appName && sameDay) {
            prev.start = start;                       // extend backwards in time
            prev.durationMinutes += (s.durationMinutes ?? 0);
            prev.switches++;
            if (s.windowTitle && !prev.titles.includes(s.windowTitle)) prev.titles.push(s.windowTitle);
        } else {
            runs.push({
                appName: s.appName,
                category: s.category ?? 'Other',
                start, end,
                durationMinutes: s.durationMinutes ?? 0,
                switches: 1,
                titles: s.windowTitle ? [s.windowTitle] : []
            });
        }
    });
    return runs;
}

function visibleActivityRuns() {
    const q = activitySearch.trim().toLowerCase();
    return mergeActivityRuns(activitySessions)
        // Filter on the merged total, not the individual sessions: six 20-second
        // visits to the same window is a two-minute stretch of doing something,
        // and should survive a "hide anything under a minute" filter.
        .filter(r => r.durationMinutes >= activityMinMinutes)
        .filter(r => !q
            || r.appName.toLowerCase().includes(q)
            || r.titles.some(t => t.toLowerCase().includes(q)));
}

async function loadActivity() {
    activityOffset = 0;
    activitySessions = [];
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

        if (isFirstPage) activitySessions = [];
        activitySessions = activitySessions.concat(sessions);
        activityOffset += sessions.length;

        renderActivity();

        const hasMore = activityOffset < activityTotalCount;
        loadMoreBtn.style.display = hasMore ? 'inline-flex' : 'none';
        loadMoreBtn.disabled = false;
        loadMoreBtn.textContent = 'Load More';
    } catch (err) {
        if (!isAbort(err)) console.error('Activity load failed', err);
        if (isFirstPage) {
            listEl.innerHTML = errorStateHtml(
                "Couldn't load activity",
                'FastApp is running but the recent-activity feed did not come back.',
                'loadActivity');
        }
        loadMoreBtn.disabled = false;
        loadMoreBtn.textContent = 'Load More';
    }
}

function renderActivity() {
    const listEl = document.getElementById('activity-list');
    const runs = visibleActivityRuns();

    const summary = document.getElementById('activity-summary');
    if (summary) {
        const hidden = mergeActivityRuns(activitySessions).length - runs.length;
        summary.textContent = activitySessions.length === 0 ? ''
            : `${runs.length} from ${activitySessions.length} switches`
              + (hidden > 0 ? ` · ${hidden} short one${hidden === 1 ? '' : 's'} hidden` : '');
    }

    if (runs.length === 0) {
        listEl.innerHTML = `<div class="empty-state">${
            activitySearch ? `No activity matching “${escapeHtml(activitySearch.trim())}”.` : 'No activity recorded yet.'
        }</div>`;
        return;
    }

    let lastDayKey = null;
    listEl.innerHTML = runs.map(r => {
        let divider = '';
        const dayKey = `${r.start.getFullYear()}-${r.start.getMonth()}-${r.start.getDate()}`;
        if (dayKey !== lastDayKey) {
            divider = `<div class="activity-day-divider">${activityDayLabel(r.start)}</div>`;
            lastDayKey = dayKey;
        }
        return divider + activityRunHtml(r);
    }).join('');
}

function activityRunHtml(r) {
    const timeRange = `${pad(r.start.getHours())}:${pad(r.start.getMinutes())} &ndash; ${pad(r.end.getHours())}:${pad(r.end.getMinutes())}`;

    // Window titles are attacker-controllable (any page sets its own), and app
    // and category names are OS- and user-supplied — everything is escaped. The
    // category chip carries its own data-open-cat, which the delegated handler
    // resolves ahead of the row's data-open-app because it sits nearer the click.
    let titleLine = '';
    if (r.titles.length) {
        const extra = r.titles.length > 1 ? ` <span class="activity-title-more">+${r.titles.length - 1} more</span>` : '';
        titleLine = `<div class="activity-title" title="${escapeHtml(r.titles.join(' · '))}">${escapeHtml(r.titles[0])}${extra}</div>`;
    }
    const switchChip = r.switches > 1
        ? `<span class="activity-switches" title="${r.switches} focus changes merged into this stretch">${r.switches}&times;</span>` : '';

    // Category sits inline with the name rather than on its own line: it is
    // already encoded in the avatar's tint, it is short, and giving it a third
    // line cost more vertical space than the whole feed could afford.
    return `
        <div class="card activity-row" data-open-app="${escapeHtml(r.appName)}" role="button" tabindex="0">
            <div class="activity-icon" style="${avatarStyle(r.category)}">${escapeHtml((r.appName || '?').charAt(0).toUpperCase())}</div>
            <div class="activity-name-col">
                <div class="activity-name-line">
                    <span class="activity-app-name" title="${escapeHtml(r.appName)}">${escapeHtml(displayAppName(r.appName))}</span>
                    <span class="activity-cat cat-link" data-open-cat="${escapeHtml(r.category)}" role="button" tabindex="0">${escapeHtml(r.category)}</span>
                    ${switchChip}
                </div>
                ${titleLine}
            </div>
            <div class="activity-time-range">${timeRange}</div>
            <div class="activity-duration">${formatTime(r.durationMinutes)}</div>
        </div>`;
}

function filterActivity(inputEl) {
    activitySearch = inputEl.value;
    renderActivity();
}

function setActivityMinDuration(mins, btnEl) {
    activityMinMinutes = mins;
    document.querySelectorAll('#activity-min-toggle button').forEach(b => b.classList.toggle('active', b === btnEl));
    renderActivity();
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
    // loadActivity resets back to page 1 — fine on a fresh tab entry, but doing
    // that on every poll would erase anyone who has clicked "Load More" deeper
    // into their history. Only auto-refresh while still on page 1, and never
    // while the user is mid-keystroke in the search box.
    refresh: () => {
        const search = document.getElementById('activity-search');
        if (search && document.activeElement === search) return;
        if (activityOffset <= ACTIVITY_PAGE_SIZE) loadActivity();
    }
};
