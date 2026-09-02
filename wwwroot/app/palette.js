/* ---------------------------------------------------------------------------
   The palette's client half.

   It owns no data. Everything it shows arrives from the WPF host over the
   WebView2 message bridge, and every action it takes goes back the same way --
   launching an app, capturing a hotkey and closing the window are native
   operations that have no business going through HTTP.

   The host is the only source of truth; this file renders it and reports
   intent. Written so that with no host attached (opened in a browser while
   working on it) it still renders, empty, rather than throwing.
   --------------------------------------------------------------------------- */

const bridge = window.chrome && window.chrome.webview ? window.chrome.webview : null;

function send(type, payload) {
    if (!bridge) return;
    bridge.postMessage(JSON.stringify({ type, ...(payload || {}) }));
}

let state = { apps: [], commands: [], focusToday: '—', tracking: true };
let query = '';
let active = 0;

const els = {
    q: document.getElementById('q'),
    results: document.getElementById('results'),
    focus: document.getElementById('focus-today'),
    statusDot: document.getElementById('status-dot'),
    statusText: document.getElementById('status-text'),
    count: document.getElementById('result-count'),
    counts: document.getElementById('counts'),
    enterVerb: document.getElementById('enter-verb')
};

/* Colours come from the same category palette the dashboard uses; the host
   sends the category and the tint is resolved here so the two surfaces cannot
   drift apart on it. */
const CATEGORY_TINT = {
    Gaming: 'rgba(139, 124, 255, 0.18)',
    Browsing: 'rgba(52, 211, 196, 0.18)',
    Communication: 'rgba(29, 118, 109, 0.28)',
    Development: 'rgba(139, 124, 255, 0.18)',
    Other: 'rgba(255, 255, 255, 0.035)'
};

function tint(category) {
    return CATEGORY_TINT[category] || CATEGORY_TINT.Other;
}

/* A plain subsequence match, so "vlr" finds Valorant. Ranked so that a prefix
   beats a word start, which beats a loose match anywhere. */
function score(name, q) {
    if (!q) return 0;
    const n = name.toLowerCase();
    if (n.startsWith(q)) return 3;
    if (n.split(/[\s\-_.]/).some(w => w.startsWith(q))) return 2;

    let i = 0;
    for (const ch of n) {
        if (ch === q[i]) i++;
        if (i === q.length) return 1;
    }
    return -1;
}

function visible() {
    const q = query.trim().toLowerCase();

    const apps = state.apps
        .map(a => ({ item: a, kind: 'app', s: score(a.name, q) }))
        .filter(r => r.s >= 0)
        .sort((a, b) => b.s - a.s);

    // With nothing typed the heading says RECENT, so the list has to earn it:
    // most recently used first, which for a launcher is almost always what you
    // came for. It used to be whatever order Manage happened to be in, which
    // made the heading a lie. Apps with no history keep their Manage order at
    // the bottom -- the sort is stable, and they all compare equal at 0.
    //
    // Only the unfiltered list is reordered. Once you type, ranking by how well
    // the name matches beats ranking by when you last opened it.
    if (!q) apps.sort((a, b) => (b.item.lastUsed || 0) - (a.item.lastUsed || 0));

    const commands = state.commands
        .map(c => ({ item: c, kind: 'command', s: score(c.title, q) }))
        .filter(r => r.s >= 0)
        .sort((a, b) => b.s - a.s);

    return { apps, commands, all: [...apps, ...commands] };
}

function render() {
    const { apps, commands, all } = visible();
    if (active >= all.length) active = Math.max(0, all.length - 1);

    els.results.textContent = '';

    if (all.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = query ? `Nothing matches "${query}"` : 'Nothing to show yet';
        els.results.appendChild(empty);
    } else {
        let index = 0;
        index = appendGroup(query ? 'APPS' : 'RECENT', apps, index);
        appendGroup('COMMANDS', commands, index);
    }

    els.count.textContent = query ? `${all.length} result${all.length === 1 ? '' : 's'}` : '';
    els.counts.textContent =
        `${state.apps.length} APPS · ${state.apps.filter(a => a.hotkey).length} HOTKEYS`;

    const current = all[active];
    els.enterVerb.textContent =
        !current ? 'LAUNCH'
        : current.kind === 'command' ? 'RUN'
        : current.item.running ? 'FOCUS' : 'LAUNCH';
}

