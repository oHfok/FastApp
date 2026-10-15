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
    // No stored choice means Windows decides, which is the new default: the
    // app used to be dark whatever the OS was set to, with no way out.
    // Themes from earlier versions (terminal, glass, nova...) no longer exist
    // and count as "follow Windows".
    const stored = localStorage.getItem(DASHBOARD_THEME_KEY);
    const current = stored === 'light' || stored === 'dark' ? stored : 'system';
    updateThemePickerActiveState(current);
}

function setDashboardTheme(theme) {
    // Following Windows is the absence of a stamp, so the media query in
    // tokens.css gets to decide. Stamping "system" would just be a third
    // theme that happens to look like one of the other two.
    if (theme === 'system') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', theme);
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
        document.getElementById('perf-logging-toggle').checked = data.performanceLogging ?? true;
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

async function savePerfLoggingSetting() {
    const enabled = document.getElementById('perf-logging-toggle').checked;
    await fetch('/api/settings/performance-logging', { method: 'POST', body: String(enabled) });
    const status = document.getElementById('perf-logging-status');
    status.style.display = 'block';
    setTimeout(() => { status.style.display = 'none'; }, 2500);
}

function renderApiAccess(data) {
    document.getElementById('api-access-toggle').checked = !!data.enabled;
    document.getElementById('api-access-details').style.display = data.enabled ? 'block' : 'none';
    document.getElementById('api-token-field').value = data.token || '';
    document.getElementById('api-token-file').textContent = data.tokenFile || '';
}

function flashApiStatus(text) {
    const status = document.getElementById('api-access-status');
    status.textContent = text;
    status.style.display = 'block';
    setTimeout(() => { status.style.display = 'none'; }, 2500);
}

async function loadApiAccess() {
    try { renderApiAccess(await (await fetch('/api/settings/api-access')).json()); }
    catch (err) { console.error(err); }
}

async function saveApiAccess() {
    const enabled = document.getElementById('api-access-toggle').checked;
    try {
        const res = await fetch('/api/settings/api-access', { method: 'POST', body: String(enabled) });
        renderApiAccess(await res.json());
        flashApiStatus(enabled ? 'On. Give the token to your app.' : 'Off. The token no longer works.');
    } catch (err) { console.error(err); loadApiAccess(); }
}

async function regenerateApiToken() {
    if (!confirm('Replace the token? Any app using the current one will stop working until you give it the new one.')) return;
    try {
        renderApiAccess(await (await fetch('/api/settings/api-access/regenerate', { method: 'POST' })).json());
        flashApiStatus('New token issued.');
    } catch (err) { console.error(err); }
}

function renderPhoneAccess(data) {
    document.getElementById('phone-access-toggle').checked = !!data.enabled;
    document.getElementById('phone-access-details').style.display = data.enabled ? 'block' : 'none';
    const addresses = (data.addresses || []).join(' or ');
    document.getElementById('phone-access-state').textContent =
        !data.listening ? 'Restart FastApp to switch this on.'
        : addresses ? `In the phone app, enter this PC's address: ${addresses}`
        : 'Tailscale is not running on this PC. Install it and sign in on both this PC and the phone.';
}

async function loadPhoneAccess() {
    try { renderPhoneAccess(await (await fetch('/api/settings/phone-access')).json()); }
    catch (err) { console.error(err); }
}

async function savePhoneAccess() {
    const enabled = document.getElementById('phone-access-toggle').checked;
    try { renderPhoneAccess(await (await fetch('/api/settings/phone-access', { method: 'POST', body: String(enabled) })).json()); }
    catch (err) { console.error(err); loadPhoneAccess(); }
}

async function showPhoneCode() {
    try {
        const { code } = await (await fetch('/api/settings/phone-access/code', { method: 'POST' })).json();
        document.getElementById('phone-code').textContent = code;
        document.getElementById('phone-code-hint').textContent = 'valid for 2 minutes';
        setTimeout(() => {
            if (document.getElementById('phone-code').textContent !== code) return;
            document.getElementById('phone-code').textContent = '';
            document.getElementById('phone-code-hint').textContent = '';
        }, 120000);
    } catch (err) { console.error(err); }
}

function toggleApiTokenVisible() {
    const field = document.getElementById('api-token-field');
    const showing = field.type === 'text';
    field.type = showing ? 'password' : 'text';
    document.getElementById('api-token-show').textContent = showing ? 'Show' : 'Hide';
}

async function copyApiToken() {
    const button = document.getElementById('api-token-copy');
    try {
        await navigator.clipboard.writeText(document.getElementById('api-token-field').value);
        button.textContent = 'Copied';
    } catch (err) { button.textContent = 'Copy failed'; }
    setTimeout(() => { button.textContent = 'Copy'; }, 1800);
}

async function toggleRecoveryKey() {
    const field = document.getElementById('recovery-key-field');
    const show = document.getElementById('recovery-key-show');
    const copy = document.getElementById('recovery-key-copy');
    if (field.value) {
        field.value = ''; show.textContent = 'Show'; copy.style.display = 'none';
        return;
    }
    try {
        const data = await (await fetch('/api/settings/recovery-key', { method: 'POST' })).json();
        field.value = data.key || '';
        show.textContent = 'Hide';
        copy.style.display = data.key ? '' : 'none';
    } catch (err) { console.error(err); }
}

async function copyRecoveryKey() {
    const button = document.getElementById('recovery-key-copy');
    try {
        await navigator.clipboard.writeText(document.getElementById('recovery-key-field').value);
        button.textContent = 'Copied';
    } catch (err) { button.textContent = 'Copy failed'; }
    setTimeout(() => { button.textContent = 'Copy'; }, 1800);
}

async function loadAutoBackups() {
    const line = document.getElementById('auto-backup-status');
    try {
        const data = await (await fetch('/api/backup/auto')).json();
        document.getElementById('recovery-key-block').style.display = data.encrypted ? 'block' : 'none';
        const newest = data.backups && data.backups[0];
        line.textContent = newest
            ? `FastApp also keeps a checked copy from each of the last ${data.keep} days. Latest: ${new Date(newest.date).toLocaleString()} (${data.backups.length} kept) in ${data.folder}`
            : `FastApp also keeps a checked copy from each of the last ${data.keep} days in ${data.folder}. The first one is made a few minutes after FastApp starts.`;
    } catch (err) { console.error(err); }
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

async function restoreBackupFile(file, recoveryKey) {
    const status = document.getElementById('restore-status');
    status.style.display = 'block';
    status.style.color = 'var(--text-dim)';
    status.textContent = 'Uploading and validating…';

    try {
        const formData = new FormData();
        formData.append('file', file);
        if (recoveryKey) formData.append('recoveryKey', recoveryKey);
        const res = await fetch('/api/restore', { method: 'POST', body: formData });
        const data = await res.json();

        // A backup made on another PC is locked with that PC's recovery key.
        if (data.needsKey) {
            const typed = prompt(
                (recoveryKey ? 'That key did not open this backup.\n\n' : '') +
                'This backup is encrypted with a different recovery key (for example, it was made on another PC).\n\n' +
                'Enter the recovery key that goes with it:');
            if (typed && typed.trim()) return restoreBackupFile(file, typed.trim());
            status.style.color = 'var(--rose)';
            status.textContent = 'Restore cancelled: this backup needs its recovery key.';
            return;
        }

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
        loadApiAccess();
        loadPhoneAccess();
        loadAutoBackups();
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
/* ==========================================================
   EXPORT

   A plain navigation rather than a fetch: the response carries
   Content-Disposition, so letting the browser handle it gets the save dialog,
   the right filename and a progress indicator for free. Fetching it would
   mean holding the whole file in memory to rebuild all three.
   ========================================================== */
function downloadExport(format) {
    const period = document.getElementById('export-period').value;
    const what = document.getElementById('export-what').value;
    const status = document.getElementById('export-status');

    const url = `/api/export?what=${encodeURIComponent(what)}`
              + `&format=${encodeURIComponent(format)}`
              + `&period=${encodeURIComponent(period)}`
              + `&date=${encodeURIComponent(getLocalTodayStr())}`;

    // The download replaces the page's own navigation for an instant, so say
    // something: a button that appears to do nothing gets pressed again.
    if (status) {
        status.textContent = `Preparing ${format.toUpperCase()}…`;
        status.style.display = 'block';
        setTimeout(() => { status.style.display = 'none'; }, 2500);
    }

    window.location.href = url;
}

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
