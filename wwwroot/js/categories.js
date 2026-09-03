/* ---------------------------------------------------------------------------
   Category colours, shared by the dashboard and the desktop palette.

   These lived in the dashboard's utils.js, so the palette carried its own
   five-entry copy and rendered the other six categories -- Music, Media
   Production, Productivity, Fun, Education, Utilities -- as the same grey it
   uses for "Other". Two lists of the same thing, one of them short.

   Loaded before utils.js on the dashboard and before palette.js in the desktop
   window; both are classic scripts sharing one global scope.
   --------------------------------------------------------------------------- */

const categoryColors = {
    'Development': '#8B7CFF',
    'Gaming': '#4E4599',
    'Productivity': '#E8A33D',
    'Browsing': '#34D3C4',
    'Communication': '#1D766D',
    'Media Production': '#FF6B6B',
    'Music': '#8C3A3A',
    'Fun': '#FF9F6B',
    'Education': '#34D3C4',
    'Utilities': '#5B5F71',
    'Other': '#3A3D4A'
};

function catColor(cat) { return categoryColors[cat] || categoryColors['Other']; }

/* The colour as a wash rather than a fill. Letter avatars used to tint the
   GLYPH with the category colour, which put dark hues straight onto a dark
   ground -- "Other" measured 1.73:1 and Gaming 2.35:1. A translucent
   background with light text keeps the colour coding and the letter stays
   readable whichever category it is. */
function catTint(cat) { return catColor(cat) + '2E'; }

function avatarStyle(cat) {
    const c = catColor(cat);
    return `background:${c}2E;border-color:${c}80;color:var(--text)`;
}

/* The same category colour, lightened until it is readable as small text.

   The palette is built for FILLS -- swatches, bars, timeline blocks -- where a
   dark violet or teal sits happily on a dark page. Used as 11px label text the
   same values land at 2.5:1: Gaming (#4E4599) and Communication (#1D766D) are
   simply too dark to read. Mixing toward white in small steps keeps the hue
   recognisably the category's while clearing the 4.5:1 body-text threshold. */
const CAT_TEXT_TARGET_RATIO = 4.5;
const CAT_TEXT_BACKDROP = [0x14, 0x16, 0x1C];   // a row surface over --bg
const _catTextCache = {};

function catTextColor(cat) {
    if (_catTextCache[cat]) return _catTextCache[cat];

    const hex = catColor(cat);
    let rgb = [1, 3, 5].map(i => parseInt(hex.substr(i, 2), 16));
    const lin = (v) => { const c = v / 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
    const lum = (c) => 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]);
    const ratio = (a, b) => { const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
                              return (hi + 0.05) / (lo + 0.05); };

    // Up to 20 steps of 6% toward white -- enough to lift the darkest entry in
    // the palette, and it stops as soon as the threshold is met so lighter
    // categories keep their colour untouched.
    for (let i = 0; i < 20 && ratio(rgb, CAT_TEXT_BACKDROP) < CAT_TEXT_TARGET_RATIO; i++) {
        rgb = rgb.map(v => Math.round(v + (255 - v) * 0.06));
    }
    const out = `rgb(${rgb[0]}, ${rgb[1]}, ${rgb[2]})`;
    _catTextCache[cat] = out;
    return out;
}
