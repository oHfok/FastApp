/* ==========================================================
   UTILS — shared helpers, European locale formatting, the
   chronometer ring builder, and cross-tab state.
   Loaded first; plain globals so every tab script can see them.
   ========================================================== */

const Dashboard = { tabs: {} };

// --- HTML escaping ---------------------------------------------------------
// Window titles are attacker-controllable (any webpage can set its own tab
// title) and get rendered via innerHTML, so anything sourced from a window
// title MUST go through this before it touches the DOM.
function escapeHtml(str) {
    if (str == null) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

// --- API access -------------------------------------------------------------
// One entry point for every call to the local backend, so three things that
// were previously each tab's own problem happen in one place:
//
//  1. A non-2xx response throws. Several tabs called .json() straight on the
//     response, so a 500 (whose body is a JSON error object) was parsed as if
//     it were data and rendered as zeroes.
//  2. Success and failure are recorded, which drives the stale-data banner
//     below. Tabs used to swallow failures into console.error and leave the
//     previous numbers on screen indefinitely with no sign they had stopped
//     updating -- the worst outcome for an app whose whole value is accurate
//     measurement.
//  3. Requests can be cancelled per tab (see abortableSignal), so switching
//     scope or tab quickly can't have a slow earlier response land last and
//     overwrite the view the user actually asked for.
async function apiFetch(url, options) {
    try {
        const res = await fetch(url, options);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        DataHealth.reportOk();
        return data;
    } catch (err) {
        // An aborted request is us cancelling deliberately, not the backend
        // failing -- it must not count towards the failure streak.
        if (err && err.name === 'AbortError') throw err;
        DataHealth.reportFail();
        throw err;
    }
}

// Per-key AbortControllers. Calling this again for the same key aborts whatever
// that key had in flight, so each tab keeps at most one live request set.
const _abortControllers = {};
function abortableSignal(key) {
    if (_abortControllers[key]) _abortControllers[key].abort();
    const controller = new AbortController();
    _abortControllers[key] = controller;
    return controller.signal;
}
function isAbort(err) { return !!err && err.name === 'AbortError'; }

// --- Connection health ------------------------------------------------------
// Shows a persistent banner once the backend has failed repeatedly, so stale
// numbers are never presented as current. Deliberately tolerant of a single
// blip: the tracker restarts, the app gets busy, and one dropped request during
// a 12-second poll is not worth shouting about.
const DataHealth = {
    consecutiveFailures: 0,
    lastOkAt: null,
    FAILURES_BEFORE_WARNING: 2,

    reportOk() {
        this.consecutiveFailures = 0;
        this.lastOkAt = Date.now();
        this._render();
    },
    reportFail() {
        this.consecutiveFailures++;
        this._render();
    },
    _render() {
        const el = document.getElementById('stale-banner');
        if (!el) return;

        if (this.consecutiveFailures < this.FAILURES_BEFORE_WARNING) {
            el.style.display = 'none';
            return;
        }
        const since = this.lastOkAt
            ? `Last updated ${describeAgo(this.lastOkAt)}.`
            : 'No data has loaded yet.';
        el.querySelector('.stale-banner-text').textContent =
            `Not updating — can't reach FastApp. ${since}`;
        el.style.display = 'flex';
    }
};

function describeAgo(ts) {
    const secs = Math.max(0, Math.round((Date.now() - ts) / 1000));
    if (secs < 60) return 'less than a minute ago';
    const mins = Math.round(secs / 60);
    if (mins < 60) return `${mins} minute${mins === 1 ? '' : 's'} ago`;
    const hrs = Math.round(mins / 60);
    return `${hrs} hour${hrs === 1 ? '' : 's'} ago`;
}

// --- Duration axis ----------------------------------------------------------
// Chart.js picks tick values that are round numbers of whatever unit the data
// is in — minutes, here — and each was then formatted as a duration. Round
// minutes are not round durations, so axes read "1h 40m, 3h 20m, 5h, 6h 40m,
// 8h 20m", which has to be decoded rather than glanced at.
//
// Pinning the step to a whole number of hours (or a clean sub-hour step for
// small ranges) makes the labels land on 0, 2h, 4h, 6h instead. Returns the
// scale options to spread into a chart's `y`.
function durationAxis(maxMinutes, theme, extra) {
    const NICE_MIN = [1, 2, 5, 10, 15, 30];           // sub-hour steps, in minutes
    const NICE_HRS = [1, 2, 3, 4, 6, 8, 12, 24, 48];  // whole-hour steps
    const target = 6;                                  // aim for ~6 gridlines
    const raw = Math.max(1, maxMinutes) / target;

    let step = NICE_MIN.find(m => m >= raw);
    if (step === undefined) {
        const hrs = NICE_HRS.find(h => h * 60 >= raw);
        step = (hrs === undefined ? Math.ceil(raw / 1440) * 1440 : hrs * 60);
    }

    return Object.assign({
        grid: { color: theme.grid },
        ticks: {
            color: theme.tick,
            font: { size: 10 },
            stepSize: step,
            // With the step pinned, formatTime now only ever sees clean values.
            callback: (v) => formatTime(v)
        }
    }, extra || {});
}

// --- Heat grid --------------------------------------------------------------
// Day-cell heatmaps were implemented four times — renderDayHeatmap in
// overview.js, monthHeatmapHtml and yearHeatmapHtml in periods.js, and
// renderInsightsHeatmap — each rebuilding the same cell and legend markup with
// slightly different assumptions. The legend in particular was copy-pasted
// verbatim four times, so any change to the colour scale had to be made in four
// places and would silently drift if one was missed.
//
// Callers supply data and layout; this owns the markup.
function heatLegendHtml() {
    return `
        <div class="heat-legend">
            <span>Less</span>
            <span class="heat-legend-swatch" style="background:var(--bg-raised)"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.3)}"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.6)}"></span>
            <span class="heat-legend-swatch" style="background:${themeAccentAlpha(0.9)}"></span>
            <span>More</span>
        </div>`;
}

// cells: [{ intensity, tooltip, className?, style? }]
function heatCellsHtml(cells, cellClass) {
    return cells.map(c => {
        const cls = c.className ? `${cellClass} ${c.className}` : cellClass;
        const bg = c.intensity === null ? '' : `background:${heatColor(c.intensity)}`;
        // Tooltips here are built by us from dates and durations, never from an
        // app name or window title — showTooltip parses its argument as HTML.
        return `<div class="${cls}" style="${bg}${c.style || ''}"`
             + (c.tooltip ? ` onmousemove="showTooltip(event, '${c.tooltip}')" onmouseleave="hideTooltip()"` : '')
             + `></div>`;
    }).join('');
}

// --- Loading state ----------------------------------------------------------
// Periods and Activity showed "Loading…"; Overview, Leaderboard, Insights and
// All Applications showed nothing at all and swapped content in when it
// arrived. Identical-looking panels behaving differently gave no reliable way
// to tell "working on it" from "stuck". One treatment everywhere fixes that.
//
// Only ever used for a first load. Poll refreshes deliberately leave the
// existing content alone — replacing good data with a skeleton every 12 seconds
// would be worse than the problem it solves.
function loadingRowsHtml(rows) {
    const n = rows || 5;
    return `<div class="skeleton-list">${
        Array.from({ length: n }, () => `<div class="skeleton-row"></div>`).join('')
    }</div>`;
}

// True the first time a container is asked to load and it has nothing in it —
// which is exactly when a skeleton helps and a refresh does not.
function isEmptyContainer(el) {
    return !el || el.children.length === 0 || !!el.querySelector('.skeleton-list');
}

// --- Failure state --------------------------------------------------------
// One shared renderer for "this didn't load", so every tab fails the same way
// and always offers a retry. Replaces per-tab messages that named C# source
// files and told the user to edit them -- those were development leftovers that
// fired on any failure at all (a 500, a restart mid-request, a dropped
// connection), long after the endpoints they described had shipped.
//
// retryFnName is the name of a global function, called from the button; it is
// written by us at the call site, never derived from data.
function errorStateHtml(title, detail, retryFnName) {
    const retry = retryFnName
        ? `<button class="btn btn-ghost" onclick="${retryFnName}()">Try again</button>`
        : '';
    return `
        <div class="error-state">
            <div class="error-state-title">${escapeHtml(title)}</div>
            <div class="error-state-detail">${escapeHtml(detail)}</div>
            ${retry}
        </div>`;
}

// --- Category -> accent color -------------------------------------------
const categoryColors = {
    'Development': '#8B7CFF',
    'Gaming': '#4E4599',
    'Productivity': '#E8A33D',
    'Browsing': '#34D3C4',
    'Communication': '#1D766D',
    'Media Production': '#FF6B6B',
    'Music': '#8C3A3A',
    'Fun': '#FF9F6B',
    'Education': '#34D3C4',
    'Utilities': '#5B5F71',
    'Other': '#3A3D4A'
};
function catColor(cat) { return categoryColors[cat] || categoryColors['Other']; }

// The same category colour, lightened until it is readable as small text.
//
// The palette is built for FILLS -- swatches, bars, timeline blocks -- where a
// dark violet or teal sits happily on a dark page. Used as 11px label text the
// same values land at 2.5:1: Gaming (#4E4599) and Communication (#1D766D) are
// simply too dark to read. Mixing toward white in small steps keeps the hue
// recognisably the category's while clearing the 4.5:1 body-text threshold.
const CAT_TEXT_TARGET_RATIO = 4.5;
const CAT_TEXT_BACKDROP = [0x14, 0x16, 0x1C];   // a row surface over --bg
const _catTextCache = {};

function catTextColor(cat) {
    if (_catTextCache[cat]) return _catTextCache[cat];

    const hex = catColor(cat);
    let rgb = [1, 3, 5].map(i => parseInt(hex.substr(i, 2), 16));
    const lin = (v) => { const c = v / 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
    const lum = (c) => 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]);
    const ratio = (a, b) => { const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
                              return (hi + 0.05) / (lo + 0.05); };

    // Up to 20 steps of 6% toward white -- enough to lift the darkest entry in
    // the palette, and it stops as soon as the threshold is met so lighter
    // categories keep their colour untouched.
    for (let i = 0; i < 20 && ratio(rgb, CAT_TEXT_BACKDROP) < CAT_TEXT_TARGET_RATIO; i++) {
        rgb = rgb.map(v => Math.round(v + (255 - v) * 0.06));
    }
    const out = `rgb(${rgb[0]}, ${rgb[1]}, ${rgb[2]})`;
    _catTextCache[cat] = out;
    return out;
}

