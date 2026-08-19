/* ==========================================================
   APP SHELL
   Persistent top bar + rail nav + tab activation.
   ========================================================== */

const VIEWS = ['overview', 'insights', 'periods', 'activity', 'leaderboard', 'allapps'];

let currentViewId = null;

function switchView(viewId) {
    document.querySelectorAll('.rail-item').forEach(el => el.classList.toggle('active', el.dataset.view === viewId));
    document.querySelectorAll('.view').forEach(el => el.classList.remove('active'));
    const target = document.getElementById('view-' + viewId);
    if (target) target.classList.add('active');
    closeSettings();
    currentViewId = viewId;
    const mod = Dashboard.tabs[viewId];
    if (mod && typeof mod.onEnter === 'function') mod.onEnter();
}

// --- Live polling -------------------------------------------------------
// This is a single local user hitting localhost, and the tracker itself only
// flushes to disk every ~60s, so a push/SSE channel would just be extra
// plumbing for freshness polling already delivers. Each tab exposes an
// optional `refresh` (distinct from `onEnter`) for tabs where blindly
// re-running onEnter would discard state a poll shouldn't touch — Periods'
// open detail view/search/sort, Activity's "Load More" depth. Tabs without
// a `refresh` just don't auto-update; that's a deliberate omission, not
// an oversight.
const POLL_INTERVAL_MS = 12000;

function pollCurrentView() {
    if (document.hidden) return; // no point fetching for a backgrounded tab
    const mod = Dashboard.tabs[currentViewId];
    if (mod && typeof mod.refresh === 'function') mod.refresh();
}

function initNav() {
    document.querySelectorAll('.rail-item[data-view]').forEach(el => {
        el.addEventListener('click', () => switchView(el.dataset.view));
    });
}

// --- Top bar: live clock (24h, European date) -------------------------------
function tickClock() {
    const now = new Date();
    const timeEl = document.getElementById('tb-time');
    const dateEl = document.getElementById('tb-date');
    if (timeEl) timeEl.textContent = `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`;
    if (dateEl) dateEl.textContent = `${DAY_NAMES[isoDow(now)]}, ${fmtDateEU(now)}`;
}

// --- Top bar: online status, focus today, all-time -----------------------
// Read by the "Most Used Today" metric's onclick in dashboard.html — a
// module-level variable instead of splicing the app name into the inline
// onclick string, since it's untrusted (attacker-controllable process/window
// names) and HTML-escaping doesn't survive the browser's attribute-decode
// step for inline handlers.
let tbMostUsedAppName = null;

async function refreshTopBar() {
    const dotEl = document.getElementById('tb-status-dot');
    try {
        const res = await fetch(`/api/overview?date=${getLocalTodayStr()}`);
        if (!res.ok) throw new Error('bad response');
        const data = await res.json();
        if (dotEl) dotEl.classList.remove('offline');

        const focusToday = data.focusToday ?? data.FocusToday ?? 0;
        const usual = data.usualDailyFocus ?? data.UsualDailyFocus ?? 0;
        const allTime = data.focusAllTime ?? data.FocusAllTime ?? 0;

        const focusEl = document.getElementById('tb-focus-value');
        if (focusEl) focusEl.textContent = formatHours(focusToday);

        const allTimeEl = document.getElementById('tb-alltime-value');
        if (allTimeEl) allTimeEl.textContent = formatHours(allTime);

        const topAppsToday = data.topAppsToday ?? data.TopAppsToday ?? [];
        const mostUsedEl = document.getElementById('tb-mostused');
        const mostUsedValueEl = document.getElementById('tb-mostused-value');
        if (mostUsedEl && mostUsedValueEl) {
            if (topAppsToday.length > 0) {
                const top = topAppsToday[0];
                const name = top.appName ?? top.AppName;
                const mins = top.focusedMinutes ?? top.FocusedMinutes ?? 0;
                tbMostUsedAppName = name;
                mostUsedValueEl.textContent = `${name} · ${formatTime(mins)}`;
                mostUsedEl.style.display = '';
            } else {
                tbMostUsedAppName = null;
                mostUsedEl.style.display = 'none';
            }
        }

        // Mini chronometer ring: today's focus vs the usual daily amount (caps at 100%)
        const ringHost = document.getElementById('tb-ring-host');
        if (ringHost) {
            const pct = usual > 0 ? Math.min(100, (focusToday / usual) * 100) : (focusToday > 0 ? 100 : 0);
            ringHost.innerHTML = chronoRing({ pct, size: 30, stroke: 3, mini: true });
        }
    } catch (err) {
        if (dotEl) dotEl.classList.add('offline');
        console.error('Top bar refresh failed', err);
    }
}

// --- Settings overlay -------------------------------------------------------
function openSettings() {
    document.getElementById('settings-overlay').classList.add('open');
    document.getElementById('settings-drawer').classList.add('open');
    if (Dashboard.tabs.settings) Dashboard.tabs.settings.onEnter();
}
function closeSettings() {
    document.getElementById('settings-overlay').classList.remove('open');
    document.getElementById('settings-drawer').classList.remove('open');
}

// --- Boot ---------------------------------------------------------------------
async function boot() {
    initNav();
    document.getElementById('tb-gear').addEventListener('click', openSettings);
    document.getElementById('settings-overlay').addEventListener('click', closeSettings);
    document.getElementById('settings-close').addEventListener('click', closeSettings);
    document.getElementById('dd-overlay').addEventListener('click', closeDrilldown);
    document.getElementById('dd-close').addEventListener('click', closeDrilldown);
    document.getElementById('cat-overlay').addEventListener('click', closeCategoryDetail);
    document.getElementById('cat-close').addEventListener('click', closeCategoryDetail);

    tickClock();
    setInterval(tickClock, 1000);

    refreshTopBar();
    setInterval(refreshTopBar, 30000);

    setInterval(pollCurrentView, POLL_INTERVAL_MS);

    try {
        const res = await fetch('/api/categories');
        allCategories = await res.json();
    } catch (e) {
        allCategories = ['Development', 'Gaming', 'Productivity', 'Browsing', 'Communication', 'Media Production', 'Music', 'Fun', 'Education', 'Utilities', 'Other'];
    }

    switchView('overview');
}

document.addEventListener('DOMContentLoaded', boot);
