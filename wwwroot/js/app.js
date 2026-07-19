/* ==========================================
   APP CORE — tab configuration, nav/section builder, bootstrap.

   HOW TO ADD A NEW TAB:
   1. Create wwwroot/partials/tab-<id>.html   (just the inner content,
      no <section> wrapper — app.js adds that for you)
   2. Create wwwroot/js/tabs/<id>.js containing that tab's logic, ending
      with:  Dashboard.tabs.<id> = { onEnter: yourLoadFunction };
   3. Add a <script src="js/tabs/<id>.js"></script> line in dashboard.html,
      alongside the other tab scripts (before app.js).
   4. Add one entry to TAB_CONFIG below.
   That's it — the nav button and section container are generated
   automatically, in order, from this list.
   ========================================== */

const TAB_CONFIG = [
    { id: 'overview', icon: '📊', label: 'Global Pulse', partial: 'partials/tab-overview.html' },
    { id: 'leaderboard', icon: '🏆', label: 'Leaderboards', partial: 'partials/tab-leaderboard.html' },
    { id: 'insights', icon: '🧠', label: 'Behaviors', partial: 'partials/tab-insights.html' },
    { id: 'timeline', icon: '⏳', label: 'Timeline', partial: 'partials/tab-timeline.html' },
    { id: 'allapps', icon: '📁', label: 'All Applications', partial: 'partials/tab-allapps.html', dividerBefore: true },
    { id: 'settings', icon: '⚙️', label: 'Settings', partial: 'partials/tab-settings.html' }
];

let sparklineData = [];

async function loadSparkline() {
    try {
        const res = await fetch('/api/sparkline');
        sparklineData = await res.json();
        renderSparkline();
    } catch (err) { console.error("Failed to load sparkline", err); }
}

function renderSparkline() {
    const container = document.getElementById('sparkline-container');
    if (!container || sparklineData.length === 0) return;

    const selectedDate = getSelectedDate();
    const maxMins = Math.max(...sparklineData.map(d => d.focusedMinutes), 1);

    let html = '';
    sparklineData.forEach(day => {
        const heightPct = Math.max((day.focusedMinutes / maxMins) * 100, 5); // Minimum 5% height
        const isSelected = day.date === selectedDate;
        const isToday = day.date === getLocalTodayStr();

        let colorClass = 'bg-slate-700 hover:bg-blue-400/50';
        if (isSelected) colorClass = 'bg-blue-400 shadow-[0_0_8px_rgba(96,165,250,0.8)]';
        else if (isToday) colorClass = 'bg-slate-500 border-t-2 border-blue-400';

        const tooltip = `${day.displayDay} (${day.date})\n${formatTime(day.focusedMinutes)} focused`;

        html += `<div onclick="selectSparklineDate('${day.date}')" 
                      class="w-full rounded-[1px] cursor-pointer transition-all duration-200 ${colorClass}" 
                      style="height: ${heightPct}%" title="${tooltip}"></div>`;
    });
    container.innerHTML = html;

    // Update the visual label
    const labelEl = document.getElementById('current-date-label');
    if (selectedDate === getLocalTodayStr()) {
        labelEl.innerText = "Today";
        labelEl.className = "text-xs font-bold text-blue-400";
    } else {
        const selectedDayObj = sparklineData.find(d => d.date === selectedDate);
        labelEl.innerText = selectedDayObj ? selectedDayObj.displayDay : selectedDate;
        labelEl.className = "text-xs font-bold text-amber-400 drop-shadow-[0_0_5px_rgba(251,191,36,0.5)]";
    }
}

function selectSparklineDate(dateStr) {
    document.getElementById('global-date').value = dateStr;
    renderSparkline(); // Instantly update the blue highlight
    refreshCurrentTab(); // Refresh the rest of the dashboard
}

function getSelectedDate() { return document.getElementById('global-date').value; }

function switchTab(tabId, btnEl) {
    document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
    btnEl.classList.add('active');
    TAB_CONFIG.forEach(t => document.getElementById('view-' + t.id).classList.add('hidden'));
    document.getElementById('view-' + tabId).classList.remove('hidden');
    activateTab(tabId);
}

function activateTab(tabId) {
    const mod = Dashboard.tabs[tabId];
    if (mod && typeof mod.onEnter === 'function') mod.onEnter();
}

function refreshCurrentTab() {
    const activeBtn = document.querySelector('.tab-btn.active');
    if (!activeBtn) return;
    activateTab(activeBtn.dataset.tabId);
}

async function buildNavAndSections() {
    const nav = document.getElementById('tab-nav');
    const sectionsContainer = document.getElementById('tab-sections');

    // --- Build nav buttons from TAB_CONFIG ---
    let navHtml = '';
    TAB_CONFIG.forEach(tab => {
        if (tab.dividerBefore) navHtml += `<div class="my-4 border-t border-slate-700/30 w-3/4 mx-auto"></div>`;
        navHtml += `<div class="tab-btn px-4 py-3 rounded-lg font-medium text-slate-400 hover:text-white" data-tab-id="${tab.id}" onclick="switchTab('${tab.id}', this)">${tab.icon} ${tab.label}</div>`;
    });
    nav.innerHTML = navHtml;
    nav.querySelector(`.tab-btn[data-tab-id="${TAB_CONFIG[0].id}"]`).classList.add('active');

    // --- Fetch each tab's partial HTML and wrap it in its <section> ---
    const sectionHtmlParts = await Promise.all(TAB_CONFIG.map(async (tab, index) => {
        const html = await fetch(tab.partial).then(r => r.text());
        const hiddenClass = index === 0 ? '' : 'hidden';
        return `<section id="view-${tab.id}" class="${hiddenClass} max-w-7xl ml-0">${html}</section>`;
    }));
    sectionsContainer.innerHTML = sectionHtmlParts.join('\n');
}

async function loadDrilldownPartial() {
    const container = document.getElementById('drilldown-container');
    container.innerHTML = await fetch('partials/drilldown.html').then(r => r.text());
}

async function initializeDashboard() {
    // 1. Set the hidden date using our local time helper
    document.getElementById('global-date').value = getLocalTodayStr();

    // 2. Load the UI components concurrently
    await Promise.all([
        buildNavAndSections(),
        loadDrilldownPartial(),
        loadSparkline()
    ]);

    // 3. Fetch real categories or fallback
    try {
        const res = await fetch('/api/categories');
        allCategories = await res.json();
        if (!allCategories || allCategories.length === 0) {
            allCategories = ['Development', 'Gaming', 'Productivity', 'Browsing', 'Communication', 'Media Production', 'Music', 'Fun', 'Education', 'Utilities', 'Other'];
        }
    } catch (err) {
        console.error("Failed to load real categories", err);
        allCategories = ['Development', 'Gaming', 'Productivity', 'Browsing', 'Other'];
    }

    // 4. Show the first tab
    activateTab(TAB_CONFIG[0].id);
}

initializeDashboard();