// Letter avatars used to tint the GLYPH with the category colour, which put dark
// hues straight onto a dark ground -- "Other" (the category two thirds of apps
// fall into) measured 1.73:1, Gaming 2.35:1. Using the colour as a translucent
// background with light text keeps the colour coding while making the letter
// readable regardless of which category it belongs to.
function avatarStyle(cat) {
    const c = catColor(cat);
    return `background:${c}2E;border-color:${c}80;color:var(--text)`;
}

// --- Milestone badges (App Detail drawer) -----------------------------------
// The tier ladder itself (names + hour thresholds) is owned by the backend and
// arrives with /api/app-details — see Services/MilestoneTiers.cs. It used to be
// duplicated here, which meant the drawer and Wrapped could silently disagree
// about the same app if either copy was edited alone.
//
// Only the colors live here, because they're presentation. Fixed medal values
// on purpose (not theme tokens): bronze/silver/gold/platinum need to read as
// "medal metal" rather than shift with the dashboard's color theme.
const MILESTONE_TIER_COLORS = {
    Bronze: '#CD7F32',
    Silver: '#C0C0C0',
    Gold: '#FFD700',
    Platinum: '#8FE3FF'
};
function milestoneTierColor(name) { return MILESTONE_TIER_COLORS[name] || 'var(--text-faint)'; }

