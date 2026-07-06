using System;
using System.Drawing;
using System.Windows.Forms;

namespace PowerTrader.Hub
{
    /// <summary>
    /// Owner-drawn coin signal tile: two 7-segment bars (LONG=blue, SHORT=orange) filled from the
    /// bottom up to the current level, plus a trade-start marker line. Mirrors NeuralSignalTile.
    /// </summary>
    internal sealed class NeuralTile : Control
    {
        private const int Levels = 7;      // display levels 1..7
        private int _long, _short;
        private int _tradeStart = 3;
        public string Coin { get; }

        public NeuralTile(string coin, int tradeStart)
        {
            Coin = coin;
            _tradeStart = Math.Max(1, Math.Min(tradeStart, Levels));
            Width = 78; Height = 96;
            DoubleBuffered = true;
            BackColor = Theme.Panel2;
            ForeColor = Theme.Fg;
        }

        public void SetValues(int longSig, int shortSig)
        {
            _long = Math.Max(0, Math.Min(longSig, Levels));
            _short = Math.Max(0, Math.Min(shortSig, Levels));
            Invalidate();
        }

        public void SetTradeStart(int level)
        {
            _tradeStart = Math.Max(1, Math.Min(level, Levels));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            using (var pen = new Pen(Theme.Border))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            using (var titleFont = new Font(FontFamily.GenericSansSerif, 8f, FontStyle.Bold))
            using (var valFont = new Font(FontFamily.GenericSansSerif, 7.5f))
            using (var fg = new SolidBrush(Theme.Fg))
            {
                var titleSize = g.MeasureString(Coin, titleFont);
                g.DrawString(Coin, titleFont, fg, (Width - titleSize.Width) / 2, 2);

                int barW = 12, gap = 16;
                int top = 20, bottom = Height - 18;
                int barH = bottom - top;
                int x0 = (Width - (barW * 2 + gap)) / 2;
                int x2 = x0 + barW + gap;

                DrawBar(g, x0, top, barW, barH, _long, Theme.Long);
                DrawBar(g, x2, top, barW, barH, _short, Theme.Short);

                // trade-start marker line
                int k = Math.Max(0, Math.Min(_tradeStart - 1, Levels));
                int y = (int)Math.Round(bottom - (k * (double)barH / Levels));
                using (var mp = new Pen(Theme.Fg, 2))
                {
                    g.DrawLine(mp, x0, y, x0 + barW, y);
                    g.DrawLine(mp, x2, y, x2 + barW, y);
                }

                string val = $"L:{_long} S:{_short}";
                var vs = g.MeasureString(val, valFont);
                g.DrawString(val, valFont, fg, (Width - vs.Width) / 2, Height - 16);
            }
        }

        private static void DrawBar(Graphics g, int x, int top, int w, int h, int level, Color fill)
        {
            using (var basePen = new Pen(Theme.Border))
            using (var baseBrush = new SolidBrush(Theme.Panel))
            using (var fillBrush = new SolidBrush(fill))
            {
                for (int seg = 0; seg < Levels; seg++)
                {
                    int yTop = (int)Math.Round(top + h - ((seg + 1) * (double)h / Levels));
                    int yBot = (int)Math.Round(top + h - (seg * (double)h / Levels));
                    var rect = new Rectangle(x, yTop, w, yBot - yTop);
                    bool on = level >= (seg + 1);
                    g.FillRectangle(on ? fillBrush : baseBrush, rect);
                    g.DrawRectangle(basePen, rect);
                }
            }
        }
    }
}