function appendGroup(label, rows, startIndex) {
    if (rows.length === 0) return startIndex;

    const group = document.createElement('div');
    group.className = 'group';

    const heading = document.createElement('span');
    heading.className = 'label';
    heading.textContent = label;
    group.appendChild(heading);

    const list = document.createElement('div');
    list.className = 'group-rows';

    let index = startIndex;
    for (const row of rows) {
        list.appendChild(buildRow(row, index));
        index++;
    }

    group.appendChild(list);
    els.results.appendChild(group);
    return index;
}

function buildRow(row, index) {
    const el = document.createElement('div');
    el.className = 'row' + (index === active ? ' active' : '');
    el.addEventListener('mousemove', () => { if (active !== index) { active = index; render(); } });
    el.addEventListener('click', () => { active = index; activate(); });

    const avatar = document.createElement('span');
    avatar.className = 'avatar';

    const text = document.createElement('span');
    text.className = 'row-text';

    const name = document.createElement('span');
    name.className = 'row-name';

    if (row.kind === 'app') {
        const app = row.item;
        avatar.style.background = tint(app.category);
        avatar.textContent = (app.name[0] || '?').toUpperCase();
        name.textContent = app.name;

        const sub = document.createElement('span');
        sub.className = 'row-sub';
        sub.textContent = app.category || 'Other';
        text.append(name, sub);
        el.append(avatar, text);

        const spacer = document.createElement('span');
        spacer.className = 'row-spacer';
        el.appendChild(spacer);

        if (app.hotkey) {
            const chip = document.createElement('span');
            chip.className = 'row-chip';
            chip.textContent = app.hotkey;
            el.appendChild(chip);
        } else if (app.autoStart) {
            const chip = document.createElement('span');
            chip.className = 'row-chip';
            chip.textContent = 'Auto-start';
            el.appendChild(chip);
        }

        const figure = document.createElement('span');
        figure.className = 'row-figure';
        figure.textContent = app.today || '';
        el.appendChild(figure);
    } else {
        const command = row.item;
        avatar.style.background = CATEGORY_TINT.Other;
        avatar.textContent = '›';
        avatar.style.color = 'var(--text-dim)';
        name.textContent = command.title;
        text.appendChild(name);
        el.append(avatar, text);

        const spacer = document.createElement('span');
        spacer.className = 'row-spacer';
        el.appendChild(spacer);

        if (command.hint) {
            const hint = document.createElement('span');
            hint.className = 'row-sub';
            hint.textContent = command.hint;
            el.appendChild(hint);
        }
    }

    return el;
}

function activate() {
    const current = visible().all[active];
    if (!current) return;

    if (current.kind === 'app') send('activate-app', { id: current.item.id });
    else send('run-command', { id: current.item.id });
}

function move(delta) {
    const total = visible().all.length;
    if (total === 0) return;
    active = (active + delta + total) % total;
    render();
    const el = els.results.querySelector('.row.active');
    if (el) el.scrollIntoView({ block: 'nearest' });
}

els.q.addEventListener('input', () => { query = els.q.value; active = 0; render(); });

/* ---------------------------------------------------------------------------
   Views

   One document rather than separate pages: the palette is summoned constantly
   and must not pay for a navigation, and the detail view wants the same bridge
   and the same stylesheet. The host is told to resize when the view changes.

   The editing views used to suppress dismiss-on-click-away, on the theory that
   a stray click would lose half-typed changes. It could not: every field saves
   on change, and clicking away blurs the field, which is what fires it. So the
   protection guarded nothing and cost the behaviour people expect from a
   palette -- click elsewhere and it goes.
   --------------------------------------------------------------------------- */

const VIEWS = {
    palette: { el: document.getElementById('view-palette'), w: 760, h: 520 },
    detail: { el: document.getElementById('view-detail'), w: 820, h: 560 },
    manage: { el: document.getElementById('view-manage'), w: 940, h: 620 },
    settings: { el: document.getElementById('view-settings'), w: 940, h: 700 },
    scanner: { el: document.getElementById('view-scanner'), w: 880, h: 640 }
};

let view = 'palette';

function show(name) {
    view = name;
    for (const [key, v] of Object.entries(VIEWS)) v.el.hidden = key !== name;

    const target = VIEWS[name];
    send('resize', { width: target.w, height: target.h });

    if (name === 'palette') els.q.focus();
}