// Returns { tier, next, hoursToNext } for a ladder supplied by the caller.
// tier is null below the first threshold; next/hoursToNext are null once the
// top tier is reached (nothing further to show).
function getMilestoneProgress(allTimeHours, tiers) {
    const hours = allTimeHours || 0;
    const ladder = tiers || [];
    let tier = null;
    let next = null;
    for (const t of ladder) {
        const tierHours = t.hours ?? 0;
        if (hours >= tierHours) tier = t;
        else { next = t; break; }
    }
    const nextHours = next ? (next.hours ?? 0) : null;
    return { tier, next, hoursToNext: next ? Math.max(0, nextHours - hours) : null };
}

// --- Display names ----------------------------------------------------------
// Names come from the process, so the interface is full of things like
// "Valorant-win64-shipping", "Fortniteclient-win64-shipping" and
// "Eaanticheat.gameservicelauncher". They are long enough to be truncated in
// most columns and they make the product read like a task manager rather than a
// description of someone's day.
//
// Purely cosmetic: the tracked name is never modified, every list keeps the raw
// value in its title attribute, and lookups (categories, drilldown, hidden
// apps) all still key off the real name. Suffix rules rather than a hand-built
// list of apps, so new games and launchers benefit without an update.
const APP_NAME_SUFFIXES = [
    /-win64-shipping$/i, /-win32-shipping$/i, /-shipping$/i,
    /-windows-msvc-[a-z0-9.-]+$/i, /-win-x64$/i, /-x64$/i, /\.tmp$/i
];
function displayAppName(name) {
    if (!name) return '';
    let out = String(name);
    for (const rx of APP_NAME_SUFFIXES) out = out.replace(rx, '');
    out = out.replace(/[-_.]+$/, '');
    // Never return an empty label just because the whole name was suffix.
    return out.length ? out : String(name);
}

