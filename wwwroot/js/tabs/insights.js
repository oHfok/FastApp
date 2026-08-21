/* ==========================================================
   TAB: INSIGHTS
   Patterns over the last 30 days: work/play rhythm by hour,
   weekday fatigue, and a day-by-hour heatmap — powered by
   /api/insights. The work/play split is user-editable (see
   Configure Work vs. Play below), not a fixed backend guess.
   ========================================================== */

let rhythmChartInstance = null;
let fatigueChartInstance = null;

async function loadInsights() {
    try {
        const data = await apiFetch(`/api/insights?date=${getLocalTodayStr()}`,
                                    { signal: abortableSignal('insights') });
        if (data.error) { console.error(data.error); return; }

        document.getElementById('in-longest-block').textContent = formatTime(data.longestBlock ?? data.LongestBlock ?? 0);
        document.getElementById('in-avg-span').textContent = formatTime(data.averageSpan ?? data.AverageSpan ?? 0);

        renderRhythmChart(data.rhythm ?? data.Rhythm ?? []);
        renderFatigueChart(data.fatigue ?? data.Fatigue ?? []);
        renderInsightsHeatmap(data.heatmap ?? data.Heatmap ?? []);
    } catch (err) {
        if (!isAbort(err)) console.error('Insights load failed', err);
    }
}

function renderRhythmChart(rhythm) {
    const ctx = document.getElementById('in-rhythm-chart');
    if (!ctx || !window.Chart) return;
    if (rhythmChartInstance) rhythmChartInstance.destroy();

    const theme = getChartTheme(); // read fresh each render — themes recolor charts by re-rendering, not by CSS alone
    const labels = rhythm.map(r => `${pad(r.hour ?? r.Hour ?? 0)}:00`);
    const work = rhythm.map(r => Math.round(r.work ?? r.Work ?? 0));
    const play = rhythm.map(r => Math.round(r.play ?? r.Play ?? 0));

    rhythmChartInstance = new Chart(ctx, {
        type: 'bar',
        data: {
            labels,
            datasets: [
                { label: 'Work', data: work, backgroundColor: theme.brass, borderRadius: 3, stack: 's' },
                { label: 'Play', data: play, backgroundColor: theme.violet, borderRadius: 3, stack: 's' }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                x: { stacked: true, grid: { display: false }, ticks: { color: theme.tick, font: { family: theme.fontMono, size: 10 }, maxRotation: 0, autoSkip: true, maxTicksLimit: 12 } },
                y: { stacked: true, grid: { color: theme.grid }, ticks: { color: theme.tick, callback: (v) => formatTime(v) } }
            },
            plugins: {
                legend: { labels: { color: theme.tick, font: { family: theme.fontBody, size: 11 } } },
                tooltip: {
                    backgroundColor: theme.tooltipBg,
                    titleColor: theme.tooltipTitle,
                    bodyColor: theme.tooltipBody,
                    callbacks: { label: (item) => `${item.dataset.label}: ${formatTime(item.raw)}` }
                }
            }
        }
    });
}

function renderFatigueChart(fatigue) {
    const ctx = document.getElementById('in-fatigue-chart');
    if (!ctx || !window.Chart) return;
    if (fatigueChartInstance) fatigueChartInstance.destroy();

    const theme = getChartTheme();
    const labels = fatigue.map(f => f.day ?? f.Day ?? '');
    const values = fatigue.map(f => Math.round(f.avgMinutes ?? f.AvgMinutes ?? 0));

    fatigueChartInstance = new Chart(ctx, {
        type: 'bar',
        data: {
            labels,
            datasets: [{ label: 'Avg session length', data: values, backgroundColor: theme.teal, borderRadius: 4, maxBarThickness: 48 }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                x: { grid: { display: false }, ticks: { color: theme.tick, font: { family: theme.fontMono, size: 11 } } },
                y: { grid: { color: theme.grid }, ticks: { color: theme.tick, callback: (v) => formatTime(v) } }
            },
            plugins: {
                legend: { display: false },
                tooltip: {
                    backgroundColor: theme.tooltipBg,
                    titleColor: theme.tooltipTitle,
                    bodyColor: theme.tooltipBody,
                    callbacks: { label: (item) => formatTime(item.raw) }
                }
            }
        }
    });
}

