/* ==========================================================
   SETTINGS
   1. Dashboard theme (instant, localStorage-only, no server call)
   2. Hidden apps list + unhide
   3. Database retention (default: Keep Forever) + save
   ========================================================== */

const DASHBOARD_THEME_KEY = 'fastapp-theme';

function setSettingsTab(tab, btnEl) {
    document.querySelectorAll('#settings-tab-toggle button').forEach(b => b.classList.toggle('active', b === btnEl));
    document.querySelectorAll('.settings-tab-panel').forEach(panel => {
        panel.style.display = panel.dataset.tab === tab ? 'block' : 'none';
    });
    // Fetched on first view rather than on page load: it is a network call to
    // GitHub, and most visits to Settings are not about the changelog.
    if (tab === 'whatsnew') loadReleaseNotes();
}

function loadDashboardTheme() {
    const current = localStorage.getItem(DASHBOARD_THEME_KEY) || 'instrument';
    updateThemePickerActiveState(current);
}

function setDashboardTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem(DASHBOARD_THEME_KEY, theme);
    updateThemePickerActiveState(theme);
    // Chart.js canvases and the JS-computed heatmap fills only re-theme when
    // re-rendered, not from the CSS change alone — refresh whatever's behind
    // this drawer so it's already correct once Settings closes.
    refreshActiveTab();
}

function updateThemePickerActiveState(theme) {
    document.querySelectorAll('#theme-picker .theme-swatch').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.themeValue === theme);
    });
}

// TIMELINE_COLOR_MODE_KEY / getTimelineColorMode() live in utils.js since
// timelineSegmentsHtml() needs to read the mode at render time — this just
// owns writing it.
function loadTimelineColorMode() {
    const current = getTimelineColorMode();
    document.querySelectorAll('#timeline-color-toggle button').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.mode === current);
    });
}

function loadTimelineRangeMode() {
    const current = getTimelineRangeMode();
    document.querySelectorAll('#timeline-range-toggle button').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.mode === current);
    });
}

function setTimelineRangeMode(mode, btnEl) {
    localStorage.setItem(TIMELINE_RANGE_KEY, mode);
    document.querySelectorAll('#timeline-range-toggle button').forEach(btn => {
        btn.classList.toggle('active', btn === btnEl);
    });
    refreshActiveTab();
}

function setTimelineColorMode(mode, btnEl) {
    localStorage.setItem(TIMELINE_COLOR_MODE_KEY, mode);
    document.querySelectorAll('#timeline-color-toggle button').forEach(btn => {
        btn.classList.toggle('active', btn === btnEl);
    });
    refreshActiveTab();
}

async function loadHiddenApps() {
    try {
        const res = await fetch('/api/hidden-apps');
        const apps = await res.json();
        const list = document.getElementById('hidden-apps-list');
        if (!apps || apps.length === 0) {
            list.innerHTML = `<div class="empty-state">No apps hidden.</div>`;
            return;
        }
        // Hidden app names are OS-supplied and can contain an apostrophe, which
        // would have broken the inline handler outright — carried as data and
        // bound after render instead.
        list.innerHTML = apps.map(app => `
            <div class="settings-list-item">
                <span>${escapeHtml(app)}</span>
                <button class="btn btn-ghost" data-unhide="${escapeHtml(app)}">Unhide</button>
            </div>`).join('');

        list.querySelectorAll('[data-unhide]').forEach(btn => {
            btn.addEventListener('click', () => unhideAppSetting(btn.dataset.unhide));
        });
    } catch (err) { console.error(err); }
}

async function unhideAppSetting(appName) {
    try {
        const res = await fetch('/api/unhide', { method: 'POST', body: appName });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        loadHiddenApps();
        refreshActiveTab();
    } catch (err) {
        console.error('Unhide failed', err);
        // Re-read the list so it shows what is actually stored; the row stays
        // put rather than disappearing as though the change had worked.
        loadHiddenApps();
    }
}

