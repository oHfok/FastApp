/* ==========================================
   UTILS — shared helpers, chart defaults, and shared state.
   Loaded first. Everything below is intentionally left as
   plain top-level declarations (no IIFE) so every other
   script file on the page can see them, exactly like the
   old single-file version where it was all one scope.
   ========================================== */

// --- Time formatting ---
function formatTime(t) {
    if (!t || t <= 0) return '0m';
    let hrs = Math.floor(t / 60), mins = Math.floor(t % 60);
    return hrs === 0 ? `${mins}m` : (mins === 0 ? `${hrs}h` : `${hrs}h ${mins}m`);
}
function formatHours(h) { return formatTime(h * 60); }

// --- Trend badge generators ---
function getBlockTrendHtml(current, previous, label) {
    if (previous === 0 && current === 0) return `<div class="mt-2 text-xs font-semibold text-slate-400 bg-slate-500/10 px-2 py-0.5 rounded w-max">- No prior data</div>`;
    if (previous === 0) return `<div class="mt-2 text-xs font-semibold text-blue-400 bg-blue-500/10 px-2 py-0.5 rounded w-max">NEW</div>`;
    let percentChange = ((current - previous) / previous) * 100;
    let rounded = Math.abs(Math.round(percentChange));
    if (percentChange > 0) return `<div class="mt-2 text-xs font-semibold text-emerald-400 bg-emerald-500/10 px-2 py-0.5 rounded w-max">▲ ${rounded}% vs ${label}</div>`;
    if (percentChange < 0) return `<div class="mt-2 text-xs font-semibold text-rose-400 bg-rose-500/10 px-2 py-0.5 rounded w-max">▼ ${rounded}% vs ${label}</div>`;
    return `<div class="mt-2 text-xs font-semibold text-slate-400 bg-slate-500/10 px-2 py-0.5 rounded w-max">▶ No Change</div>`;
}

function getInlineTrendHtml(current, previous) {
    if (previous === 0 && current === 0) return `<span class="text-slate-500 text-[10px] ml-1">-</span>`;
    if (previous === 0) return `<span class="text-blue-400 text-[10px] ml-1 uppercase bg-blue-500/10 px-1 rounded">New</span>`;
    let percentChange = ((current - previous) / previous) * 100;
    let rounded = Math.abs(Math.round(percentChange));
    if (percentChange > 0) return `<span class="text-emerald-400 text-[10px] ml-1">▲ ${rounded}%</span>`;
    if (percentChange < 0) return `<span class="text-rose-400 text-[10px] ml-1">▼ ${rounded}%</span>`;
    return `<span class="text-slate-400 text-[10px] ml-1">▶ 0%</span>`;
}

// --- Category color map (used by overview + leaderboard) ---
const categoryColors = {
    'Development': 'bg-purple-500',
    'Gaming': 'bg-indigo-500',
    'Productivity': 'bg-blue-500',
    'Browsing': 'bg-sky-400',
    'Communication': 'bg-teal-400',
    'Media Production': 'bg-amber-400',
    'Music': 'bg-emerald-500',
    'Fun': 'bg-pink-500',
    'Education': 'bg-cyan-400',
    'Utilities': 'bg-slate-400',
    'Other': 'bg-stone-500',
    'Uncategorized': 'bg-gray-600'
};

// --- Chart.js global defaults ---
Chart.defaults.color = '#64748b';
Chart.defaults.font.family = "'Segoe UI', system-ui, sans-serif";
Chart.defaults.font.weight = '600';

// --- Shared, cross-tab mutable state ---
let trendChart = null, donutChart = null, drillChart = null;
let currentLeaderboardData = [], currentLeaderboardMode = 'focus';
let searchQuery = '';
let allCategories = [];

// --- Tab registry ---
// Each tab module (js/tabs/*.js) registers itself here with an onEnter()
// callback. app.js calls onEnter() whenever that tab becomes active.
// This is the ONLY namespaced object; every function referenced from
// inline onclick/onchange attributes in the HTML partials stays a plain
// global function, exactly like the original single-file version.
const Dashboard = { tabs: {} };

// --- Theme Handling ---
function setTheme(themeName) {
    document.body.className = themeName;
    localStorage.setItem('fastapp-theme', themeName);
}

// Don't use window.onload here, we call it in app.js
const savedTheme = localStorage.getItem('fastapp-theme') || 'dark';
document.body.className = savedTheme;