// Backend's Heatmap.DayIndex is .NET's native Sunday=0..Saturday=6; convert
// to the Monday-first row index (isoDow) used by every other heatmap here.
function renderInsightsHeatmap(heatmap) {
    const body = document.getElementById('in-heatmap-body');
    const grid = Array.from({ length: 7 }, () => new Array(24).fill(0));

    (heatmap || []).forEach(h => {
        const dow = h.dayIndex ?? h.DayIndex ?? 0;
        const hour = h.hour ?? h.Hour ?? 0;
        const mins = h.totalMinutes ?? h.TotalMinutes ?? 0;
        const row = (dow + 6) % 7;
        if (hour >= 0 && hour < 24) grid[row][hour] += mins;
    });

    const maxMins = Math.max(...grid.flat(), 1);

    let cellsHtml = '';
    for (let day = 0; day < 7; day++) {
        for (let hour = 0; hour < 24; hour++) {
            const mins = grid[day][hour];
            const intensity = mins / maxMins;
            const bg = heatColor(intensity);
            const tip = `${DAY_SHORT[day]} at ${pad(hour)}:00<br>${formatTime(mins)} over the last 30 days`;
            cellsHtml += `<div class="heat-cell" style="background:${bg}" onmousemove="showTooltip(event, '${tip}')" onmouseleave="hideTooltip()"></div>`;
        }
    }

    body.innerHTML = `
        <div class="heat-hours-wrap">
            <div class="heat-day-labels">${DAY_SHORT.map(d => `<span>${d}</span>`).join('')}</div>
            <div class="heat-hours-grid">${cellsHtml}</div>
        </div>
        <div class="heat-hours-ticks"><span>00:00</span><span>06:00</span><span>12:00</span><span>18:00</span><span>24:00</span></div>`;
}

// Work/Play classification — user-editable, drives the rhythm chart above.
// Loaded independently of loadInsights() so the list still renders even if
// the chart data itself fails for some reason.
async function loadCategoryClassification() {
    const listEl = document.getElementById('in-classification-list');
    try {
        const res = await fetch('/api/settings/category-classification');
        const data = await res.json();
        const entries = Object.entries(data).sort((a, b) => a[0].localeCompare(b[0]));

        if (entries.length === 0) {
            listEl.innerHTML = `<div class="empty-state">No categories yet.</div>`;
            return;
        }

        // Category comes from a data-* attribute rather than being spliced into
        // the onclick string — names are free-form, so an apostrophe would have
        // broken the handler outright and markup would have been re-executed.
        const segButton = (cat, value, label, current) =>
            `<button class="${current === value ? 'active' : ''}" data-classify-cat="${escapeHtml(cat)}" data-classify-as="${value}">${label}</button>`;

        listEl.innerHTML = `<div class="settings-list">${entries.map(([cat, cls]) => `
                <div class="settings-list-item">
                    <span><span class="cat-swatch" style="background:${catColor(cat)};display:inline-block;margin-right:8px;"></span>${escapeHtml(cat)}</span>
                    <div class="segmented">
                        ${segButton(cat, 'play', 'Play', cls)}
                        ${segButton(cat, 'neutral', 'Neutral', cls)}
                        ${segButton(cat, 'work', 'Work', cls)}
                    </div>
                </div>`).join('')}</div>`;

        listEl.querySelectorAll('[data-classify-cat]').forEach(btn => {
            btn.addEventListener('click', () =>
                setCategoryClassification(btn.dataset.classifyCat, btn.dataset.classifyAs));
        });
    } catch (err) {
        console.error('Failed to load category classification', err);
        listEl.innerHTML = `<div class="empty-state">Couldn't load categories.</div>`;
    }
}

async function setCategoryClassification(category, classification) {
    const listEl = document.getElementById('in-classification-list');
    try {
        const res = await fetch('/api/settings/category-classification', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ category, classification })
        });
        // This response was never checked, so a rejected value still re-rendered
        // and left the button looking as though the change had taken.
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        await loadCategoryClassification(); // refresh which button is highlighted
        loadInsights(); // recompute + redraw the rhythm chart against the new mapping
    } catch (err) {
        console.error('Classification update failed', err);
        // Re-read from the server so the highlighted button reflects what is
        // actually stored, not what was clicked.
        await loadCategoryClassification();
        if (listEl) {
            listEl.insertAdjacentHTML('afterbegin',
                `<div class="error-state-detail" style="color:var(--rose);margin-bottom:10px;">Couldn't save that change.</div>`);
        }
    }
}

Dashboard.tabs.insights = {
    onEnter: () => {
        loadInsights();
        loadCategoryClassification();
    },
    refresh: loadInsights // classification mapping rarely changes; skip re-fetching it every poll
};