async function loadRetentionSetting() {
    try {
        const res = await fetch('/api/settings');
        const data = await res.json();
        const val = data.retentionDays ?? 99999;
        const select = document.getElementById('retention-select');
        // If the stored value isn't one of the presets, fall back to Keep Forever
        const hasOption = Array.from(select.options).some(o => o.value == val);
        select.value = hasOption ? val : 99999;
        // Remembered so saveRetentionSetting() can tell a shortening (destructive,
        // needs confirmation) from a lengthening (deletes nothing).
        select.dataset.savedValue = select.value;

        const captureWindowTitles = data.captureWindowTitles ?? false;
        document.getElementById('window-titles-toggle').checked = captureWindowTitles;
    } catch (err) { console.error(err); }
}

async function saveRetentionSetting() {
    const select = document.getElementById('retention-select');
    const val = select.value;
    const status = document.getElementById('retention-status');

    // Shortening retention destroys data on next launch with no undo, so it gets
    // an explicit confirmation naming the cutoff. Lengthening it (or Keep Forever)
    // deletes nothing, so it saves without interruption.
    const previous = parseInt(select.dataset.savedValue || '99999', 10);
    const next = parseInt(val, 10);
    if (next < previous) {
        const label = select.options[select.selectedIndex].textContent.trim();
        const confirmed = confirm(
            `Keep only the last ${label}?\n\n` +
            `Session and macro logs older than that will be permanently deleted the next time ` +
            `FastApp starts. This can't be undone without a backup.\n\n` +
            `Your daily totals and per-app history are not affected.`
        );
        if (!confirmed) {
            select.value = String(previous); // put the dropdown back where it was
            return;
        }
    }

    const res = await fetch('/api/settings/retention', { method: 'POST', body: val });
    if (!res.ok) {
        status.textContent = 'Could not save that value.';
        status.style.color = 'var(--rose)';
        status.style.display = 'block';
        setTimeout(() => { status.style.display = 'none'; }, 2500);
        return;
    }

    select.dataset.savedValue = val;
    status.textContent = 'Saved.';
    status.style.color = '';
    status.style.display = 'block';
    setTimeout(() => { status.style.display = 'none'; }, 2500);
}

async function saveWindowTitlesSetting() {
    const enabled = document.getElementById('window-titles-toggle').checked;
    await fetch('/api/settings/window-titles', { method: 'POST', body: String(enabled) });
    const status = document.getElementById('window-titles-status');
    status.style.display = 'block';
    setTimeout(() => { status.style.display = 'none'; }, 2500);
}

async function loadDbStats() {
    try {
        const res = await fetch('/api/db-stats');
        const data = await res.json();

        const sizeBytes = data.dbSizeBytes ?? 0;
        const firstDate = data.firstTrackedDate;
        const p90 = data.projected90Days ?? 0;
        const p365 = data.projected365Days ?? 0;

        document.getElementById('dbsize-current').textContent = formatBytes(sizeBytes);
        document.getElementById('dbsize-since').textContent = firstDate ? fmtDateEU(parseDateStr(firstDate)) : 'No data yet';
        document.getElementById('dbsize-90').textContent = formatBytes(p90);
        document.getElementById('dbsize-365').textContent = formatBytes(p365);
    } catch (err) { console.error(err); }
}

// --- Restore from backup -----------------------------------------------------
function handleRestoreFileSelected(inputEl) {
    const file = inputEl.files?.[0];
    if (!file) return;

    const confirmed = confirm(
        `Restore FastApp from "${file.name}"?\n\n` +
        `This REPLACES everything currently tracked with this backup's contents and restarts FastApp.\n\n` +
        `Your current data is saved first (in case this was a mistake), but this can't be casually undone. Continue?`
    );
    inputEl.value = ''; // reset so selecting the same file again still fires onchange
    if (!confirmed) return;

    restoreBackupFile(file);
}

