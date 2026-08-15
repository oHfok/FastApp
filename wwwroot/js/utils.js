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

// --- Time / number formatting (European: 24h, comma-free) ---------------
function formatTime(mins) {
    if (!mins || mins <= 0) return '0m';
    const hrs = Math.floor(mins / 60), m = Math.round(mins % 60);
    if (hrs === 0) return `${m}m`;
    if (m === 0) return `${hrs}h`;
    return `${hrs}h ${m}m`;
}
function formatHours(h) { return formatTime(h * 60); }

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
function showTooltip(evt, text) {
    const box = document.getElementById('tooltip-box');
    if (!box) return;
    box.innerHTML = text;
    box.style.display = 'block';
    let x = evt.clientX + 16, y = evt.clientY + 16;
    if (x + 250 > window.innerWidth) x = evt.clientX - 260;
    box.style.left = x + 'px';
    box.style.top = y + 'px';
}
function hideTooltip() {
    const box = document.getElementById('tooltip-box');
    if (box) box.style.display = 'none';
}

// --- Chart.js shared theme --------------------------------------------------
const CHART_THEME = {
    grid: 'rgba(243,241,234,0.06)',
    tick: '#9C9FAE',
    tooltipBg: 'rgba(20,22,31,0.95)',
    tooltipTitle: '#E8A33D',
    tooltipBody: '#F3F1EA',
    brass: '#E8A33D',
    teal: '#34D3C4',
    violet: '#8B7CFF',
    rose: '#FF6B6B',
    pointBorder: '#0A0B10'
};
if (window.Chart) {
    Chart.defaults.color = CHART_THEME.tick;
    Chart.defaults.font.family = "'Inter', sans-serif";
}

// --- Shared cross-tab state --------------------------------------------------
let allCategories = [];
let selectedScope = 'day'; // day | week | month | year (Overview tab)
let selectedDate = getLocalTodayStr();

function getSelectedDate() { return selectedDate; }
function getSelectedScope() { return selectedScope; }
