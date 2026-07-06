using System.Drawing;
using System.Windows.Forms;

namespace PowerTrader.Hub
{
    /// <summary>Dark theme colors mirroring the Tkinter hub's palette.</summary>
    internal static class Theme
    {
        public static readonly Color Bg = ColorTranslator.FromHtml("#070B10");
        public static readonly Color Bg2 = ColorTranslator.FromHtml("#0B1220");
        public static readonly Color Panel = ColorTranslator.FromHtml("#0E1626");
        public static readonly Color Panel2 = ColorTranslator.FromHtml("#121C2F");
        public static readonly Color Border = ColorTranslator.FromHtml("#243044");
        public static readonly Color Fg = ColorTranslator.FromHtml("#C7D1DB");
        public static readonly Color Muted = ColorTranslator.FromHtml("#8B949E");
        public static readonly Color Accent = ColorTranslator.FromHtml("#00FF66");
        public static readonly Color Accent2 = ColorTranslator.FromHtml("#00E5FF");
        public static readonly Color Long = Color.RoyalBlue;
        public static readonly Color Short = Color.DarkOrange;
        public static readonly Color Green = Color.FromArgb(0, 200, 90);
        public static readonly Color Red = Color.FromArgb(230, 70, 70);

        public static void Apply(Control root)
        {
            root.BackColor = Bg;
            root.ForeColor = Fg;
            Style(root);
        }

        private static void Style(Control c)
        {
            foreach (Control child in c.Controls)
            {
                switch (child)
                {
                    case Button b:
                        b.BackColor = Panel2; b.ForeColor = Fg; b.FlatStyle = FlatStyle.Flat;
                        b.FlatAppearance.BorderColor = Border;
                        break;
                    case TextBox tb:
                        tb.BackColor = Panel; tb.ForeColor = Fg; tb.BorderStyle = BorderStyle.FixedSingle;
                        break;
                    case ComboBox cb:
                        cb.BackColor = Panel; cb.ForeColor = Fg; cb.FlatStyle = FlatStyle.Flat;
                        break;
                    case ListBox lb:
                        lb.BackColor = Panel; lb.ForeColor = Fg; lb.BorderStyle = BorderStyle.FixedSingle;
                        break;
                    case Label lbl:
                        lbl.ForeColor = Fg; lbl.BackColor = Color.Transparent;
                        break;
                    case Panel p:
                        p.BackColor = c.BackColor;
                        break;
                    case GroupBox gb:
                        gb.ForeColor = Fg;
                        break;
                    case CheckBox chk:
                        chk.ForeColor = Fg;
                        break;
                }
                Style(child);
            }
        }
    }
}