document.addEventListener('keydown', e => {
    if (view === 'scanner') {
        switch (e.key) {
            case 'Escape': e.preventDefault(); show('palette'); break;
            case 'ArrowDown': e.preventDefault(); moveScan(1); break;
            case 'ArrowUp': e.preventDefault(); moveScan(-1); break;
            case ' ': e.preventDefault(); toggleScanPick(); break;
            case 'Enter': e.preventDefault(); addScanned(); break;
        }
        return;
    }

    if (view === 'settings') {
        if (e.key === 'Escape') { e.preventDefault(); show('palette'); }
        return;
    }

    if (view === 'manage') {
        switch (e.key) {
            case 'Escape': e.preventDefault(); show('palette'); break;
            case 'ArrowDown':
            case 'ArrowUp': {
                e.preventDefault();
                const delta = e.key === 'ArrowDown' ? 1 : -1;
                // Alt turns navigation into reordering, which is also the
                // startup order, so the two live on the same keys deliberately.
                if (e.altKey) {
                    const entry = state.apps[manageActive];
                    if (entry) {
                        send('reorder-app', { id: entry.id, delta });
                        manageActive = Math.min(Math.max(manageActive + delta, 0), state.apps.length - 1);
                    }
                } else {
                    manageActive = Math.min(Math.max(manageActive + delta, 0), state.apps.length - 1);
                    renderManage();
                }
                break;
            }
            case 'Enter': {
                e.preventDefault();
                const entry = state.apps[manageActive];
                if (entry) send('edit-app', { id: entry.id });
                break;
            }
        }
        return;
    }

    if (view === 'detail') {
        if (e.key === 'Escape') {
            e.preventDefault();
            if (capturing) { stopCapture(); return; }
            show('palette');
        }
        return;
    }

    switch (e.key) {
        case 'ArrowDown': e.preventDefault(); move(1); break;
        case 'ArrowUp': e.preventDefault(); move(-1); break;
        case 'Enter': e.preventDefault(); activate(); break;
        case 'Escape': e.preventDefault(); send('close'); break;
        case 'Tab': {
            e.preventDefault();
            const current = visible().all[active];
            if (current && current.kind === 'app') send('edit-app', { id: current.item.id });
            break;
        }
    }
});

/* ---------------------------------------------------------------------------
   Scanner

   The host owns the scan; this only chooses from it. Selections are held as
   paths rather than positions, so a re-scan cannot silently move the choice
   onto a different application.
   --------------------------------------------------------------------------- */

let scanApps = [];
let scanning = false;
let scanActive = 0;
let scanFilter = '';
const scanPicked = new Set();

const sc = {
    list: document.getElementById('sc-list'),
    filter: document.getElementById('sc-filter'),
    count: document.getElementById('sc-count'),
    subtitle: document.getElementById('sc-subtitle'),
    add: document.getElementById('sc-add'),
    none: document.getElementById('sc-none')
};

function scanVisible() {
    const q = scanFilter.trim().toLowerCase();
    return q ? scanApps.filter(a => score(a.name, q) >= 0) : scanApps;
}

function renderScan() {
    const rows = scanVisible();
    if (scanActive >= rows.length) scanActive = Math.max(0, rows.length - 1);

    sc.list.textContent = '';

    if (scanning) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = 'Looking through your Start menu and the Microsoft Store…';
        sc.list.appendChild(empty);
    } else if (rows.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = scanFilter
            ? `Nothing found matching "${scanFilter}"`
            : 'Nothing new found. Everything discovered is already managed.';
        sc.list.appendChild(empty);
    } else {
        rows.forEach((app, index) => sc.list.appendChild(buildScanRow(app, index)));
    }

    sc.count.textContent = String(scanPicked.size);
    sc.add.disabled = scanPicked.size === 0;
    sc.add.textContent = scanPicked.size === 0
        ? 'Add'
        : `Add ${scanPicked.size} application${scanPicked.size === 1 ? '' : 's'}`;

    sc.subtitle.textContent = scanning
        ? 'Scanning…'
        : `${scanApps.length} found that you are not already managing`;

    const active = sc.list.querySelector('.sc-row.active');
    if (active) active.scrollIntoView({ block: 'nearest' });
}

