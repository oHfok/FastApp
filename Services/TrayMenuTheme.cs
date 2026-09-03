using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FastApp.Services
{
    /// <summary>
    /// Dresses a WinForms ContextMenuStrip in the same design system as the
    /// palette and the dashboard.
    ///
    /// This is the last piece of stock Windows chrome the app shows. Everything
    /// else moved to the shared tokens in 2.0, so a bright grey menu with a
    /// checkbox gutter was the one surface still announcing that FastApp is a
    /// WinForms tray app underneath, and it is the surface people see most,
    /// because it is how you reach everything without the hotkey.
    ///
    /// The colours are the token values transcribed, not re-picked: GDI+ cannot
    /// lay an alpha wash over an unknown backdrop the way CSS can, so the two
    /// translucent tokens are composited against --panel-solid here and written
    /// as the flat results. The rest are copied exactly.
    /// </summary>
    internal static class TrayMenuTheme
    {
        // Two palettes rather than one, because this menu is drawn by hand and
        // cannot inherit a theme the way the web surfaces do. Properties rather
        // than readonly fields: the theme can change while the app is running,
        // and a field captured at class load would keep the colours it started
        // with.
        private static bool Light => SystemTheme.IsLight;

        // --panel-solid, the menu surface
        private static Color Surface => Light
            ? Color.FromArgb(0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x14, 0x16, 0x1F);
        // --panel-border rgba(255,255,255,0.08) over that surface
        private static Color Border => Light
            ? Color.FromArgb(0xE0, 0xDE, 0xD9)
            : Color.FromArgb(0x2B, 0x2D, 0x35);
        // --panel-border-soft, for the dividers
        private static Color Divider => Light
            ? Color.FromArgb(0xEC, 0xEA, 0xE6)
            : Color.FromArgb(0x23, 0x25, 0x2C);
        // --text and --text-faint
        private static Color Text => Light
            ? Color.FromArgb(0x16, 0x17, 0x1D)
            : Color.FromArgb(0xF3, 0xF1, 0xEA);
        private static Color TextFaint => Light
            ? Color.FromArgb(0x63, 0x68, 0x7A)
            : Color.FromArgb(0x7C, 0x81, 0x94);
        // --brass, and --brass at 0.12 over the surface: focus, and only focus
        // Darker on light, where the vivid brass measures 2:1 as text.
        private static Color Brass => Light
            ? Color.FromArgb(0x8A, 0x63, 0x21)
            : Color.FromArgb(0xE8, 0xA3, 0x3D);
        private static Color BrassWash => Light
            ? Color.FromArgb(0xF7, 0xEC, 0xDC)
            : Color.FromArgb(0x2D, 0x27, 0x23);

        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Takes a ToolStripDropDownMenu rather than a ContextMenuStrip, which
        /// is its subclass: a submenu's DropDown is the base type, so typing
        /// this to ContextMenuStrip meant `item.DropDown as ContextMenuStrip`
        /// silently produced null and the first property set threw -- inside the
        /// tray constructor, which runs before anything else in the app.
        /// </summary>
        public static void Apply(ToolStripDropDownMenu menu)
        {
            if (menu == null) return;

            menu.RenderMode = ToolStripRenderMode.Professional;
            menu.Renderer = new DarkRenderer();
            menu.BackColor = Surface;
            menu.ForeColor = Text;
            menu.Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);

            // No item carries an icon, and the gutter Windows reserves for one
            // is the single most dated thing about the default menu.
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.DropShadowEnabled = true;

            // Note for anyone tempted to set menu.Padding here: a drop-down menu
            // recomputes its own padding from the margins it is showing, on
            // every layout, so an assignment is silently discarded. The breathing
            // room comes from the items instead.

            // Rounded like every other surface the app draws, using the same
            // compositor attribute as the palette window. A drop-down is a real
            // top-level window, so it takes the attribute the same way.
            menu.HandleCreated += (s, e) => Round(menu.Handle);
            if (menu.IsHandleCreated) Round(menu.Handle);
        }

        /// <summary>
        /// A row. Padded here rather than at each call site so the vertical
        /// rhythm is set in one place. Only the vertical half of that padding
        /// takes effect -- a drop-down positions item text from its own margins
        /// and ignores the horizontal -- which is also why the highlight below
        /// is measured from the window rather than from the item.
        /// </summary>
        public static ToolStripMenuItem Item(string text, EventHandler onClick, string shortcut = null)
        {
            var item = new ToolStripMenuItem(text) { Padding = new Padding(6, 4, 6, 4) };
            if (onClick != null) item.Click += onClick;
            if (!string.IsNullOrEmpty(shortcut))
            {
                // A display string with no real ShortcutKeys behind it: the
                // combination is served by the global hook, not by the menu, so
                // this is a label rather than a binding.
                item.ShortcutKeyDisplayString = shortcut;
                item.ShowShortcutKeys = true;
            }
            return item;
        }

        /// <summary>
        /// The status line. A disabled item rather than a ToolStripLabel so it
        /// keeps the same text inset as the rows beneath it, and so keyboard
        /// navigation skips it for free.
        /// </summary>
        public static ToolStripMenuItem Header(string text) =>
            new ToolStripMenuItem(text)
            {
                Enabled = false,
                Font = new Font("Consolas", 8.25f, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(6, 3, 6, 3)
            };

        private static void Round(IntPtr hwnd)
        {
            try
            {
                int preference = DwmwcpRound;
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            }
            catch
            {
                // Windows 10 has no such attribute; square corners are the only
                // cost, so this is not worth surfacing.
            }
        }

        // How far the highlight sits inside the menu's edges. Four leaves the
        // label just inside the rounded cap, which is as tight as it can be
        // before the two touch.
        private const int Inset = 4;

        private static GraphicsPath Pill(Rectangle bounds)
        {
            // --radius-pill: anything interactive is fully rounded. At menu-row
            // height that makes the radius simply half the height, unless the
            // menu is somehow narrower than a row is tall, in which case the
            // arcs would overlap and draw a bow tie.
            int radius = Math.Max(2, Math.Min(bounds.Height, bounds.Width) / 2);
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Covers the parts of the professional renderer not worth overriding by
        /// hand, chiefly the margins, which have to match the surface or they
        /// draw as a pale stripe down the side.
        /// </summary>
        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Surface;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => BrassWash;
            public override Color MenuItemSelected => BrassWash;
            public override Color MenuItemSelectedGradientBegin => BrassWash;
            public override Color MenuItemSelectedGradientEnd => BrassWash;
            public override Color MenuItemPressedGradientBegin => BrassWash;
            public override Color MenuItemPressedGradientMiddle => BrassWash;
            public override Color MenuItemPressedGradientEnd => BrassWash;
            public override Color ImageMarginGradientBegin => Surface;
            public override Color ImageMarginGradientMiddle => Surface;
            public override Color ImageMarginGradientEnd => Surface;
            public override Color SeparatorDark => Divider;
            public override Color SeparatorLight => Divider;
        }

        private sealed class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using var brush = new SolidBrush(Surface);
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                // Drawn inside the bounds so the compositor clips the ends of
                // the line at the corners rather than leaving them hanging.
                var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
                bounds.Width -= 1;
                bounds.Height -= 1;
                using var pen = new Pen(Border);
                e.Graphics.DrawRectangle(pen, bounds);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;

                // Deliberately not e.Item.Width: a drop-down lays its items out
                // wider than the window and lets them clip, so measuring the
                // item put the right end of the pill past the border. Measure
                // the window and inset evenly, which also keeps the pill
                // centred -- the content rectangle is itself lopsided (eight
                // pixels of margin on the left, none on the right), so following
                // that instead left the label sitting outside the rounded cap.
                //
                // The graphics origin is the item, so the window has to be
                // expressed relative to it.
                var bounds = new Rectangle(
                    Inset - e.Item.Bounds.Left,
                    1,
                    e.ToolStrip.Width - Inset * 2,
                    e.Item.Height - 2);
                if (bounds.Width <= 0 || bounds.Height <= 0) return;

                var previous = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Pill(bounds))
                using (var brush = new SolidBrush(BrassWash))
                {
                    e.Graphics.FillPath(brush, path);
                }
                e.Graphics.SmoothingMode = previous;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                // A menu item with a shortcut paints in two passes: the label,
                // then the shortcut right-aligned into the same rectangle. The
                // second pass is the one flagged Right, and giving it the faint
                // colour is what stops a hint you already know from competing
                // with the thing you came to click.
                bool shortcut = (e.TextFormat & TextFormatFlags.Right) != 0;

                // Brass means focus and nothing else, which here is the row
                // under the pointer.
                e.TextColor = shortcut || !e.Item.Enabled ? TextFaint
                            : e.Item.Selected ? Brass
                            : Text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using var pen = new Pen(Divider);
                e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
            }
        }
    }
}