async function restoreBackupFile(file) {
    const status = document.getElementById('restore-status');
    status.style.display = 'block';
    status.style.color = 'var(--text-dim)';
    status.textContent = 'Uploading and validating…';

    try {
        const formData = new FormData();
        formData.append('file', file);
        const res = await fetch('/api/restore', { method: 'POST', body: formData });
        const data = await res.json();

        if (!res.ok || data.error) {
            status.style.color = 'var(--rose)';
            status.textContent = data.error || 'Restore failed.';
            return;
        }

        status.style.color = 'var(--teal)';
        status.textContent = data.message || 'Restoring — FastApp will restart in a few seconds.';
    } catch (err) {
        status.style.color = 'var(--rose)';
        status.textContent = "Couldn't reach FastApp to restore.";
    }
}

async function loadPinSetting() {
    try {
        const res = await fetch('/api/settings/pin');
        const data = await res.json();
        const hasPin = data.hasPin ?? false;
        document.getElementById('pin-status-line').style.display = hasPin ? 'block' : 'none';
        document.getElementById('pin-form').style.display = hasPin ? 'none' : 'flex';
    } catch (err) { console.error(err); }
}

function showPinForm() {
    document.getElementById('pin-form').style.display = 'flex';
    document.getElementById('pin-status-line').style.display = 'none';
}

function flashPinStatus(text, isError) {
    const status = document.getElementById('pin-status');
    status.textContent = text;
    status.style.color = isError ? 'var(--rose)' : 'var(--teal)';
    status.style.display = 'block';
    setTimeout(() => { status.style.display = 'none'; }, 2500);
}

async function savePinSetting() {
    const pin = document.getElementById('pin-input').value;
    if (!pin || pin.length < 4) {
        flashPinStatus('PIN must be at least 4 characters.', true);
        return;
    }

    try {
        const res = await fetch('/api/settings/pin', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ pin })
        });
        if (!res.ok) throw new Error('save failed');
        document.getElementById('pin-input').value = '';
        flashPinStatus('Saved.', false);
        loadPinSetting();
    } catch (err) {
        flashPinStatus('Failed to save PIN.', true);
    }
}

Dashboard.tabs.settings = {
    onEnter: () => {
        loadDashboardTheme();
        loadTimelineColorMode();
        loadTimelineRangeMode();
        loadHiddenApps();
        loadRetentionSetting();
        loadPinSetting();
        loadDbStats();
    }
};

/* ==========================================================
   VERSION HISTORY ("What's New")

   Notes are markdown from the GitHub release, rendered by
   renderMarkdown(). The version the user is actually running is
   marked, and versions with no notes still appear rather than
   silently vanishing -- releases before 1.0.5 predate the habit
   of writing them, and a gap in the list would read as a bug.
   ========================================================== */
let releasesLoaded = false;

async function loadReleaseNotes(force) {
    const listEl = document.getElementById('release-list');
    if (!listEl || (releasesLoaded && !force)) return;

    listEl.innerHTML = loadingRowsHtml(3);
    try {
        const data = await apiFetch('/api/releases');
        const releases = data.releases ?? [];
        if (releases.length === 0) {
            listEl.innerHTML = `<div class="empty-state">No published versions found.</div>`;
            return;
        }

        const current = (data.currentVersion || '').trim();
        listEl.innerHTML = releases.map((r, i) => {
            const isCurrent = current && r.version === current;
            const body = r.notesMarkdown && r.notesMarkdown.trim()
                ? renderMarkdown(r.notesMarkdown)
                : `<p class="release-nonotes">No notes were recorded for this release.</p>`;
            return `
                <details class="release-item${isCurrent ? ' is-current' : ''}"${i === 0 ? ' open' : ''}>
                    <summary class="release-head">
                        <span class="release-version">${escapeHtml(r.version)}</span>
                        ${isCurrent ? '<span class="release-badge">Installed</span>' : ''}
                        <span class="release-date">${r.publishedAt ? escapeHtml(r.publishedAt) : ''}</span>
                    </summary>
                    <div class="release-body">${body}</div>
                </details>`;
        }).join('');
        releasesLoaded = true;
    } catch (err) {
        if (isAbort(err)) return;
        console.error('Failed to load release notes', err);
        listEl.innerHTML = errorStateHtml(
            "Couldn't load version history",
            'The list comes from GitHub, so this needs a working connection.',
            'loadReleaseNotes');
    }
}