function buildScanRow(app, index) {
    const row = document.createElement('div');
    row.className = 'sc-row'
        + (index === scanActive ? ' active' : '')
        + (scanPicked.has(app.path) ? ' picked' : '');
    row.addEventListener('click', () => { scanActive = index; toggleScanPick(); });

    const tick = document.createElement('span');
    tick.className = 'sc-tick';
    if (scanPicked.has(app.path)) {
        const mark = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        mark.setAttribute('width', '12');
        mark.setAttribute('height', '12');
        mark.setAttribute('viewBox', '0 0 24 24');
        mark.setAttribute('fill', 'none');
        mark.setAttribute('stroke', '#1A1000');
        mark.setAttribute('stroke-width', '3');
        mark.setAttribute('stroke-linecap', 'round');
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', 'M5 12l5 5L19 7');
        mark.appendChild(path);
        tick.appendChild(mark);
    }

    const avatar = document.createElement('span');
    avatar.className = 'avatar';
    avatar.style.width = '30px';
    avatar.style.height = '30px';
    avatar.style.background = 'rgba(255, 255, 255, 0.035)';
    avatar.textContent = (app.name[0] || '?').toUpperCase();

    const text = document.createElement('span');
    text.className = 'sc-text';
    const name = document.createElement('span');
    name.className = 'sc-name';
    name.textContent = app.name;
    const path = document.createElement('span');
    path.className = 'sc-path';
    path.textContent = app.path;
    text.append(name, path);

    const spacer = document.createElement('span');
    spacer.className = 'row-spacer';

    const source = document.createElement('span');
    source.className = 'sc-source' + (app.packaged ? ' store' : '');
    source.textContent = app.packaged ? 'STORE' : 'START MENU';

    row.append(tick, avatar, text, spacer, source);
    return row;
}

function moveScan(delta) {
    const total = scanVisible().length;
    if (total === 0) return;
    scanActive = Math.min(Math.max(scanActive + delta, 0), total - 1);
    renderScan();
}

function toggleScanPick() {
    const app = scanVisible()[scanActive];
    if (!app) return;
    if (scanPicked.has(app.path)) scanPicked.delete(app.path);
    else scanPicked.add(app.path);
    renderScan();
}

function addScanned() {
    if (scanPicked.size === 0) return;
    send('add-scanned', { paths: [...scanPicked] });
    scanPicked.clear();
    renderScan();
}

sc.filter.addEventListener('input', () => { scanFilter = sc.filter.value; scanActive = 0; renderScan(); });
sc.add.addEventListener('click', addScanned);
sc.none.addEventListener('click', () => { scanPicked.clear(); renderScan(); });
document.querySelector('[data-back-scanner]').addEventListener('click', () => show('palette'));

/* ---------------------------------------------------------------------------
   Settings

   Nothing here is stored locally. Every change is sent to the host, which sets
   the matching view-model property so the persistence and side effects the WPF
   settings tab relies on all still happen, and the host then sends the whole
   settings state back. Some of it is answered asynchronously -- the startup
   toggle waits on a UAC prompt -- so the page always renders what came back
   rather than what it just sent.
   --------------------------------------------------------------------------- */

const st = {
    version: document.getElementById('s-version'),
    versionLine: document.getElementById('s-version-line'),
    conflict: document.getElementById('s-conflict'),
    conflictPath: document.getElementById('s-conflict-path'),
    fix: document.getElementById('s-fix'),
    startup: document.getElementById('s-startup'),
    osd: document.getElementById('s-osd'),
    progress: document.getElementById('s-progress'),
    notify: document.getElementById('s-notify'),
    quiet: document.getElementById('s-quiet'),
    quietTimes: document.getElementById('s-quiet-times'),
    quietFrom: document.getElementById('s-quiet-from'),
    quietTo: document.getElementById('s-quiet-to'),
    dashboardStatus: document.getElementById('s-dashboard-status'),
    openDashboard: document.getElementById('s-open-dashboard'),
    updateStatus: document.getElementById('s-update-status'),
    check: document.getElementById('s-check'),
    apply: document.getElementById('s-apply'),
    whatsNewCard: document.getElementById('s-whatsnew-card'),
    whatsNewLabel: document.getElementById('s-whatsnew-label'),
    whatsNew: document.getElementById('s-whatsnew'),
    rollbackCard: document.getElementById('s-rollback-card'),
    rollbackVersion: document.getElementById('s-rollback-version'),
    rollback: document.getElementById('s-rollback'),
    rollbackWarning: document.getElementById('s-rollback-warning'),
    rollbackStatus: document.getElementById('s-rollback-status'),
    whatsNewEmpty: document.getElementById('s-whatsnew-empty'),
    rollbackRow: document.getElementById('s-rollback-row'),
    rollbackEmpty: document.getElementById('s-rollback-empty')
};

