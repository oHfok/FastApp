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

// --- Milestone badges (App Detail drawer) -----------------------------------
// Tiered per-app badges based on cumulative all-time focused hours. Fixed
// medal colors on purpose (not theme tokens) — bronze/silver/gold/platinum
// need to read as "medal metal", not shift with the dashboard's color theme.
const MILESTONE_TIERS = [
    { name: 'Bronze', hours: 10, color: '#CD7F32', icon: '🥉' },
    { name: 'Silver', hours: 50, color: '#C0C0C0', icon: '🥈' },
    { name: 'Gold', hours: 150, color: '#FFD700', icon: '🥇' },
    { name: 'Platinum', hours: 500, color: '#8FE3FF', icon: '💎' }
];
// Returns { tier, next, hoursToNext } — tier is null below the first threshold,
// next/hoursToNext are null once Platinum is reached (nothing further to show).
function getMilestoneProgress(allTimeHours) {
    const hours = allTimeHours || 0;
    let tier = null;
    let next = null;
    for (const t of MILESTONE_TIERS) {
        if (hours >= t.hours) tier = t;
        else { next = t; break; }
    }
    return { tier, next, hoursToNext: next ? Math.max(0, next.hours - hours) : null };
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
function timelineSegmentsHtml(sessions) {
    if (!sessions || sessions.length === 0) {
        return `<div class="empty-state" style="border:none;background:none;">No sessions recorded for this day.</div>`;
    }
    return sessions.map(s => {
        const name = s.appName || s.AppName;
        const cat = s.category || s.Category;
        const startStr = s.start || s.Start;
        const endStr = s.end || s.End;
        const dur = s.durationMinutes ?? s.DurationMinutes ?? 0;
        const startMins = s.startMinutes ?? s.StartMinutes ?? 0;
        const title = s.windowTitle ?? s.WindowTitle;
        const left = (startMins / 1440) * 100;
        const width = Math.max((dur / 1440) * 100, 0.25);
        // Window titles (and, defensively, app names) are attacker-controllable —
        // any webpage can set its own tab title — so they're carried as plain data
        // in data-* attributes and rendered via showSessionTooltip's textContent
        // builder, never concatenated into an HTML string. escapeHtml here is
        // just what makes the raw value safe to sit inside the attribute's own
        // quotes; showSessionTooltip does the one read-and-display step.
        return `<div class="timeline-seg" style="left:${left}%;width:${width}%;background:${timelineSegColor(name, cat)}"
                    data-name="${escapeHtml(name)}" data-range="${escapeHtml(startStr + ' – ' + endStr)}"
                    data-dur="${escapeHtml(formatTime(dur))}" data-title="${title ? escapeHtml(title) : ''}"
                    onmousemove="showSessionTooltip(event, this)" onmouseleave="hideTooltip()"
                    onclick="openDrilldown(this.dataset.name)"></div>`;
    }).join('');
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
