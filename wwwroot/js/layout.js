/* ==========================================================
   CARD LAYOUT

   Lets people put the cards they care about at the top and
   hide the ones they don't, on Overview, Insights and the App
   Detail drawer.

   Vertical order only, and the movable unit is a whole
   top-level block rather than an individual card. Some of those
   blocks are pairs -- AFK sits beside Uptime, Top Applications
   beside Top Categories -- and free 2D placement would mean
   flattening every block to one uniform width, which throws
   away the hero/minor size hierarchy and changes the layout for
   everyone. Moving blocks keeps the design intact and still
   answers "put that at the top".

   Order lives on the server, not in localStorage: this is work
   someone did deliberately, and per-browser storage loses it
   the moment they open the dashboard somewhere else.
   ========================================================== */

// Container per scope. Its direct children carrying data-card are the movable
// blocks; anything else (the view header) stays where it is.
const LAYOUT_SCOPES = {
    overview:  '#view-overview',
    insights:  '#view-insights',
    appdetail: '#dd-tab-panel-overview'
};

let layoutConfig = {};
let layoutEditScope = null;

function layoutFor(scope) {
    if (!layoutConfig[scope]) layoutConfig[scope] = { order: [], hidden: [] };
    return layoutConfig[scope];
}

function layoutContainer(scope) {
    const sel = LAYOUT_SCOPES[scope];
    return sel ? document.querySelector(sel) : null;
}

function layoutBlocks(scope) {
    const container = layoutContainer(scope);
    return container ? [...container.children].filter(el => el.dataset.card) : [];
}

async function loadLayout() {
    try {
        layoutConfig = await apiFetch('/api/settings/layout') || {};
    } catch (err) {
        // A missing or unreachable layout is not an error worth showing: the
        // default order is the markup order, which is what the page already is.
        if (!isAbort(err)) console.warn('Layout not loaded, using defaults', err);
        layoutConfig = {};
    }
    Object.keys(LAYOUT_SCOPES).forEach(applyLayout);
}