function renderSettings(v) {
    st.version.textContent = v.version || '';
    st.versionLine.textContent = v.version || 'FastApp';

    st.conflict.hidden = !v.hasStartupConflict;
    st.conflictPath.textContent = (v.startupConflictText || '').split('\n').pop();

    setToggle(st.startup, v.launchOnStartup);
    setToggle(st.osd, v.enableOsd);
    setToggle(st.progress, v.showAutoLaunchProgress);
    setToggle(st.notify, v.notificationsEnabled);
    setToggle(st.quiet, v.quietHoursEnabled);

    st.quietTimes.hidden = !v.quietHoursEnabled;
    if (document.activeElement !== st.quietFrom) st.quietFrom.value = v.quietHoursFrom || '';
    if (document.activeElement !== st.quietTo) st.quietTo.value = v.quietHoursTo || '';

    st.dashboardStatus.textContent = v.dashboardStatus || '';
    st.openDashboard.disabled = !v.dashboardRunning;

    st.updateStatus.textContent = v.updateStatus || '';
    st.check.textContent = v.checkingForUpdates ? 'Checking…' : 'Check now';
    st.apply.hidden = !v.updateReady;

    // Both of the cards below stay on screen with nothing to show. A section
    // that vanishes entirely reads as a bug or a missing feature; one that
    // explains why it is empty answers the question instead.
    const devBuild = /dev/i.test(v.version || '');

    st.whatsNewLabel.textContent =
        v.hasWhatsNew && v.version ? `WHAT'S NEW IN ${v.version}` : "WHAT'S NEW";
    renderNotes(v.hasWhatsNew ? (v.whatsNew || '') : '');
    st.whatsNewEmpty.hidden = v.hasWhatsNew;
    st.whatsNewEmpty.textContent = devBuild
        ? 'Development builds carry no release notes. They appear here for installed versions.'
        : 'No notes were recorded for this version.';

    st.rollbackRow.hidden = !v.hasRollbackVersions;
    st.rollbackEmpty.hidden = v.hasRollbackVersions;
    st.rollbackEmpty.textContent = devBuild
        ? 'Not available on a development build, which Windows has no installed history for.'
        : 'No earlier version is installed yet. After your first update, the version you came from can be reinstalled here.';

    if (v.rollbackVersions && st.rollbackVersion.options.length !== v.rollbackVersions.length) {
        st.rollbackVersion.textContent = '';
        for (const version of v.rollbackVersions) {
            const option = document.createElement('option');
            option.value = version;
            option.textContent = version;
            st.rollbackVersion.appendChild(option);
        }
    }
    if (v.selectedRollback) st.rollbackVersion.value = v.selectedRollback;
    st.rollbackWarning.hidden = !v.rollbackWarning;
    st.rollbackWarning.textContent = v.rollbackWarning || '';
    st.rollbackStatus.hidden = !v.rollbackStatus;
    st.rollbackStatus.textContent = v.rollbackStatus || '';
    st.rollback.textContent = v.rollbackBusy ? 'Working…' : 'Reinstall';
    st.rollback.disabled = !!v.rollbackBusy;
}

/* The notes arrive as the release body: bullet lines and prose. Rendered as
   text, never as markup -- it comes from a GitHub release, which is not ours
   to trust as HTML. */
function renderNotes(text) {
    st.whatsNew.textContent = '';
    for (const raw of text.split('\n')) {
        const line = raw.trim();
        if (!line) continue;

        const note = document.createElement('div');
        note.className = 's-note';

        const bullet = line.startsWith('-') || line.startsWith('*');
        if (bullet) {
            const dot = document.createElement('span');
            dot.className = 's-note-dot';
            note.appendChild(dot);
        }

        const body = document.createElement('span');
        body.className = 's-note-text';
        body.textContent = (bullet ? line.slice(1) : line).replace(/\*\*/g, '').trim();
        note.appendChild(body);
        st.whatsNew.appendChild(note);
    }
}