// --- Per-app accent color (Timeline ribbon "Individual" mode) --------------
// Category color alone makes a timeline unreadable once more than one or two
// apps share a category — they render as one indistinguishable block. This
// hashes the app name to one of a wide, evenly-spaced set of hues instead, so
// different apps get visually distinct colors. Deterministic (same app name
// always gets the same hue, every render, every day) rather than
// assignment-order-based, so it stays recognizable over time instead of
// shuffling depending on which apps happen to appear first in a given day.
function hashString(str) {
    let hash = 0;
    for (let i = 0; i < str.length; i++) {
        hash = (hash * 31 + str.charCodeAt(i)) | 0;
    }
    return Math.abs(hash);
}
const APP_COLOR_HUE_BUCKETS = 20; // 360/20 = 18° apart — a wide, well-separated spread
function appColor(name) {
    if (!name) return catColor('Other');
    const hue = (hashString(name) % APP_COLOR_HUE_BUCKETS) * (360 / APP_COLOR_HUE_BUCKETS);
    return `hsl(${hue}, 68%, 60%)`;
}

// Persisted in Settings ("Timeline Colors"): 'app' colors each session by its
// own app (the default — this is what people actually asked for), 'category'
// falls back to the old category-color behavior.
const TIMELINE_COLOR_MODE_KEY = 'fastapp-timeline-color-mode';
function getTimelineColorMode() {
    return localStorage.getItem(TIMELINE_COLOR_MODE_KEY) || 'app';
}
function timelineSegColor(name, cat) {
    return getTimelineColorMode() === 'category' ? catColor(cat) : appColor(name);
}

// --- Time / number formatting (European: 24h, comma-free) ---------------
function formatTime(mins) {
    if (!mins || mins <= 0) return '0m';
    const hrs = Math.floor(mins / 60), m = Math.round(mins % 60);
    if (hrs === 0) return `${m}m`;
    if (m === 0) return `${hrs}h`;
    return `${hrs}h ${m}m`;
}
function formatHours(h) { return formatTime(h * 60); }

function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return '0 KB';
    const units = ['bytes', 'KB', 'MB', 'GB'];
    let val = bytes, i = 0;
    while (val >= 1024 && i < units.length - 1) { val /= 1024; i++; }
    return `${val.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

// --- European calendar helpers (weeks start Monday) ----------------------
const DAY_NAMES = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
const DAY_SHORT = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const MONTH_NAMES = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

function pad(n) { return n.toString().padStart(2, '0'); }

function getLocalTodayStr() {
    const d = new Date();
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

// Mon=0 ... Sun=6
function isoDow(date) { return (date.getDay() + 6) % 7; }

function mondayOf(date) {
    const d = new Date(date);
    d.setDate(d.getDate() - isoDow(d));
    d.setHours(0, 0, 0, 0);
    return d;
}

function fmtDateEU(date) {
    return `${pad(date.getDate())}.${pad(date.getMonth() + 1)}.${date.getFullYear()}`;
}

function fmtDateLong(date) {
    return `${date.getDate()} ${MONTH_NAMES[date.getMonth()]} ${date.getFullYear()}`;
}

function parseDateStr(s) {
    const [y, m, d] = s.split('-').map(Number);
    return new Date(y, m - 1, d);
}

// --- Trend pill -----------------------------------------------------------
function trendPill(current, previous, label) {
    if (previous === 0 && current === 0) return `<span class="trend-pill trend-flat">No prior data</span>`;
    if (previous === 0) return `<span class="trend-pill trend-new">NEW</span>`;
    const pct = ((current - previous) / previous) * 100;
    const rounded = Math.abs(Math.round(pct));
    if (pct > 0) return `<span class="trend-pill trend-up">&#9650; ${rounded}%${label ? ' vs ' + label : ''}</span>`;
    if (pct < 0) return `<span class="trend-pill trend-down">&#9660; ${rounded}%${label ? ' vs ' + label : ''}</span>`;
    return `<span class="trend-pill trend-flat">No change</span>`;
}

