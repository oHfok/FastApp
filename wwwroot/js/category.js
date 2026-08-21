/* ==========================================================
   CATEGORY DETAIL DRAWER
   Click a category anywhere it's shown (category bar, legend,
   Top Categories rows, per-app category label) to see every
   app tagged with it. Not wired on the category *selector* in
   the App Detail drawer — that's for editing, not browsing.
   ========================================================== */

async function openCategoryDetail(category) {
    document.getElementById('cat-overlay').classList.add('open');
    document.getElementById('cat-drawer').classList.add('open');

    document.getElementById('cat-title').textContent = category;
    document.getElementById('cat-sub').textContent = 'Loading…';

    const iconEl = document.getElementById('cat-icon');
    const color = catColor(category);
    iconEl.textContent = category.charAt(0).toUpperCase();
    iconEl.style.background = `${color}26`;
    iconEl.style.borderColor = color;
    iconEl.style.color = color;

    const listEl = document.getElementById('cat-app-list');
    listEl.innerHTML = loadingRowsHtml(5);

    try {
        const allApps = await apiFetch('/api/all-apps', { signal: abortableSignal('category-detail') });

        const apps = allApps
            .filter(a => (a.category ?? 'Other') === category)
            .map(a => ({
                name: a.appName,
                focus: a.totalFocus ?? 0
            }))
            .sort((a, b) => b.focus - a.focus);

        document.getElementById('cat-sub').textContent = `${apps.length} app${apps.length === 1 ? '' : 's'}`;

        listEl.innerHTML = apps.length === 0
            ? `<div class="empty-state">No apps tagged with this category yet.</div>`
            // The delegated opener handles the click; closeCategoryDetail runs
            // from openDrilldown so this drawer doesn't stay stacked underneath.
            : apps.map((a, i) => `
                <div class="lb-row app-link" data-open-app="${escapeHtml(a.name)}" role="button" tabindex="0">
                    <div class="lb-rank">${i + 1}</div>
                    <div class="lb-name" title="${escapeHtml(a.name)}">${escapeHtml(displayAppName(a.name))}</div>
                    <div class="lb-time">${formatTime(a.focus)}</div>
                </div>`).join('');
    } catch (err) {
        if (isAbort(err)) return;
        console.error('Failed to load category apps', err);
        listEl.innerHTML = `<div class="empty-state">Couldn't load apps for this category.</div>`;
    }
}

function closeCategoryDetail() {
    document.getElementById('cat-overlay').classList.remove('open');
    document.getElementById('cat-drawer').classList.remove('open');
}