function setting(key, value) { send('set-setting', { key, value }); }
function settingText(key, text) { send('set-setting', { key, text }); }

for (const [el, key] of [
    [st.startup, 'launchOnStartup'],
    [st.osd, 'enableOsd'],
    [st.progress, 'showAutoLaunchProgress'],
    [st.notify, 'notificationsEnabled'],
    [st.quiet, 'quietHoursEnabled']
]) {
    el.addEventListener('click', () => {
        const next = !toggleOn(el);
        setToggle(el, next);          // optimistic; the host's reply is the truth
        setting(key, next);
    });
}

st.quietFrom.addEventListener('change', () => settingText('quietHoursFrom', st.quietFrom.value));
st.quietTo.addEventListener('change', () => settingText('quietHoursTo', st.quietTo.value));
st.rollbackVersion.addEventListener('change', () => settingText('selectedRollback', st.rollbackVersion.value));

st.fix.addEventListener('click', () => send('settings-command', { id: 'fix-startup' }));
st.check.addEventListener('click', () => send('settings-command', { id: 'check-updates' }));
st.apply.addEventListener('click', () => send('settings-command', { id: 'apply-update' }));
st.rollback.addEventListener('click', () => send('settings-command', { id: 'rollback' }));
st.openDashboard.addEventListener('click', () => send('settings-command', { id: 'open-dashboard' }));
document.querySelector('[data-back-settings]').addEventListener('click', () => show('palette'));

/* ---------------------------------------------------------------------------
   Manage view
   --------------------------------------------------------------------------- */

let manageActive = 0;
const mList = document.getElementById('m-list');
const mCounts = document.getElementById('m-counts');

function renderManage() {
    mList.textContent = '';

    state.apps.forEach((app, index) => {
        const row = document.createElement('div');
        row.className = 'm-row' + (index === manageActive ? ' active' : '');
        row.addEventListener('click', () => { manageActive = index; renderManage(); });
        row.addEventListener('dblclick', () => send('edit-app', { id: app.id }));

        const avatar = document.createElement('span');
        avatar.className = 'avatar';
        avatar.style.width = '30px';
        avatar.style.height = '30px';
        avatar.style.background = tint(app.category);
        avatar.textContent = (app.name[0] || '?').toUpperCase();

        const name = document.createElement('span');
        name.className = 'm-name';
        name.textContent = app.name;

        const hotkey = document.createElement('span');
        hotkey.className = app.hotkey ? 'row-chip' : 'm-meta';
        hotkey.textContent = app.hotkey || '—';
        if (app.hotkey) hotkey.style.justifySelf = 'start';

        const limit = document.createElement('span');
        limit.className = 'm-meta';
        limit.textContent = app.limitMinutes ? app.limitMinutes + 'm' : '—';

        const auto = document.createElement('span');
        auto.className = 'm-dot' + (app.autoStart ? '' : ' off');

        const figure = document.createElement('span');
        figure.className = 'm-figure';
        figure.textContent = app.today || '—';

        row.append(avatar, name, hotkey, limit, auto, figure);
        mList.appendChild(row);
    });

    const withHotkeys = state.apps.filter(a => a.hotkey).length;
    const withAuto = state.apps.filter(a => a.autoStart).length;
    mCounts.textContent =
        `${state.apps.length} ENTRIES · ${withHotkeys} HOTKEYS · ${withAuto} AUTO-START`;

    const active = mList.querySelector('.m-row.active');
    if (active) active.scrollIntoView({ block: 'nearest' });
}

document.getElementById('m-new-action').addEventListener('click', () => send('new-action'));
document.getElementById('m-browse').addEventListener('click', () => send('browse-files'));
document.getElementById('m-scan').addEventListener('click', () => send('open-scanner'));
document.querySelector('[data-back-manage]').addEventListener('click', () => show('palette'));

/* ---------------------------------------------------------------------------
   Detail view
   --------------------------------------------------------------------------- */

