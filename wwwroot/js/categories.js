/* ---------------------------------------------------------------------------
   Category colours, shared by the dashboard and the desktop palette.

   These lived in the dashboard's utils.js, so the palette carried its own
   five-entry copy and rendered the other six categories as the same grey it
   uses for "Other". Two lists of the same thing, one of them short.

   The palette is bright and evenly spread around the colour wheel, chosen to
   read as a FILL (bars, swatches, timeline blocks, avatar tints) on white and
   on near-black alike. As small text most of these miss 4.5:1 on one theme or
   the other, which is what catTextColor below is for.

   Loaded before utils.js on the dashboard and before palette.js in the desktop
   window; both are classic scripts sharing one global scope.
   --------------------------------------------------------------------------- */

const categoryColors = {
    'Development':      '#6366F1',
    'Gaming':           '#8B5CF6',
    'Productivity':     '#F59E0B',
    'Browsing':         '#14B8A6',
    'Communication':    '#0EA5E9',
    'Media Production': '#EC4899',
    'Music':            '#D946EF',
    'Fun':              '#F97316',
    'Education':        '#22C55E',
    'Utilities':        '#64748B',
    'Other':            '#94A3B8'
};

function catColor(cat) { return categoryColors[cat] || categoryColors['Other']; }

/* The colour as a soft wash rather than a fill. */
function catTint(cat) { return catColor(cat) + '2E'; }

/* Letter avatars: a faint category tint behind the initial, drawn in that
   category's own legible text colour. */
function avatarStyle(cat) {
    const c = catColor(cat);
    return `background:${c}22;border-color:${c}44;color:${catTextColor(cat)}`;
}

/* A category colour as small TEXT: walked toward black on a light surface and
   toward white on a dark one until it reads at 4.5:1, stopping as soon as it
   does so lighter hues stay put. The surface is read live from --surface, so a
   theme switch re-derives instead of serving a stale answer. */
const CAT_TEXT_TARGET_RATIO = 4.5;
const _catTextCache = {};

function _surfaceRgb() {
    const hex = getComputedStyle(document.documentElement).getPropertyValue('--surface').trim();
    const m = /^#?([0-9a-f]{6})$/i.exec(hex);
    return m ? [0, 2, 4].map(i => parseInt(m[1].substr(i, 2), 16)) : [255, 255, 255];
}

function catTextColor(cat) {
    const surface = _surfaceRgb();
    const key = cat + '|' + surface.join(',');
    if (_catTextCache[key]) return _catTextCache[key];

    const hex = catColor(cat);
    let rgb = [1, 3, 5].map(i => parseInt(hex.substr(i, 2), 16));
    const lin = (v) => { const c = v / 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
    const lum = (c) => 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]);
    const ratio = (a, b) => { const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x); return (hi + 0.05) / (lo + 0.05); };

    const toward = lum(surface) > 0.5 ? 0 : 255;   // away from the surface
    for (let i = 0; i < 24 && ratio(rgb, surface) < CAT_TEXT_TARGET_RATIO; i++) {
        rgb = rgb.map(v => Math.round(v + (toward - v) * 0.08));
    }
    return (_catTextCache[key] = `rgb(${rgb[0]}, ${rgb[1]}, ${rgb[2]})`);
}