// --- Chronometer ring (signature element) ---------------------------------
// Renders an SVG arc dial. pct is 0-100. Returns HTML string.
function chronoRing({ pct, size = 120, stroke = 10, centerHtml = '', mini = false }) {
    const r = (size - stroke) / 2;
    const c = size / 2;
    const circumference = 2 * Math.PI * r;
    const clamped = Math.max(0, Math.min(100, pct));
    const offset = circumference * (1 - clamped / 100);
    const wrapClass = mini ? 'chrono-ring-wrap chrono-mini' : 'chrono-ring-wrap';
    return `
        <div class="${wrapClass}" style="width:${size}px;height:${size}px;">
            <svg width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">
                <circle class="chrono-track" cx="${c}" cy="${c}" r="${r}" stroke-width="${stroke}"></circle>
                <circle class="chrono-arc" cx="${c}" cy="${c}" r="${r}" stroke-width="${stroke}"
                    stroke-dasharray="${circumference}" stroke-dashoffset="${offset}"></circle>
            </svg>
            <div class="chrono-center">${centerHtml}</div>
        </div>`;
}

// --- Custom fixed-position tooltip -----------------------------------------
function positionTooltipBox(box, evt) {
    box.style.display = 'block';
    let x = evt.clientX + 16, y = evt.clientY + 16;
    if (x + 250 > window.innerWidth) x = evt.clientX - 260;
    box.style.left = x + 'px';
    box.style.top = y + 'px';
}
// text is trusted HTML (dates/times/labels the app itself generated) — every
// call site passes a literal template string, never attacker-controlled data.
// For anything sourced from user/OS input (app names, window titles), use
// showSessionTooltip instead, which never parses its content as markup.
function showTooltip(evt, text) {
    const box = document.getElementById('tooltip-box');
    if (!box) return;
    box.innerHTML = text;
    positionTooltipBox(box, evt);
}
// Safe tooltip renderer for content that may be attacker-controlled — window
// titles are the clearest case (any webpage can set its own tab title) but
// app names aren't fully trustworthy either. Reads pre-escaped values back
// out of data-* attributes (safe: a single attribute-decode just recovers the
// original string as DATA) and builds the tooltip with textContent/createElement
// instead of innerHTML, so the string is never re-parsed as markup no matter
// what it contains — unlike showTooltip(), where a value that survives one
// escape-decode-escape round trip could still land as live HTML.
function showSessionTooltip(evt, el) {
    const box = document.getElementById('tooltip-box');
    if (!box) return;
    const { name, range, dur, title } = el.dataset;
    box.innerHTML = '';
    box.appendChild(document.createTextNode(name));
    box.appendChild(document.createElement('br'));
    box.appendChild(document.createTextNode(range));
    box.appendChild(document.createElement('br'));
    box.appendChild(document.createTextNode(dur));
    if (title) {
        box.appendChild(document.createElement('br'));
        const span = document.createElement('span');
        span.style.opacity = '0.75';
        span.textContent = title;
        box.appendChild(span);
    }
    positionTooltipBox(box, evt);
}
function hideTooltip() {
    const box = document.getElementById('tooltip-box');
    if (box) box.style.display = 'none';
}

// --- Theme-aware heatmap fills -----------------------------------------------
// Every heatmap (Overview, Insights, Weeks & Months) shades cells by mixing an
// accent color with a computed alpha — that requires an actual R,G,B triple,
// not just a CSS variable name, so a hex custom property has to be parsed at
// runtime rather than hardcoded like it used to be.
function themeAccentRgb() {
    const hex = getComputedStyle(document.documentElement).getPropertyValue('--brass').trim();
    const parts = hex.replace('#', '').match(/.{1,2}/g);
    if (!parts || parts.length < 3) return '232, 163, 61'; // fallback: default brass
    return parts.slice(0, 3).map(h => parseInt(h, 16)).join(', ');
}
function themeAccentAlpha(alpha) {
    return `rgba(${themeAccentRgb()}, ${alpha})`;
}
function heatColor(intensity, minAlpha = 0.15, alphaRange = 0.75) {
    return intensity > 0 ? themeAccentAlpha(minAlpha + intensity * alphaRange) : 'var(--bg-raised)';
}