const d = {
    avatar: document.getElementById('d-avatar'),
    name: document.getElementById('d-name'),
    category: document.getElementById('d-category'),
    today: document.getElementById('d-today'),
    triggers: document.getElementById('d-triggers'),
    customName: document.getElementById('d-custom-name'),
    path: document.getElementById('d-path'),
    hotkey: document.getElementById('d-hotkey'),
    hotkeyClear: document.getElementById('d-hotkey-clear'),
    suppress: document.getElementById('d-suppress'),
    autostart: document.getElementById('d-autostart'),
    args: document.getElementById('d-args'),
    delay: document.getElementById('d-delay'),
    limit: document.getElementById('d-limit'),
    force: document.getElementById('d-force'),
    limitCard: document.getElementById('d-limit-card'),
    locked: document.getElementById('d-locked'),
    limitNote: document.getElementById('d-limit-note'),
    remove: document.getElementById('d-delete'),
    actionCard: document.getElementById('d-action-card'),
    actionHint: document.getElementById('d-action-hint'),
    payloadCard: document.getElementById('d-payload-card'),
    payload: document.getElementById('d-payload'),
    startupCard: document.getElementById('d-startup-card'),
    execRow: document.getElementById('d-exec-row')
};

const ACTION_HINTS = {
    1: 'Mutes or unmutes the system volume. Nothing else to configure.',
    2: 'Centres the window you are currently in, on the screen it is on.',
    3: 'Pastes the text below, then puts your clipboard back as it was.'
};

let editing = null;
let capturing = false;

function setToggle(el, on) { el.setAttribute('aria-checked', on ? 'true' : 'false'); }
function toggleOn(el) { return el.getAttribute('aria-checked') === 'true'; }

function renderDetail(app) {
    editing = app;

    d.avatar.textContent = (app.displayName[0] || '?').toUpperCase();
    d.avatar.style.background = tint(app.category);
    d.name.textContent = app.displayName;
    d.category.textContent = app.category || 'Other';
    d.today.textContent = app.today || '0m';
    d.triggers.textContent = app.triggerCount;

    d.customName.value = app.customName || '';
    d.path.textContent = app.packaged
        ? 'Microsoft Store app'
        : (app.executablePath || 'No executable');

    // An action has no executable, nothing to auto-start and no time to limit;
    // showing those fields greyed would only invite the question of why.
    d.actionCard.hidden = !app.isAction;
    d.payloadCard.hidden = !(app.isAction && app.actionType === 3);

    // The mirror of the above: an action has no executable to show, nothing to
    // open at login and no running time to limit, so those cards go entirely
    // rather than sitting there greyed out inviting the question of why.
    d.startupCard.hidden = app.isAction;
    d.limitCard.hidden = app.isAction;
    d.execRow.hidden = app.isAction;
    d.payload.value = app.actionPayload || '';
    for (const pill of d.actionCard.querySelectorAll('.pill')) {
        pill.setAttribute('aria-pressed',
            Number(pill.dataset.action) === app.actionType ? 'true' : 'false');
    }
    d.actionHint.textContent = ACTION_HINTS[app.actionType] || '';

    d.hotkey.textContent = app.hotkeySequence ? app.hotkeyDisplay : 'None';
    setToggle(d.suppress, app.suppressHotkeyPassthrough);
    setToggle(d.autostart, app.launchOnStartup);
    d.args.value = app.launchArguments || '';
    d.delay.value = app.launchDelaySeconds ? String(app.launchDelaySeconds) : '';
    d.limit.value = app.dailyLimitMinutes ? String(app.dailyLimitMinutes) : '';
    setToggle(d.force, app.strictFocusMode);

    // The PIN gates limits wherever they are edited from; this surface must not
    // become a way around it.
    d.locked.hidden = !app.limitsLocked;
    d.limitNote.hidden = !app.limitsLocked;
    d.limitCard.classList.toggle('disabled', app.limitsLocked);
    d.limit.disabled = app.limitsLocked;
    d.force.disabled = app.limitsLocked;

    show('detail');
}

function saveDetail() {
    if (!editing) return;
    send('save-app', {
        app: {
            id: editing.id,
            customName: d.customName.value,
            hotkeySequence: editing.hotkeySequence || '',
            hotkeyDisplay: editing.hotkeyDisplay || '',
            suppressHotkeyPassthrough: toggleOn(d.suppress),
            launchOnStartup: toggleOn(d.autostart),
            launchArguments: d.args.value,
            launchDelaySeconds: parseInt(d.delay.value, 10) || 0,
            dailyLimitMinutes: parseInt(d.limit.value, 10) || 0,
            strictFocusMode: toggleOn(d.force),
            category: editing.category,
            actionType: editing.actionType,
            actionPayload: d.payload.value
        }
    });
}

