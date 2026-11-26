using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Replenishment
{
    internal sealed class TrendPanel : Panel
    {
        private Forecast forecast;
        private int safety;

        internal TrendPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
        }

        internal void Set(Forecast next, int limit)
        {
            forecast = next;
            safety = limit;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            var graphics = args.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var title = new Font("Microsoft YaHei UI", 12, FontStyle.Bold))
            using (var ink = new SolidBrush(Theme.Ink))
                graphics.DrawString("库存趋势", title, ink, 13, 11);
            if (forecast == null || Width < 180) return;
            var known = forecast.Months.Where(month => month.Closing.HasValue).ToList();
            using (var muted = new SolidBrush(Theme.Muted))
            {
                if (known.Count == 0)
                {
                    graphics.DrawString("录入销量后显示各月期末库存。", Theme.Normal, muted, 16, 65);
                    return;
                }
                graphics.DrawString("期末库存", Theme.Small, muted, 30, 43);
                graphics.DrawString("安全库存", Theme.Small, muted, 111, 43);
                int left = 23, right = Width - 26, top = 84, bottom = Height - 51;
                if (bottom <= top) return;
                var max = Math.Max(Math.Max(safety, known.Max(month => month.Closing.Value)), 1);
                Func<long, float> y = value => bottom - (float)((decimal)value / max) * (bottom - top);
                float spacing = (float)(right - left) / Math.Max(1, forecast.Months.Count - 1);
                using (var guide = new Pen(Theme.Line))
                using (var line = new Pen(Theme.Green, 2.5f))
                using (var limit = new Pen(Color.FromArgb(172, 115, 44), 1.4f) { DashStyle = DashStyle.Dash })
                using (var point = new SolidBrush(Theme.Green))
                {
                    graphics.DrawLine(line, 14, 51, 28, 51);
                    graphics.DrawLine(limit, 95, 51, 109, 51);
                    graphics.DrawLine(guide, left, bottom, right, bottom);
                    graphics.DrawLine(limit, left, y(safety), right, y(safety));
                    var points = known.Select((month, i) => new PointF(left + spacing * i, y(month.Closing.Value))).ToArray();
                    if (points.Length > 1) graphics.DrawLines(line, points);
                    foreach (var item in points) graphics.FillEllipse(point, item.X - 3, item.Y - 3, 6, 6);
                    for (int i = 0; i < forecast.Months.Count; i++)
                    {
                        string mark = forecast.Months[i].Month.ToString("M月");
                        graphics.DrawString(mark, Theme.Small, muted, left + spacing * i - 12, bottom + 8);
                    }
                }
            }
        }
    }
}
