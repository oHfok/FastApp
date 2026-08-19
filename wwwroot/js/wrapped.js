/* ==========================================================
   WRAPPED — Spotify-Wrapped-style recap of the current week/
   month/year. Always live (never a one-time reveal): an
   in-progress period is compared to the same elapsed portion
   of the previous one, not its full total. Deliberately no
   browsing past periods here — that's what Periods is for;
   this is a small, curated "here's what's ready" moment with
   exactly three cards (current week/month/year).
   ========================================================== */

const WRAPPED_SEEN_KEY = 'fastapp-wrapped-seen';
const WRAPPED_TYPE_NOUN = { week: 'Week', month: 'Month', year: 'Year' };

let wrappedAvailableData = [];
let wrappedSlides = [];
let wrappedSlideIndex = 0;

function getWrappedSeen() {
    try { return JSON.parse(localStorage.getItem(WRAPPED_SEEN_KEY) || '{}'); }
    catch { return {}; }
}

function markWrappedSeen(type, label) {
    const seen = getWrappedSeen();
    seen[type] = label;
    localStorage.setItem(WRAPPED_SEEN_KEY, JSON.stringify(seen));
    updateWrappedReadyDot();
}

function updateWrappedReadyDot() {
    const seen = getWrappedSeen();
    const hasUnseen = wrappedAvailableData.some(w => seen[w.type ?? w.Type] !== (w.label ?? w.Label));
    const dot = document.getElementById('tb-wrapped-ready-dot');
    if (dot) dot.style.display = hasUnseen ? '' : 'none';
}

async function loadWrappedAvailable() {
    const cardsEl = document.getElementById('wrapped-panel-cards');
    try {
        const res = await fetch('/api/wrapped/available');
        const data = await res.json();
        wrappedAvailableData = data || [];

        if (wrappedAvailableData.length === 0) {
            cardsEl.innerHTML = `<div class="empty-state" style="padding:16px;">Nothing to wrap yet — check back once you've used your PC a bit this week.</div>`;
            document.getElementById('tb-wrapped-ready-dot').style.display = 'none';
            return;
        }

        cardsEl.innerHTML = wrappedAvailableData.map(w => {
            const type = w.type ?? w.Type;
            const label = w.label ?? w.Label;
            const teaser = w.teaser ?? w.Teaser;
            return `
                <div class="wrapped-card" data-type="${type}" onclick="openWrappedStory(this.dataset.type)">
                    <div>
                        <div class="wrapped-card-eyebrow">${escapeHtml(label)}</div>
                        <div class="wrapped-card-teaser">${escapeHtml(teaser)}</div>
                    </div>
                    <div class="wrapped-card-chev">&rarr;</div>
                </div>`;
        }).join('');

        updateWrappedReadyDot();
    } catch (err) {
        console.error('Failed to load Wrapped availability', err);
        if (cardsEl) cardsEl.innerHTML = `<div class="empty-state" style="padding:16px;">Couldn't load Wrapped.</div>`;
    }
}

function toggleWrappedPanel() {
    const panel = document.getElementById('wrapped-panel');
    const isOpen = panel.style.display !== 'none';
    panel.style.display = isOpen ? 'none' : '';
    if (!isOpen) loadWrappedAvailable();
}

function initWrappedPanelOutsideClick() {
    document.addEventListener('click', (e) => {
        const panel = document.getElementById('wrapped-panel');
        const btn = document.getElementById('tb-wrapped-btn');
        if (!panel || panel.style.display === 'none') return;
        if (panel.contains(e.target) || btn.contains(e.target)) return;
        panel.style.display = 'none';
    });
}

// --- Story ------------------------------------------------------------------

async function openWrappedStory(type) {
    document.getElementById('wrapped-panel').style.display = 'none';
    const overlay = document.getElementById('wrapped-overlay');
    overlay.style.display = 'flex';
    document.getElementById('wrapped-slide-body').innerHTML = `<div class="empty-state" style="border:none;background:none;">Loading…</div>`;

    try {
        const res = await fetch(`/api/wrapped?type=${type}`);
        const data = await res.json();
        if (data.error) throw new Error(data.error);

        wrappedSlides = buildWrappedSlides(type, data);
        wrappedSlideIndex = 0;
        renderWrappedSlide();

        markWrappedSeen(type, data.label ?? data.Label);
    } catch (err) {
        console.error('Failed to load Wrapped story', err);
        document.getElementById('wrapped-slide-body').innerHTML = `<div class="empty-state" style="border:none;background:none;">Couldn't load this recap.</div>`;
    }
}