async function saveLayout() {
    try {
        const res = await fetch('/api/settings/layout', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(layoutConfig)
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
    } catch (err) {
        console.error('Could not save layout', err);
        const bar = document.querySelector(`.layout-bar[data-scope="${layoutEditScope}"] .layout-bar-status`);
        if (bar) bar.textContent = "Couldn't save — your changes may not stick.";
    }
}

// Applies stored order and hidden flags to the live DOM.
function applyLayout(scope) {
    const container = layoutContainer(scope);
    if (!container) return;

    const cfg = layoutFor(scope);
    const blocks = layoutBlocks(scope);
    const byId = new Map(blocks.map(el => [el.dataset.card, el]));

    // Stored order first, then anything the stored order doesn't mention, in
    // markup order. That way a card added in a later release appears rather than
    // vanishing because an older saved layout never heard of it.
    const ordered = [];
    cfg.order.forEach(id => { if (byId.has(id)) { ordered.push(byId.get(id)); byId.delete(id); } });
    blocks.forEach(el => { if (byId.has(el.dataset.card)) ordered.push(el); });

    ordered.forEach(el => container.appendChild(el));
    blocks.forEach(el => {
        const hidden = cfg.hidden.includes(el.dataset.card);
        el.classList.toggle('layout-hidden', hidden);
    });
}

function currentOrder(scope) {
    return layoutBlocks(scope).map(el => el.dataset.card);
}

// --- Editing ---------------------------------------------------------------

function toggleLayoutEdit(scope) {
    if (layoutEditScope === scope) { exitLayoutEdit(); return; }
    if (layoutEditScope) exitLayoutEdit();

    layoutEditScope = scope;
    const container = layoutContainer(scope);
    if (!container) return;
    container.classList.add('layout-editing');

    layoutBlocks(scope).forEach(el => decorateForEdit(el, scope));
    renderLayoutBar(scope);
    updateLayoutButtons(scope);
}

function exitLayoutEdit() {
    const scope = layoutEditScope;
    if (!scope) return;
    const container = layoutContainer(scope);
    if (container) container.classList.remove('layout-editing');
    layoutBlocks(scope).forEach(el => {
        el.removeAttribute('draggable');
        el.querySelector(':scope > .layout-handle')?.remove();
    });
    document.querySelector(`.layout-bar[data-scope="${scope}"]`)?.remove();
    layoutEditScope = null;
}

function decorateForEdit(el, scope) {
    if (el.querySelector(':scope > .layout-handle')) return;
    el.setAttribute('draggable', 'true');

    const name = el.dataset.cardName || el.dataset.card;
    const hidden = layoutFor(scope).hidden.includes(el.dataset.card);

    // The handle is a real button, and the arrows are the keyboard path. Drag is
    // the obvious gesture but it is mouse-only; without these the feature would
    // be unreachable for anyone not using one.
    const handle = document.createElement('div');
    handle.className = 'layout-handle';
    handle.innerHTML = `
        <span class="layout-grip" aria-hidden="true">⠿</span>
        <span class="layout-name">${escapeHtml(name)}</span>
        <button type="button" class="layout-btn" data-layout-move="up"   title="Move up"   aria-label="Move ${escapeHtml(name)} up">▲</button>
        <button type="button" class="layout-btn" data-layout-move="down" title="Move down" aria-label="Move ${escapeHtml(name)} down">▼</button>
        <button type="button" class="layout-btn layout-btn-hide" data-layout-hide
                aria-pressed="${hidden}" title="${hidden ? 'Show this card' : 'Hide this card'}"
                aria-label="${hidden ? 'Show' : 'Hide'} ${escapeHtml(name)}">${hidden ? 'Show' : 'Hide'}</button>`;
    el.prepend(handle);
}

function moveCard(scope, cardId, delta) {
    const blocks = layoutBlocks(scope);
    const index = blocks.findIndex(el => el.dataset.card === cardId);
    const target = index + delta;
    if (index < 0 || target < 0 || target >= blocks.length) return;

    const container = layoutContainer(scope);
    const moving = blocks[index];
    if (delta < 0) container.insertBefore(moving, blocks[target]);
    else container.insertBefore(moving, blocks[target].nextSibling);

    layoutFor(scope).order = currentOrder(scope);
    saveLayout();
    updateLayoutButtons(scope);
    moving.querySelector(':scope > .layout-handle [data-layout-move]')?.focus();
}

function setCardHidden(scope, cardId, hidden) {
    const cfg = layoutFor(scope);
    cfg.hidden = cfg.hidden.filter(id => id !== cardId);
    if (hidden) cfg.hidden.push(cardId);
    cfg.order = currentOrder(scope);

    const el = layoutBlocks(scope).find(b => b.dataset.card === cardId);
    if (el) {
        el.classList.toggle('layout-hidden', hidden);
        const btn = el.querySelector(':scope > .layout-handle [data-layout-hide]');
        const name = el.dataset.cardName || cardId;
        if (btn) {
            btn.textContent = hidden ? 'Show' : 'Hide';
            btn.setAttribute('aria-pressed', String(hidden));
            btn.setAttribute('aria-label', `${hidden ? 'Show' : 'Hide'} ${name}`);
            btn.title = hidden ? 'Show this card' : 'Hide this card';
        }
    }
    saveLayout();
    renderLayoutBarStatus(scope);
}

// First and last card cannot move further; saying so beats a button that
// silently does nothing.
function updateLayoutButtons(scope) {
    const blocks = layoutBlocks(scope);
    blocks.forEach((el, i) => {
        const up = el.querySelector(':scope > .layout-handle [data-layout-move="up"]');
        const down = el.querySelector(':scope > .layout-handle [data-layout-move="down"]');
        if (up) up.disabled = i === 0;
        if (down) down.disabled = i === blocks.length - 1;
    });
    renderLayoutBarStatus(scope);
}

function renderLayoutBar(scope) {
    const container = layoutContainer(scope);
    if (!container || container.querySelector(`.layout-bar[data-scope="${scope}"]`)) return;
    const bar = document.createElement('div');
    bar.className = 'layout-bar';
    bar.dataset.scope = scope;
    bar.innerHTML = `
        <span class="layout-bar-text">Drag a card, or use the arrows. Changes save as you go.</span>
        <span class="layout-bar-status"></span>
        <button type="button" class="btn btn-ghost" data-layout-reset>Reset</button>
        <button type="button" class="btn btn-brass" data-layout-done>Done</button>`;
    container.prepend(bar);
    renderLayoutBarStatus(scope);
}

function renderLayoutBarStatus(scope) {
    const status = document.querySelector(`.layout-bar[data-scope="${scope}"] .layout-bar-status`);
    if (!status) return;
    const n = layoutFor(scope).hidden.length;
    status.textContent = n ? `${n} card${n === 1 ? '' : 's'} hidden` : '';
}

function resetLayout(scope) {
    layoutConfig[scope] = { order: [], hidden: [] };
    // Markup order is the default, and it is whatever the elements were sorted
    // into; re-sorting by the original document order is what "reset" means.
    const container = layoutContainer(scope);
    const blocks = layoutBlocks(scope);
    blocks.sort((a, b) => (LAYOUT_DEFAULT_ORDER[scope] || []).indexOf(a.dataset.card)
                        - (LAYOUT_DEFAULT_ORDER[scope] || []).indexOf(b.dataset.card));
    blocks.forEach(el => container.appendChild(el));
    blocks.forEach(el => el.classList.remove('layout-hidden'));
    blocks.forEach(el => {
        const btn = el.querySelector(':scope > .layout-handle [data-layout-hide]');
        if (btn) { btn.textContent = 'Hide'; btn.setAttribute('aria-pressed', 'false'); }
    });
    saveLayout();
    updateLayoutButtons(scope);
}

// Captured once at startup, before any stored layout is applied, so Reset has a
// real target rather than "whatever order it is in now".
const LAYOUT_DEFAULT_ORDER = {};
function captureDefaultLayoutOrder() {
    Object.keys(LAYOUT_SCOPES).forEach(scope => {
        LAYOUT_DEFAULT_ORDER[scope] = layoutBlocks(scope).map(el => el.dataset.card);
    });
}

// --- Wiring ----------------------------------------------------------------

function initLayout() {
    captureDefaultLayoutOrder();

    document.addEventListener('click', (e) => {
        const toggle = e.target.closest('[data-layout-edit]');
        if (toggle) { toggleLayoutEdit(toggle.dataset.layoutEdit); return; }
        if (!layoutEditScope) return;

        if (e.target.closest('[data-layout-done]')) { exitLayoutEdit(); return; }
        if (e.target.closest('[data-layout-reset]')) { resetLayout(layoutEditScope); return; }

        const move = e.target.closest('[data-layout-move]');
        if (move) {
            const card = move.closest('[data-card]');
            moveCard(layoutEditScope, card.dataset.card, move.dataset.layoutMove === 'up' ? -1 : 1);
            return;
        }
        const hide = e.target.closest('[data-layout-hide]');
        if (hide) {
            const card = hide.closest('[data-card]');
            setCardHidden(layoutEditScope, card.dataset.card,
                          !layoutFor(layoutEditScope).hidden.includes(card.dataset.card));
        }
    });

    initLayoutDragAndDrop();
}

let layoutDragged = null;

function initLayoutDragAndDrop() {
    document.addEventListener('dragstart', (e) => {
        if (!layoutEditScope) return;
        const card = e.target.closest('[data-card]');
        if (!card || !layoutContainer(layoutEditScope)?.contains(card)) return;
        layoutDragged = card;
        card.classList.add('layout-dragging');
        e.dataTransfer.effectAllowed = 'move';
        // Firefox refuses to start a drag without data set on the transfer.
        e.dataTransfer.setData('text/plain', card.dataset.card);
    });

    document.addEventListener('dragover', (e) => {
        if (!layoutDragged || !layoutEditScope) return;
        const over = e.target.closest('[data-card]');
        const container = layoutContainer(layoutEditScope);
        if (!over || over === layoutDragged || !container.contains(over)) return;
        e.preventDefault();

        // Insert before or after depending on which half of the target the
        // pointer is in, so the card lands where it looks like it will.
        const box = over.getBoundingClientRect();
        const after = e.clientY > box.top + box.height / 2;
        container.insertBefore(layoutDragged, after ? over.nextSibling : over);
    });

    document.addEventListener('drop', (e) => { if (layoutDragged) e.preventDefault(); });

    document.addEventListener('dragend', () => {
        if (!layoutDragged) return;
        layoutDragged.classList.remove('layout-dragging');
        layoutDragged = null;
        if (!layoutEditScope) return;
        layoutFor(layoutEditScope).order = currentOrder(layoutEditScope);
        saveLayout();
        updateLayoutButtons(layoutEditScope);
    });
}