for (const pill of d.actionCard.querySelectorAll('.pill')) {
    pill.addEventListener('click', () => {
        if (!editing) return;
        editing.actionType = Number(pill.dataset.action);
        for (const other of d.actionCard.querySelectorAll('.pill')) {
            other.setAttribute('aria-pressed', other === pill ? 'true' : 'false');
        }
        d.payloadCard.hidden = editing.actionType !== 3;
        d.actionHint.textContent = ACTION_HINTS[editing.actionType] || '';
        saveDetail();
    });
}
d.payload.addEventListener('change', saveDetail);

for (const el of [d.customName, d.args, d.delay, d.limit]) {
    el.addEventListener('change', saveDetail);
}
for (const el of [d.suppress, d.autostart, d.force]) {
    el.addEventListener('click', () => {
        if (el.disabled) return;
        setToggle(el, !toggleOn(el));
        saveDetail();
    });
}

function startCapture() {
    capturing = true;
    d.hotkey.classList.add('capturing');
    d.hotkey.textContent = 'Press a combination\u2026';
    send('capture-hotkey');
}

function stopCapture() {
    capturing = false;
    d.hotkey.classList.remove('capturing');
    d.hotkey.textContent = editing && editing.hotkeySequence ? editing.hotkeyDisplay : 'None';
    send('cancel-capture');
}

d.hotkey.addEventListener('click', () => (capturing ? stopCapture() : startCapture()));

d.hotkeyClear.addEventListener('click', () => {
    if (!editing) return;
    editing.hotkeySequence = '';
    editing.hotkeyDisplay = 'None';
    d.hotkey.textContent = 'None';
    saveDetail();
});

d.remove.addEventListener('click', () => {
    if (!editing) return;
    send('delete-app', { id: editing.id });
    show('palette');
});

document.querySelector('[data-back]').addEventListener('click', () => show('palette'));

/* The host pushes a whole new state rather than deltas: the palette is small
   enough that reconciling partial updates would cost more than it saves. */
if (bridge) {
    bridge.addEventListener('message', event => {
        let message;
        try {
            message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        } catch {
            return;
        }
        if (!message) return;

        if (message.type === 'app') { renderDetail(message.app); return; }

        if (message.type === 'hotkey-captured') {
            if (!capturing || !editing) return;
            capturing = false;
            d.hotkey.classList.remove('capturing');
            editing.hotkeySequence = message.sequence;
            editing.hotkeyDisplay = message.display;
            d.hotkey.textContent = message.display;
            saveDetail();
            return;
        }

        if (message.type === 'reset') {
            // A summon always starts clean. The window focus handler does this
            // too, but it does not fire when the palette is re-summoned while
            // already focused, which left the old query and selection in place.
            query = '';
            els.q.value = '';
            active = 0;
            show('palette');
            render();
            return;
        }

        if (message.type === 'show-manage') { manageActive = 0; renderManage(); show('manage'); return; }

        if (message.type === 'show-settings') { show('settings'); return; }

        if (message.type === 'show-scanner') {
            scanPicked.clear();
            scanFilter = '';
            sc.filter.value = '';
            scanActive = 0;
            show('scanner');
            return;
        }

        if (message.type === 'scan') {
            scanning = message.scan.scanning;
            scanApps = message.scan.apps || [];
            renderScan();
            return;
        }

        if (message.type === 'settings') { renderSettings(message.settings); return; }

        if (message.type !== 'state') return;

        state = { ...state, ...message.state };
        if (view === 'manage') renderManage();
        els.focus.textContent = state.focusToday || '—';
        els.statusText.textContent = state.tracking ? 'TRACKING' : 'PAUSED';
        els.statusDot.style.background = state.tracking ? 'var(--teal)' : 'var(--text-faint)';
        render();
    });
}

/* Reset on every open: a palette that reopens holding the last query is a
   palette you have to clear before you can use it. */
window.addEventListener('focus', () => {
    query = '';
    els.q.value = '';
    active = 0;
    els.q.focus();
    render();
    send('ready');
});

render();
send('ready');