function closeWrappedStory() {
    document.getElementById('wrapped-overlay').style.display = 'none';
    wrappedSlides = [];
}

function handleWrappedOverlayClick(event) {
    // Click on the dimmed backdrop itself (not the slide or the nav zones,
    // which stopPropagation) closes the story.
    if (event.target.id === 'wrapped-overlay') closeWrappedStory();
}

function wrappedNextSlide() {
    if (wrappedSlideIndex < wrappedSlides.length - 1) {
        wrappedSlideIndex++;
        renderWrappedSlide();
    } else {
        closeWrappedStory();
    }
}

function wrappedPrevSlide() {
    if (wrappedSlideIndex > 0) {
        wrappedSlideIndex--;
        renderWrappedSlide();
    }
}

function renderWrappedSlide() {
    const slide = wrappedSlides[wrappedSlideIndex];
    if (!slide) return;

    document.getElementById('wrapped-progress-row').innerHTML = wrappedSlides.map((s, i) => {
        const cls = i < wrappedSlideIndex ? 'done' : i === wrappedSlideIndex ? 'active' : '';
        return `<div class="progress-seg ${cls}"></div>`;
    }).join('');

    // innerHTML, not textContent: slide.eyebrow deliberately contains the
    // "&middot;" entity (textContent would print it as literal, undecoded
    // text). Safe here since it's built entirely from backend-computed
    // date/label strings, never from an app name or window title.
    document.getElementById('wrapped-slide-eyebrow').innerHTML = slide.eyebrow;
    document.getElementById('wrapped-slide-counter').textContent = `${wrappedSlideIndex + 1} / ${wrappedSlides.length}`;
    document.getElementById('wrapped-slide-body').innerHTML = slide.body;
}

