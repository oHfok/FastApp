/* ==========================================================
   APP SHELL
   Persistent top bar + rail nav + tab activation.
   ========================================================== */

const VIEWS = ['overview', 'insights', 'periods', 'activity', 'leaderboard', 'allapps'];

let currentViewId = null;

// --- URL state --------------------------------------------------------------
// The dashboard opens in a browser tab, so people use browser habits on it —
// and none of them used to work. Tab, scope and date lived only in memory and
// the URL never changed, so refreshing dumped you back on Overview/Day from
// wherever you were, Back left the dashboard entirely instead of closing what
// you had just opened, and there was no way to bookmark a view or keep two
// tabs on different periods.
//
// Only the shell's own state goes in the URL (which tab, which scope, which
// date). Drawers are deliberately left out: they are transient detail views
// over a tab, and putting them in history would make Back walk through every
// app you happened to glance at.
function readUrlState() {
    const params = new URLSearchParams(location.search);
    const view = params.get('view');
    return {
        view: VIEWS.includes(view) ? view : null,
        scope: ['day', 'week', 'month', 'year'].includes(params.get('scope')) ? params.get('scope') : null,
        // Only accept a real yyyy-mm-dd, so a hand-edited URL can't push a
        // malformed string into every date-keyed request the tab makes.
        date: /^\d{4}-\d{2}-\d{2}$/.test(params.get('date') || '') ? params.get('date') : null
    };
}

function writeUrlState(replace) {
    const params = new URLSearchParams();
    if (currentViewId) params.set('view', currentViewId);
    if (currentViewId === 'overview') {
        params.set('scope', getSelectedScope());
        // Today is the default, so leaving it out keeps the common URL short
        // and means a bookmark made today still means "today" tomorrow.
        if (getSelectedDate() !== getLocalTodayStr()) params.set('date', getSelectedDate());
    }
    const url = `${location.pathname}?${params.toString()}`;
    if (replace) history.replaceState(null, '', url);
    else history.pushState(null, '', url);
}

function switchView(viewId, opts) {
    document.querySelectorAll('.rail-item').forEach(el => {
        const isActive = el.dataset.view === viewId;
        el.classList.toggle('active', isActive);
        // The active state was colour-only, which says nothing to a screen
        // reader; aria-current carries the same fact non-visually.
        if (isActive) el.setAttribute('aria-current', 'page');
        else el.removeAttribute('aria-current');
    });
    document.querySelectorAll('.view').forEach(el => el.classList.remove('active'));
    const target = document.getElementById('view-' + viewId);
    if (target) target.classList.add('active');
    closeSettings();
    currentViewId = viewId;

    if (!opts || !opts.fromHistory) writeUrlState(opts && opts.replace);

    // Each tab is its own destination, so it should start at its beginning.
    // Without this the scroll position carried over, and switching tabs while
    // part-way down a page dropped you into the middle of the next one with its
    // title off screen. Only on a real tab change — a poll refresh must not
    // yank the page back to the top while you are reading.
    window.scrollTo(0, 0);

    const mod = Dashboard.tabs[viewId];
    if (mod && typeof mod.onEnter === 'function') mod.onEnter();
}