// --- Timeline ribbon segments ------------------------------------------------
// Shared between Overview's day view and Weeks & Months' "day" period detail
// (a day period's Daily Activity is this same ribbon, not a heatmap — breaking
// a single day down into "days" would be circular). Returns just the inner
// HTML for a .timeline-track; callers own their own wrapper/ticks markup.
// Persisted like the colour mode: 'day' spans the full 24 hours, 'activity'
// trims the axis to the first and last recorded session. On a normal day most
// of the 24h is asleep or away, so the full-day ribbon is ~90% empty and the
// hours that matter are squeezed into a few pixels. Trimming makes the same
// data several times larger on screen without changing what is recorded.
const TIMELINE_RANGE_KEY = 'fastapp-timeline-range';
function getTimelineRangeMode() {
    return localStorage.getItem(TIMELINE_RANGE_KEY) || 'day';
}

// Returns the window the ribbon spans, in minutes past midnight, plus the tick
// labels to print under it. Callers render the ticks so both the Overview and
// the Periods ribbon stay in step with whatever range is in force.
function timelineWindow(sessions) {
    const FULL = { startMin: 0, endMin: 1440 };
    if (getTimelineRangeMode() !== 'activity' || !sessions || !sessions.length) return FULL;

    let lo = Infinity, hi = -Infinity;
    sessions.forEach(s => {
        const st = s.startMinutes ?? 0;
        lo = Math.min(lo, st);
        hi = Math.max(hi, st + (s.durationMinutes ?? 0));
    });
    if (!isFinite(lo) || !isFinite(hi) || hi <= lo) return FULL;

    // Round out to whole hours and keep a little air either side, so segments
    // never start flush against the edge.
    const startMin = Math.max(0, Math.floor(lo / 60) * 60 - 60);
    const endMin = Math.min(1440, Math.ceil(hi / 60) * 60 + 60);
    // Below a few hours the trimming stops being worth the loss of context.
    if (endMin - startMin >= 1200) return FULL;
    return { startMin, endMin };
}

function timelineTicksHtml(win) {
    const span = win.endMin - win.startMin;
    const steps = 4;
    let out = '';
    for (let i = 0; i <= steps; i++) {
        const m = win.startMin + (span / steps) * i;
        out += `<span>${pad(Math.floor(m / 60) % 24)}:${pad(Math.round(m % 60))}</span>`;
    }
    return out;
}

function timelineSegmentsHtml(sessions) {
    if (!sessions || sessions.length === 0) {
        return `<div class="empty-state" style="border:none;background:none;">No sessions recorded for this day.</div>`;
    }
    const win = timelineWindow(sessions);
    const span = Math.max(1, win.endMin - win.startMin);
    return sessions.map(s => {
        const name = s.appName;
        const cat = s.category;
        const startStr = s.start;
        const endStr = s.end;
        const dur = s.durationMinutes ?? 0;
        const startMins = s.startMinutes ?? 0;
        const title = s.windowTitle;
        const left = ((startMins - win.startMin) / span) * 100;
        const width = Math.max((dur / span) * 100, 0.25);
        // Window titles (and, defensively, app names) are attacker-controllable —
        // any webpage can set its own tab title — so they're carried as plain data
        // in data-* attributes and rendered via showSessionTooltip's textContent
        // builder, never concatenated into an HTML string. escapeHtml here is
        // just what makes the raw value safe to sit inside the attribute's own
        // quotes; showSessionTooltip does the one read-and-display step.
        return `<div class="timeline-seg" style="left:${left}%;width:${width}%;background:${timelineSegColor(name, cat)}"
                    data-name="${escapeHtml(name)}" data-range="${escapeHtml(startStr + ' – ' + endStr)}"
                    data-dur="${escapeHtml(formatTime(dur))}" data-title="${title ? escapeHtml(title) : ''}"
                    data-open-app="${escapeHtml(name)}" role="button" tabindex="0"
                    aria-label="${escapeHtml(`${name}, ${startStr} to ${endStr}, ${formatTime(dur)}`)}"
                    onmousemove="showSessionTooltip(event, this)" onmouseleave="hideTooltip()"></div>`;
    }).join('');
}

