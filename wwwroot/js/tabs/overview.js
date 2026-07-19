/* ==========================================
   TAB: Overview (Global Pulse)
   ========================================== */

async function loadOverview() {
    try {
        const res = await fetch(`/api/overview?date=${getSelectedDate()}`);
        const data = await res.json();

        document.getElementById('kpi-total-today').innerText = formatHours(data.totalToday);
        document.getElementById('kpi-afk').innerText = formatHours(data.afkToday);
        document.getElementById('kpi-switches').innerText = data.contextSwitches;

        document.getElementById('kpi-focus').innerText = formatHours(data.focusToday);

        // --- PHASE 1: CONTEXTUAL BASELINES ---
        const selectedDate = getSelectedDate();
        const kpiFocusLabel = document.querySelector('#kpi-focus').parentElement.previousElementSibling.querySelector('h3');

        // If we are looking at the past via the Time Machine
        if (selectedDate !== getLocalTodayStr() && data.usualDailyFocus !== undefined) {
            kpiFocusLabel.innerText = "Focus Time (Historical)";

            const diff = data.focusToday - data.usualDailyFocus;
            if (Math.abs(diff) > (10 / 60)) { // 10 minute threshold
                const sign = diff > 0 ? '▲' : '▼';
                const color = diff > 0 ? 'text-emerald-400 bg-emerald-500/10' : 'text-rose-400 bg-rose-500/10';
                document.getElementById('kpi-focus-trend').innerHTML = `<div class="mt-2 text-xs font-semibold ${color} px-2 py-0.5 rounded w-max border border-current/20">${sign} ${formatTime(Math.abs(diff) * 60)} vs usual</div>`;
            } else {
                document.getElementById('kpi-focus-trend').innerHTML = `<div class="mt-2 text-xs font-semibold text-slate-400 bg-slate-500/10 px-2 py-0.5 rounded w-max border border-slate-500/20">▶ Average day</div>`;
            }
        } else {
            // Normal behavior for "Today"
            kpiFocusLabel.innerText = "Focus Today";
            document.getElementById('kpi-focus-trend').innerHTML = getBlockTrendHtml(data.focusToday, data.prevFocusToday, "Yesterday");
        }
        document.getElementById('kpi-focus-trend').innerHTML = getBlockTrendHtml(data.focusToday, data.prevFocusToday, "Yesterday");

        document.getElementById('kpi-week').innerText = formatHours(data.focusWeek);
        document.getElementById('kpi-week-trend').innerHTML = getBlockTrendHtml(data.focusWeek, data.prevFocusWeek, "Last Week");

        document.getElementById('kpi-month').innerText = formatHours(data.focusMonth);
        document.getElementById('kpi-month-trend').innerHTML = getBlockTrendHtml(data.focusMonth, data.prevFocusMonth, "Last Month");

        document.getElementById('kpi-year').innerText = formatHours(data.focusYear);
        document.getElementById('kpi-year-trend').innerHTML = getBlockTrendHtml(data.focusYear, data.prevFocusYear, "Last Year");

        document.getElementById('kpi-alltime').innerText = formatHours(data.focusAllTime);

        let totalMins = data.categories.reduce((sum, c) => sum + c.focusedMinutes, 0);
        let barHtml = '', legendHtml = '';
        if (totalMins > 0) {
            data.categories.forEach(cat => {
                let pct = (cat.focusedMinutes / totalMins) * 100;
                let colorClass = categoryColors[cat.category] || categoryColors['Uncategorized'];
                barHtml += `<div class="${colorClass} h-full transition-all duration-500 ease-out border-r border-slate-900/50" style="width: ${pct}%" title="${cat.category}: ${formatTime(cat.focusedMinutes)}"></div>`;
                legendHtml += `
                        <div class="flex items-center bg-slate-800/40 px-3 py-1.5 rounded-lg border border-slate-700/30 shadow-sm">
                            <div class="w-3 h-3 rounded-full ${colorClass} mr-2 shadow-[0_0_8px_currentColor]"></div>
                            <span class="text-slate-200 font-bold tracking-wide">${cat.category}</span>
                            <span class="text-slate-500 font-bold ml-2">${Math.round(pct)}%</span>
                        </div>`;
            });
        } else {
            barHtml = `<div class="bg-slate-800 h-full w-full"></div>`;
            legendHtml = `<span class="text-slate-500">No category data for this date.</span>`;
        }
        document.getElementById('category-bar').innerHTML = barHtml;
        document.getElementById('category-legend').innerHTML = legendHtml;

        // --- 365-DAY HEATMAP RENDERER ---
        const parts = getSelectedDate().split('-');
        const target = new Date(parts[0], parts[1] - 1, parts[2]);

        // Safely extract the data regardless of C# JSON casing
        const heatData = data.yearlyHeatmap || data.YearlyHeatmap || [];

        let maxYearFocus = heatData.length > 0
            ? Math.max(...heatData.map(d => d.focusedMinutes !== undefined ? d.focusedMinutes : (d.FocusedMinutes || 0)))
            : 1;
        if (maxYearFocus === 0) maxYearFocus = 1;

        let oldestDate = new Date(target);
        oldestDate.setDate(target.getDate() - 364);

        // Map C# DayOfWeek (Sun=0) to our visual grid where Mon=TopRow(0)
        let dayOfWeek = oldestDate.getDay();
        let emptyCells = dayOfWeek === 0 ? 6 : dayOfWeek - 1;

        let gridHtml = '';

        // Inject invisible spacer blocks so the calendar perfectly aligns with Mondays
        for (let e = 0; e < emptyCells; e++) {
            gridHtml += `<div class="w-[11px] h-[11px] rounded-[2px] bg-transparent pointer-events-none"></div>`;
        }

        // Loop through 365 days
        for (let i = 364; i >= 0; i--) {
            let d = new Date(target);
            d.setDate(target.getDate() - i);

            // Format local date to match C# "yyyy-MM-dd" exactly
            let y = d.getFullYear();
            let m = String(d.getMonth() + 1).padStart(2, '0');
            let day = String(d.getDate()).padStart(2, '0');
            let dateStrLocal = `${y}-${m}-${day}`;

            let dayData = heatData.find(x => {
                let xDate = x.date || x.Date;
                return xDate === dateStrLocal;
            });

            let mins = dayData ? (dayData.focusedMinutes !== undefined ? dayData.focusedMinutes : (dayData.FocusedMinutes || 0)) : 0;

            let intensity = mins / maxYearFocus;
            let bgClass = 'bg-slate-800/50 border border-slate-700/50';

            if (intensity > 0.8) bgClass = 'bg-blue-400 shadow-[0_0_5px_rgba(96,165,250,0.8)] border-none';
            else if (intensity > 0.5) bgClass = 'bg-blue-500/90 border-none';
            else if (intensity > 0.25) bgClass = 'bg-blue-500/60 border-none';
            else if (intensity > 0) bgClass = 'bg-blue-500/30 border-none';

            let displayDate = d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
            let tooltip = `${displayDate} - ${formatTime(mins)} focused`;

            gridHtml += `<div class="w-[11px] h-[11px] rounded-[2px] ${bgClass} transition-all duration-200 hover:scale-[1.3] hover:ring-1 hover:ring-white cursor-crosshair relative z-0 hover:z-10" title="${tooltip}"></div>`;
        }

        document.getElementById('yearly-heatmap').innerHTML = gridHtml;
        // --- END 365-DAY HEATMAP ---

        if (trendChart) trendChart.destroy();
        trendChart = new Chart(document.getElementById('trendChart'), { type: 'line', data: { labels: data.weeklyTrend.map(d => d.day), datasets: [{ label: 'Focus Time', data: data.weeklyTrend.map(d => d.focusedHours * 60), borderColor: '#38bdf8', backgroundColor: 'rgba(56, 189, 248, 0.15)', borderWidth: 4, tension: 0.4, fill: true, pointBackgroundColor: '#0f172a', pointBorderColor: '#38bdf8', pointBorderWidth: 3, pointRadius: 5 }] }, options: { responsive: true, maintainAspectRatio: false, plugins: { legend: { display: false }, tooltip: { backgroundColor: 'rgba(15,23,42,0.9)', titleColor: '#38bdf8', bodyColor: '#fff', bodyFont: { size: 14, weight: 'bold' }, padding: 12, cornerRadius: 8, callbacks: { label: function (c) { return formatTime(c.raw); } } } }, scales: { y: { grid: { color: 'rgba(255,255,255,0.05)' }, beginAtZero: true, ticks: { callback: function (val) { return formatTime(val); } } }, x: { grid: { display: false } } } } });
        if (donutChart) donutChart.destroy();
        donutChart = new Chart(document.getElementById('donutChart'), { type: 'doughnut', data: { labels: data.topAppsToday.map(a => a.appName), datasets: [{ data: data.topAppsToday.map(a => a.focusedMinutes), backgroundColor: ['#8b5cf6', '#6366f1', '#3b82f6', '#0ea5e9', '#14b8a6', '#f59e0b', '#f43f5e'], borderWidth: 2, borderColor: '#0f172a' }] }, options: { responsive: true, maintainAspectRatio: false, cutout: '75%', plugins: { legend: { position: 'right', labels: { color: '#cbd5e1', font: { weight: 'bold' } } }, tooltip: { backgroundColor: 'rgba(15,23,42,0.9)', padding: 12, callbacks: { label: function (c) { return " " + formatTime(c.raw); } } } } } });
    } catch (err) { console.error(err); }
}

// Registers this tab's entry point with the tab system (see app.js)
Dashboard.tabs.overview = { onEnter: loadOverview };