// Back/forward. Restores the whole shell state rather than just the tab, so
// stepping back from "Overview / week / a past date" lands on exactly that.
function initHistoryNav() {
    window.addEventListener('popstate', () => {
        const state = readUrlState();
        if (state.scope) setSelectedScope(state.scope);
        if (state.date) setSelectedDate(state.date);
        else setSelectedDate(getLocalTodayStr());
        syncOverviewControls();
        switchView(state.view || 'overview', { fromHistory: true });
    });
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

// --- Delegated open handlers ------------------------------------------------
// Every "open this app / category / period" click in the dashboard routes
// through here. Previously each row built an inline onclick with the name
// spliced into the string:
//
//     onclick="openDrilldown('${app.appName}')"
//
// which had two problems. An apostrophe in a name -- legal in Windows
// filenames, so legal in a process name -- produced a JavaScript syntax error
// and made that app permanently unclickable; escaping it to &#39; did not
// help, because the browser decodes the entity back to a quote before the
// handler is ever parsed. And since category names are free-form (the API
// accepts any string), a name could carry markup that the row then re-executed
// on every render.
//
// Reading the value from a data-* attribute fixes both: the string is only
// ever DATA, never re-parsed as code or markup, whatever it contains.
//
// closest() with a comma selector returns the NEAREST matching ancestor, so a
// category chip nested inside an app row wins over the row itself -- which is
// what the old event.stopPropagation() calls were for.
const OPENER_SELECTOR = '[data-open-app], [data-open-cat], [data-open-period]';

function activateOpener(el) {
    if (el.dataset.openCat !== undefined) {
        openCategoryDetail(el.dataset.openCat);
    } else if (el.dataset.openApp !== undefined) {
        openDrilldown(el.dataset.openApp);
    } else {
        openPeriodDetail(el.dataset.openPeriod);
    }
}

function initDelegatedOpeners() {
    document.addEventListener('click', (e) => {
        const el = e.target.closest(OPENER_SELECTOR);
        if (el) activateOpener(el);
    });

    // Keyboard equivalent. These are div/span elements carrying role="button"
    // (they contain their own controls, so a real <button> would nest buttons),
    // and a role alone gets no behaviour for free — the browser only handles
    // Enter/Space on genuine buttons. Space is preventDefault'd because its
    // default action on a non-button is to scroll the page.
    document.addEventListener('keydown', (e) => {
        if (e.key !== 'Enter' && e.key !== ' ' && e.key !== 'Spacebar') return;
        const el = e.target.closest ? e.target.closest(OPENER_SELECTOR) : null;
        if (!el) return;
        e.preventDefault();
        activateOpener(el);
    });
}

// --- Escape closes whatever is layered on top --------------------------------
// There were no keyboard handlers in the dashboard at all, so the only way out
// of a drawer or the Wrapped story was finding its ✕ or clicking the backdrop.
// Closes one layer per press, innermost first, so Escape never skips past
// something the user was looking at.
function initEscapeToClose() {
    document.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape') return;

        const wrapped = document.getElementById('wrapped-overlay');
        if (wrapped && wrapped.style.display !== 'none') { closeWrappedStory(); return; }

        const panel = document.getElementById('wrapped-panel');
        if (panel && panel.style.display !== 'none') { panel.style.display = 'none'; return; }

        if (document.getElementById('cat-drawer').classList.contains('open')) { closeCategoryDetail(); return; }
        if (document.getElementById('dd-drawer').classList.contains('open')) { closeDrilldown(); return; }
        if (document.getElementById('settings-drawer').classList.contains('open')) { closeSettings(); return; }
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

// The top bar needs four numbers, but /api/overview also carries a 365-day
// heatmap array — so this poll was pulling a year of data every 30 seconds to
// update a clock-sized readout, on top of whatever the active tab was fetching
// from the same endpoint. When Overview is the active tab it has already
// fetched this, so its payload is reused and no second request is made.
let _lastOverviewPayload = null;
let _lastOverviewAt = 0;
const OVERVIEW_REUSE_WINDOW_MS = 10000;

function cacheOverviewPayload(data) {
    _lastOverviewPayload = data;
    _lastOverviewAt = Date.now();
}

async function refreshTopBar() {
    const dotEl = document.getElementById('tb-status-dot');
    try {
        const fresh = _lastOverviewPayload && (Date.now() - _lastOverviewAt) < OVERVIEW_REUSE_WINDOW_MS;
        const data = fresh
            ? _lastOverviewPayload
            : await apiFetch(`/api/overview?date=${getLocalTodayStr()}`);
        if (!fresh) cacheOverviewPayload(data);
        if (dotEl) dotEl.classList.remove('offline');

        const focusToday = data.focusToday ?? 0;
        const usual = data.usualDailyFocus ?? 0;
        const allTime = data.focusAllTime ?? 0;

        const focusEl = document.getElementById('tb-focus-value');
        if (focusEl) focusEl.textContent = formatHours(focusToday);

        const allTimeEl = document.getElementById('tb-alltime-value');
        if (allTimeEl) allTimeEl.textContent = formatHours(allTime);

        const topAppsToday = data.topAppsToday ?? [];
        const mostUsedEl = document.getElementById('tb-mostused');
        const mostUsedValueEl = document.getElementById('tb-mostused-value');
        if (mostUsedEl && mostUsedValueEl) {
            if (topAppsToday.length > 0) {
                const top = topAppsToday[0];
                const name = top.appName;
                const mins = top.focusedMinutes ?? 0;
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
    // Always land back on Appearance — reopening Settings while still on a
    // different tab from last time would be a surprising place to land.
    setSettingsTab('appearance', document.querySelector('#settings-tab-toggle button[data-tab="appearance"]'));
    if (Dashboard.tabs.settings) Dashboard.tabs.settings.onEnter();
}
function closeSettings() {
    document.getElementById('settings-overlay').classList.remove('open');
    document.getElementById('settings-drawer').classList.remove('open');
}

// --- Boot ---------------------------------------------------------------------
async function boot() {
    initNav();
    initDelegatedOpeners();
    initEscapeToClose();
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

    loadWrappedAvailable();
    initWrappedPanelOutsideClick();
    initWrappedKeys();

    setInterval(pollCurrentView, POLL_INTERVAL_MS);

    try {
        allCategories = await apiFetch('/api/categories');
    } catch (e) {
        allCategories = ['Development', 'Gaming', 'Productivity', 'Browsing', 'Communication', 'Media Production', 'Music', 'Fun', 'Education', 'Utilities', 'Other'];
    }

    // Restore whatever the URL asked for before the first render, so a
    // bookmarked or refreshed view comes back as it was instead of snapping to
    // Overview/Day. replace:true so the restored state doesn't add a history
    // entry on top of the one the browser already has for this load.
    const urlState = readUrlState();
    if (urlState.scope) setSelectedScope(urlState.scope);
    if (urlState.date) setSelectedDate(urlState.date);
    initOverviewControls();
    initHistoryNav();
    switchView(urlState.view || 'overview', { replace: true });

    checkFirstRun();
}

// --- First run ---------------------------------------------------------------
// On a fresh install every tab is a terse dead end ("No data yet. Time to open
// some apps."), which reads as a failed install rather than as a tracker that
// simply hasn't flushed yet. The tracker writes every ~60s, so the first
// numbers genuinely are about a minute away — saying so is the whole fix.
//
// Keyed off there being no data at all, not off a stored flag, so it disappears
// by itself the moment anything is tracked and never needs dismissing.
async function checkFirstRun() {
    const host = document.getElementById('first-run');
    if (!host) return;
    try {
        const apps = await apiFetch('/api/all-apps');
        const hasData = Array.isArray(apps) && apps.length > 0;
        host.style.display = hasData ? 'none' : 'block';
    } catch (e) {
        // Can't tell — say nothing rather than claim it's a fresh install.
        host.style.display = 'none';
    }
}

document.addEventListener('DOMContentLoaded', boot);