// The ribbon identified its blocks on hover alone, so a day that was glanced at
// rather than explored said nothing about what was in it. Labels are applied
// after render, not baked into the markup, because whether a block can hold text
// depends on its pixel width -- and the markup only knows a percentage, which
// means nothing until the container has been laid out.
function labelWideTimelineSegments(trackEl) {
    if (!trackEl) return;
    trackEl.querySelectorAll('.timeline-seg').forEach(seg => {
        const old = seg.querySelector('.timeline-seg-label');
        if (old) old.remove();

        const name = displayAppName(seg.dataset.name || '');
        if (!name) return;

        // ~6.5px per character at 11px/600, plus 14px of breathing room. Below
        // that the name would be clipped mid-word, which reads worse than the
        // bare block it replaced -- so narrow blocks keep the hover-only path.
        if (seg.clientWidth < Math.max(name.length * 6.5 + 14, 44)) return;

        const label = document.createElement('span');
        label.className = 'timeline-seg-label';
        label.textContent = name;
        // Blocks are coloured per app -- hsl(h, 68%, 60%) -- or per category, and
        // both palettes span very light to very dark. A fixed white label is
        // 10.8:1 on 'Other' (#3A3D4A) but only 2.14:1 on 'Productivity'
        // (#E8A33D), so the ink has to be chosen from whatever it lands on.
        label.classList.add(timelineLabelInk(getComputedStyle(seg).backgroundColor));
        seg.appendChild(label);
    });
}