// Each builder returns { eyebrow, body } for one slide. All five share the
// same shape (headline, big hero number, optional caption/chip/foot cards)
// established by the approved mockup, just with different content per slide.
function buildWrappedSlides(type, data) {
    const noun = WRAPPED_TYPE_NOUN[type] || 'Period';
    // CSS handles the uppercase styling (.wrapped-slide .eyebrow) -- doing it
    // here in JS as well would also uppercase the "&middot;" entity into
    // "&MIDDOT;", which browsers don't recognize and print literally.
    const eyebrow = `${data.label ?? data.Label} &middot; ${(data.dateRange ?? data.DateRange) || ''}`;
    const elapsedSuffix = (data.isInProgress ?? data.IsInProgress) ? ' so far' : '';

    const timeOnPc = data.timeOnPc ?? data.TimeOnPc ?? {};
    const focusQuality = data.focusQuality ?? data.FocusQuality ?? {};
    const topApp = data.topApp ?? data.TopApp;
    const peakDay = data.peakDay ?? data.PeakDay;
    const totalFocusedHours = data.totalFocusedHours ?? data.TotalFocusedHours ?? 0;

    const slides = [];

    // 1. Cover
    slides.push({
        eyebrow,
        body: `
            <div class="headline">Your <em>${noun}</em>, Wrapped.</div>
            <div class="hero">
                <div class="hero-number">${formatHoursWhole(totalFocusedHours)}</div>
                <div class="hero-caption">hours focused${elapsedSuffix}</div>
            </div>`
    });

    // 2. Time on PC
    const pctOfPeriod = timeOnPc.pctOfPeriod ?? timeOnPc.PctOfPeriod ?? 0;
    const hours = timeOnPc.hours ?? timeOnPc.Hours ?? 0;
    const periodTotalHours = timeOnPc.periodTotalHours ?? timeOnPc.PeriodTotalHours ?? 0;
    const deltaPct = timeOnPc.deltaPct ?? timeOnPc.DeltaPct;
    const deltaChip = deltaPct == null ? '' : `<div class="delta-chip ${deltaPct < 0 ? 'is-down' : ''}">${deltaPct >= 0 ? '&#9650;' : '&#9660;'} ${Math.abs(deltaPct)}% vs last ${noun.toLowerCase()}</div>`;
    slides.push({
        eyebrow,
        body: `
            <div class="headline">This ${noun.toLowerCase()}, your PC was on for <em>${pctOfPeriod}%</em> of your life.</div>
            <div class="hero">
                <div class="hero-number">${pctOfPeriod}<sup>%</sup></div>
                <div class="hero-caption">${formatTime(hours * 60)} of the ${noun.toLowerCase()}'s ${Math.round(periodTotalHours)} hours</div>
                ${deltaChip}
            </div>`
    });

    // 3. Focus quality
    const pctFocused = focusQuality.pctFocused ?? focusQuality.PctFocused ?? 0;
    const focusedHours = focusQuality.focusedHours ?? focusQuality.FocusedHours ?? 0;
    const rank = focusQuality.rank ?? focusQuality.Rank;
    const totalPeriods = focusQuality.totalPeriods ?? focusQuality.TotalPeriods;
    slides.push({
        eyebrow,
        body: `
            <div class="headline">Of that time, <em>${pctFocused}%</em> was spent actually focused.</div>
            <div class="hero">
                <div class="hero-number">${pctFocused}<sup>%</sup></div>
                <div class="hero-caption">${formatTime(focusedHours * 60)} actively focused</div>
            </div>
            <div class="foot" style="grid-template-columns:1fr;">
                <div class="foot-card">
                    <div class="foot-label">Rank this ${type === 'week' ? 'year' : 'span'}</div>
                    <div class="foot-value">#${rank ?? '—'} of ${totalPeriods ?? '—'} ${noun.toLowerCase()}s</div>
                </div>
            </div>`
    });

    // 4. Top app
    if (topApp) {
        const appName = topApp.appName ?? topApp.AppName;
        const minutes = topApp.minutes ?? topApp.Minutes ?? 0;
        const mover = topApp.mover ?? topApp.Mover;
        let moverHtml = '';
        if (mover) {
            const moverName = mover.appName ?? mover.AppName;
            const direction = mover.direction ?? mover.Direction;
            const moverDeltaPct = mover.deltaPct ?? mover.DeltaPct;
            const moverText = moverDeltaPct != null
                ? `${direction === 'up' ? '&#9650;' : '&#9660;'} ${moverDeltaPct}% vs last ${noun.toLowerCase()}`
                : (direction === 'up' ? 'New this ' + noun.toLowerCase() : 'Quiet this ' + noun.toLowerCase());
            moverHtml = `
                <div class="foot" style="grid-template-columns:1fr;">
                    <div class="foot-card">
                        <div class="foot-label">Biggest Mover</div>
                        <div class="foot-value ${direction === 'up' ? 'teal' : ''}">${escapeHtml(moverName)} &middot; ${moverText}</div>
                    </div>
                </div>`;
        }
        slides.push({
            eyebrow,
            body: `
                <div class="headline"><em>${escapeHtml(appName)}</em> was your top app.</div>
                <div class="hero">
                    <div class="hero-number" style="font-size:56px;">${escapeHtml(appName.charAt(0).toUpperCase())}</div>
                    <div class="hero-caption">${formatTime(minutes)} focused</div>
                </div>
                ${moverHtml}`
        });
    } else {
        slides.push({ eyebrow, body: `<div class="headline">No standout app this ${noun.toLowerCase()} yet.</div>` });
    }

    // 5. Close
    if (peakDay) {
        const dayName = peakDay.dayName ?? peakDay.DayName;
        const peakHours = peakDay.hours ?? peakDay.Hours ?? 0;
        slides.push({
            eyebrow,
            body: `
                <div class="headline">Your peak day was <em>${dayName}</em>.</div>
                <div class="hero">
                    <div class="hero-number">${formatHoursWhole(peakHours)}</div>
                    <div class="hero-caption">focused that day</div>
                </div>
                <div class="foot" style="grid-template-columns:1fr;">
                    <div class="foot-card">
                        <div class="foot-label">Total This ${noun}</div>
                        <div class="foot-value">${formatTime(totalFocusedHours * 60)}</div>
                    </div>
                </div>`
        });
    } else {
        slides.push({
            eyebrow,
            body: `
                <div class="headline">That's your ${noun.toLowerCase()} so far.</div>
                <div class="hero"><div class="hero-number">${formatHoursWhole(totalFocusedHours)}</div><div class="hero-caption">hours focused</div></div>`
        });
    }

    return slides;
}

function formatHoursWhole(hours) {
    const h = Math.floor(hours);
    const m = Math.round((hours - h) * 60);
    return m > 0 ? `${h}<span style="font-size:0.4em;">h</span> ${m}<span style="font-size:0.4em;">m</span>` : `${h}<span style="font-size:0.4em;">h</span>`;
}