// WCAG relative luminance of an "r, g, b" string.
function relativeLuminance(cssColor) {
    const parts = (cssColor.match(/[\d.]+/g) || []).slice(0, 3).map(Number);
    if (parts.length < 3) return 0;
    const lin = parts.map(v => {
        const c = v / 255;
        return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2];
}

// Which of the two label inks reads better on this block. Scoring both and
// taking the winner rather than testing luminance against a threshold: any
// fixed cut is wrong somewhere, and #E8A33D (luminance 0.44) proved it -- it
// fell on the dark side of a 0.45 cut and got white at 2.16:1 when the dark
// ink scores 8:1 on the same colour.
const TIMELINE_INK_DARK_LUM = 0.00669;   // #14161C, matching .on-light in CSS
function timelineLabelInk(cssColor) {
    const bg = relativeLuminance(cssColor);
    const vsWhite = 1.05 / (bg + 0.05);
    const vsDark = (bg + 0.05) / (TIMELINE_INK_DARK_LUM + 0.05);
    return vsDark > vsWhite ? 'on-light' : 'on-dark';
}

// --- Markdown (release notes) ------------------------------------------------
// A deliberately small subset: headings, bold, italic, inline code, links,
// bullet lists, tables and rules -- which is everything the release notes
// actually use. Not a general markdown engine, and not trying to be.
//
// Escaping happens FIRST, on the whole string, and every transform below runs
// over already-escaped text. Notes are authored on GitHub rather than by a
// stranger, but they arrive over the network and get written with innerHTML,
// which is exactly the shape of thing that should not be trusted on the way in.
function renderMarkdown(md) {
    if (!md) return '';
    const lines = escapeHtml(md).replace(/\r\n/g, '\n').split('\n');
    const out = [];
    let paragraph = [];
    let listItems = [];
    let tableRows = [];

    const flushParagraph = () => {
        if (!paragraph.length) return;
        out.push(`<p>${inline(paragraph.join(' '))}</p>`);
        paragraph = [];
    };
    const flushList = () => {
        if (!listItems.length) return;
        out.push(`<ul>${listItems.map(li => `<li>${inline(li)}</li>`).join('')}</ul>`);
        listItems = [];
    };
    const flushTable = () => {
        if (!tableRows.length) return;
        const cells = row => row.replace(/^\||\|$/g, '').split('|').map(c => c.trim());
        const head = cells(tableRows[0]);
        // A separator row (|---|---|) marks the row above as headers; without one
        // the block is still rendered, just with no header styling.
        const hasHeader = tableRows.length > 1 && /^[\s|:-]+$/.test(tableRows[1]);
        const bodyRows = tableRows.slice(hasHeader ? 2 : 1);
        const thead = hasHeader ? `<thead><tr>${head.map(c => `<th>${inline(c)}</th>`).join('')}</tr></thead>` : '';
        const tbody = (hasHeader ? bodyRows : tableRows).map(r =>
            `<tr>${cells(r).map(c => `<td>${inline(c)}</td>`).join('')}</tr>`).join('');
        out.push(`<div class="md-table-wrap"><table class="md-table">${thead}<tbody>${tbody}</tbody></table></div>`);
        tableRows = [];
    };
    const flushAll = () => { flushParagraph(); flushList(); flushTable(); };

    for (const raw of lines) {
        const line = raw.trimEnd();

        if (!line.trim()) { flushAll(); continue; }

        if (/^\|.*\|$/.test(line.trim())) { flushParagraph(); flushList(); tableRows.push(line.trim()); continue; }
        if (tableRows.length) flushTable();

        const heading = line.match(/^(#{1,6})\s+(.*)$/);
        if (heading) {
            flushAll();
            // Notes start at ###; clamping keeps them from out-ranking the page's
            // own headings in the document outline.
            const level = Math.min(6, Math.max(3, heading[1].length));
            out.push(`<h${level} class="md-h">${inline(heading[2])}</h${level}>`);
            continue;
        }
        if (/^(-{3,}|\*{3,}|_{3,})$/.test(line.trim())) { flushAll(); out.push('<hr class="md-hr">'); continue; }

        const bullet = line.match(/^\s*[-*+]\s+(.*)$/);
        if (bullet) { flushParagraph(); listItems.push(bullet[1]); continue; }

        flushList();
        paragraph.push(line.trim());
    }
    flushAll();
    return out.join('');
}

function inline(text) {
    return text
        .replace(/`([^`]+)`/g, '<code class="md-code">$1</code>')
        .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
        .replace(/(^|[\s(])\*([^*\n]+)\*(?=[\s.,;:)!?]|$)/g, '$1<em>$2</em>')
        // Only http(s) becomes a link. escapeHtml has already run, so a javascript:
        // or data: target cannot be smuggled through -- but spelling out the
        // allowed schemes means that stays true if the escaping ever changes.
        .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g,
                 '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>');
}

// --- Chart.js shared theme --------------------------------------------------
// A function, not a static object — Chart.js canvases are drawn on a <canvas>,
// so CSS alone can't re-theme them. Reading the live custom-property values at
// render time is what lets a theme switch actually recolor the charts instead
// of leaving them stuck on whatever theme was active on page load.
function getChartTheme() {
    const s = getComputedStyle(document.documentElement);
    const v = (name) => s.getPropertyValue(name).trim();
    return {
        grid: v('--chart-grid'),
        tick: v('--text-dim'),
        tooltipBg: v('--chart-tooltip-bg'),
        tooltipTitle: v('--brass'),
        tooltipBody: v('--text'),
        brass: v('--brass'),
        teal: v('--teal'),
        violet: v('--violet'),
        rose: v('--rose'),
        pointBorder: v('--bg'),
        fontBody: v('--font-body') || "'Inter', sans-serif",
        fontMono: v('--font-mono') || "'JetBrains Mono', monospace"
    };
}
if (window.Chart) {
    Chart.defaults.color = getChartTheme().tick;
    Chart.defaults.font.family = "'Inter', sans-serif";
}

// --- Shared cross-tab state --------------------------------------------------
let allCategories = [];
let selectedScope = 'day'; // day | week | month | year (Overview tab)
let selectedDate = getLocalTodayStr();

function getSelectedDate() { return selectedDate; }
function getSelectedScope() { return selectedScope; }
// Setters exist so restoring from the URL (or stepping through history) goes
// through one place instead of assigning these globals from several files.
function setSelectedScope(scope) { selectedScope = scope; }
function setSelectedDate(dateStr) { selectedDate = dateStr; }

function shiftSelectedDate(days) {
    const d = parseDateStr(selectedDate);
    d.setDate(d.getDate() + days);
    // Never past today — there is no data for the future, and an empty
    // "tomorrow" reads as the app being broken rather than as a boundary.
    const today = parseDateStr(getLocalTodayStr());
    if (d > today) return false;
    selectedDate = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
    return true;
}
