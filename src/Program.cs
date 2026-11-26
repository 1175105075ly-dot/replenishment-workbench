using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Replenishment
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainWindow());
        }
    }

    internal static class Theme
    {
        internal static readonly Color Ink = Color.FromArgb(23, 53, 45);
        internal static readonly Color Muted = Color.FromArgb(77, 104, 96);
        internal static readonly Color Dark = Color.FromArgb(20, 55, 47);
        internal static readonly Color Green = Color.FromArgb(29, 103, 85);
        internal static readonly Color Paper = Color.FromArgb(239, 246, 243);
        internal static readonly Color Input = Color.FromArgb(255, 246, 218);
        internal static readonly Color Line = Color.FromArgb(214, 230, 223);
        internal static readonly Color Danger = Color.FromArgb(145, 47, 34);
        internal static readonly Color DangerBg = Color.FromArgb(252, 226, 221);
        internal static readonly Color Warn = Color.FromArgb(120, 77, 15);
        internal static readonly Font Normal = new Font("Microsoft YaHei UI", 10);
        internal static readonly Font Small = new Font("Microsoft YaHei UI", 9);
        internal static readonly Font Bold = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);

        internal static Label Label(string text, float size = 10, bool bold = false, Color? color = null)
        {
            return new Label { Text = text, ForeColor = color ?? Ink, AutoEllipsis = true,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft };
        }

        internal static Button Button(string text, Action action, bool primary = false)
        {
            var button = new Button { Text = text, Height = 33, AutoSize = true, Font = Bold,
                Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat, Padding = new Padding(9, 0, 9, 0),
                BackColor = primary ? Green : Color.White, ForeColor = primary ? Color.White : Ink,
                UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = primary ? Green : Line;
            button.Click += (sender, args) => action();
            return button;
        }

        internal static DataGridView Grid(bool editable)
        {
            var grid = new DataGridView { Dock = DockStyle.Fill, RowHeadersVisible = false, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToResizeColumns = false,
                AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White, BorderStyle = BorderStyle.None, GridColor = Line,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                EnableHeadersVisualStyles = false, ColumnHeadersHeight = 32,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ReadOnly = !editable, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2, Font = Normal };
            grid.RowTemplate.Height = 27;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Dark, ForeColor = Color.White,
                Font = Bold, Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 3, 0) };
            grid.DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Ink, BackColor = Color.White,
                SelectionBackColor = Color.FromArgb(218, 238, 229), SelectionForeColor = Ink,
                Padding = new Padding(6, 0, 3, 0), NullValue = "" };
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(246, 250, 248);
            return grid;
        }

        internal static void Column(DataGridView grid, string header, float width, bool editable = false)
        {
            var column = new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = width,
                MinimumWidth = 55, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = !editable };
            if (editable) column.DefaultCellStyle.BackColor = Input;
            grid.Columns.Add(column);
        }
    }

    internal sealed class MainWindow : Form
    {
        private Plan plan = Plan.Create();
        private Forecast forecast;
        private string file, sessionKey;
        private bool loading, dirty;
        private readonly TextBox product = new TextBox(), adjustment = new TextBox();
        private readonly DateTimePicker baseline = new DateTimePicker();
        private readonly NumericUpDown onHand = Number(Forecast.UnitLimit), inbound = Number(Forecast.UnitLimit),
            safety = Number(Forecast.UnitLimit), production = Number(Forecast.LeadLimit), transit = Number(Forecast.LeadLimit);
        private readonly TabControl tabs = new TabControl();
        private readonly DataGridView batches = Theme.Grid(true), months = Theme.Grid(false),
            sales = Theme.Grid(true), outages = Theme.Grid(false);
        private readonly Label period = Theme.Label("", 10, false, Color.White), status = Theme.Label("", 9, false, Theme.Muted),
            source = Theme.Label("", 9, false, Theme.Muted);
        private readonly Label startMetric = Theme.Label("", 19, true), sentMetric = Theme.Label("", 19, true),
            finishMetric = Theme.Label("", 19, true), demandMetric = Theme.Label("", 17, true);
        private readonly Panel alertPanel = new Panel();
        private readonly TrendPanel chart = new TrendPanel();

        internal MainWindow()
        {
            Text = "补货预测工作台";
            Icon = Icon.ExtractAssociatedIcon(typeof(Program).Assembly.Location);
            StartPosition = FormStartPosition.CenterScreen;
            var screen = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(1240, screen.Width - 32), Math.Min(860, screen.Height - 54));
            MinimumSize = new Size(1000, 700);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Theme.Normal;
            BackColor = Theme.Paper;
            ForeColor = Theme.Ink;
            KeyPreview = true;
            BuildLayout();
            BindInputs();
            LoadPlan(Plan.Create(), null);
            FormClosing += (sender, args) => { if (!CanReplacePlan()) args.Cancel = true; };
            KeyDown += (sender, args) => {
                if (!args.Control) return;
                if (args.KeyCode == Keys.S) SavePlan(args.Shift);
                else if (args.KeyCode == Keys.O) OpenPlan();
                else if (args.KeyCode == Keys.N) NewPlan();
                else return;
                args.SuppressKeyPress = true;
            };
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Controls.Add(root);
            var hero = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Dark };
            root.Controls.Add(hero, 0, 0);
            var title = Theme.Label("补货预测工作台", 21, true, Color.White);
            title.SetBounds(20, 7, 380, 39);
            period.SetBounds(23, 46, 440, 23);
            hero.Controls.Add(title);
            hero.Controls.Add(period);
            var commands = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, Width = 558, Padding = new Padding(0, 18, 17, 0), BackColor = Theme.Dark };
            commands.Controls.Add(Theme.Button("从领星导入", Import, true));
            commands.Controls.Add(Theme.Button("另存为", () => SavePlan(true)));
            commands.Controls.Add(Theme.Button("保存方案", () => SavePlan(false)));
            commands.Controls.Add(Theme.Button("打开方案", OpenPlan));
            commands.Controls.Add(Theme.Button("新建产品", NewPlan));
            hero.Controls.Add(commands);
            root.Controls.Add(tabs, 0, 1);
            tabs.Dock = DockStyle.Fill;
            tabs.Margin = new Padding(13, 10, 13, 0);
            tabs.Padding = new Point(17, 5);
            tabs.Font = Theme.Bold;
            var controlTab = new TabPage("补货总览") { BackColor = Theme.Paper };
            var salesTab = new TabPage("销量录入") { BackColor = Theme.Paper };
            tabs.TabPages.Add(controlTab);
            tabs.TabPages.Add(salesTab);
            status.Dock = DockStyle.Fill;
            status.Margin = new Padding(20, 0, 16, 0);
            root.Controls.Add(status, 0, 2);
            BuildOverview(controlTab);
            BuildSales(salesTab);
        }

        private void BuildOverview(TabPage page)
        {
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 8, 10, 8) };
            page.Controls.Add(scroller);
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 4, Height = 685 };
            foreach (int height in new[] { 152, 75, 166, 292 })
                stack.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            scroller.Controls.Add(stack);
            var entry = Section("基础参数", "黄色格可编辑。库存基准日为当日库存快照，从次日开始预测。");
            stack.Controls.Add(entry, 0, 0);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2,
                Padding = new Padding(12, 1, 12, 4) };
            for (int i = 0; i < 4; i++) fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            entry.Controls.Add(fields, 0, 1);
            product.MaxLength = 100;
            adjustment.MaxLength = 20;
            baseline.Format = DateTimePickerFormat.Custom;
            baseline.CustomFormat = "yyyy-MM-dd";
            baseline.MinDate = Forecast.MinDate;
            baseline.MaxDate = Forecast.MaxBaseDate;
            var labels = new[] { "产品名称 / SKU", "库存基准日", "当前 FBA 可售（台）", "按可售计入的在途（台）",
                "安全库存（台）", "生产周期（自然日）", "发货至可售（自然日）", "销量同比调整（%）" };
            var controls = new Control[] { product, baseline, onHand, inbound, safety, production, transit, adjustment };
            for (int i = 0; i < controls.Length; i++) fields.Controls.Add(Field(labels[i], controls[i]), i % 4, i / 4);

            var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(1, 0, 1, 6) };
            for (int i = 0; i < 4; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            metrics.Controls.Add(Metric("起始可售库存", startMetric), 0, 0);
            metrics.Controls.Add(Metric("计划发货合计", sentMetric), 1, 0);
            metrics.Controls.Add(Metric("预测期末库存", finishMetric), 2, 0);
            metrics.Controls.Add(Metric("销量数据", demandMetric), 3, 0);
            stack.Controls.Add(metrics, 0, 1);

            var schedule = Section("发货安排", "可编辑批次、订购日和数量。日期格式 YYYY-MM-DD。同日到货合计。");
            stack.Controls.Add(schedule, 0, 2);
            Theme.Column(batches, "批次", 145, true);
            Theme.Column(batches, "计划订购日", 105, true);
            Theme.Column(batches, "计划发货日", 100);
            Theme.Column(batches, "预计 FBA 可售日", 113);
            Theme.Column(batches, "覆盖销量", 76);
            Theme.Column(batches, "最低需补", 75);
            Theme.Column(batches, "计划数量", 79, true);
            Theme.Column(batches, "覆盖期末库存", 98);
            Theme.Column(batches, "批次状态", 130);
            schedule.Controls.Add(batches, 0, 1);

            var results = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 67));
            results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            stack.Controls.Add(results, 0, 3);
            var stock = Section("月度库存预测", "库存和销量单位为台。月内最低库存用于判断预警。");
            foreach (var column in new[] { Tuple.Create("销售期间", 165f), Tuple.Create("期初", 56f),
                Tuple.Create("预计销量", 77f), Tuple.Create("新增可售", 74f), Tuple.Create("期末", 56f),
                Tuple.Create("月内最低", 77f), Tuple.Create("库存状态", 96f) })
                Theme.Column(months, column.Item1, column.Item2);
            stock.Controls.Add(months, 0, 1);
            results.Controls.Add(stock, 0, 0);
            var right = new Panel { Dock = DockStyle.Fill, Margin = new Padding(5, 1, 1, 6), BackColor = Color.White };
            results.Controls.Add(right, 1, 0);
            chart.Dock = DockStyle.Fill;
            alertPanel.Dock = DockStyle.Fill;
            var alertBlock = Section("预计断货与到货", "恢复可售当天不计入断货天数。");
            alertBlock.Margin = Padding.Empty;
            alertPanel.Controls.Add(alertBlock);
            Theme.Column(outages, "预计断货日", 100);
            Theme.Column(outages, "恢复可售日", 100);
            Theme.Column(outages, "断货天数", 87);
            alertBlock.Controls.Add(outages, 0, 1);
            right.Controls.Add(chart);
            right.Controls.Add(alertPanel);
        }

        private void BuildSales(TabPage page)
        {
            var block = Section("按年月录入销量",
                "有去年同期且填了同比调整时自动推算。其他月份手填整月销量。0 表示零销量，空白表示待补录。");
            block.Padding = new Padding(12);
            page.Controls.Add(block);
            Theme.Column(sales, "预测月份", 105);
            Theme.Column(sales, "去年同期月份", 120);
            Theme.Column(sales, "去年同期实际销量", 145, true);
            Theme.Column(sales, "手填整月预测", 135, true);
            Theme.Column(sales, "整月预测销量", 130);
            Theme.Column(sales, "区间预计销量", 130);
            Theme.Column(sales, "预测来源", 110);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.Controls.Add(sales, 0, 0);
            source.Dock = DockStyle.Fill;
            body.Controls.Add(source, 0, 1);
            block.Controls.Add(body, 0, 1);
        }

        private static TableLayoutPanel Section(string title, string hint)
        {
            var section = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(1, 1, 1, 6),
                BackColor = Color.White, ColumnCount = 1, RowCount = 2 };
            section.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var heading = Theme.Label(title, 12, true);
            heading.AutoSize = true;
            heading.Anchor = AnchorStyles.Left;
            heading.Margin = new Padding(11, 0, 9, 0);
            var note = Theme.Label(hint, 9, false, Theme.Muted);
            note.Dock = DockStyle.Fill;
            bar.Controls.Add(heading, 0, 0);
            bar.Controls.Add(note, 1, 0);
            section.Controls.Add(bar, 0, 0);
            return section;
        }

        private static TableLayoutPanel Field(string label, Control control)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Margin = new Padding(1, 0, 12, 0) };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 19));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var caption = Theme.Label(label, 9, false, Theme.Muted);
            caption.Dock = DockStyle.Fill;
            caption.Margin = Padding.Empty;
            control.Dock = DockStyle.Fill;
            control.BackColor = Theme.Input;
            control.ForeColor = Theme.Ink;
            control.Font = Theme.Normal;
            control.Margin = Padding.Empty;
            control.AccessibleName = label;
            panel.Controls.Add(caption, 0, 0);
            panel.Controls.Add(control, 0, 1);
            return panel;
        }

        private static Control Metric(string caption, Label value)
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White,
                Margin = new Padding(0, 0, 5, 0) };
            var name = Theme.Label(caption, 9, false, Theme.Muted);
            name.SetBounds(11, 5, 180, 21);
            value.SetBounds(10, 24, 190, 34);
            name.Anchor = value.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            panel.Controls.Add(name);
            panel.Controls.Add(value);
            return panel;
        }

        private static NumericUpDown Number(int max)
        {
            return new NumericUpDown { Maximum = max, Minimum = 0, ThousandsSeparator = true };
        }

        private void BindInputs()
        {
            product.TextChanged += (sender, args) => {
                if (loading) return;
                plan.Product = product.Text.Trim();
                Changed();
            };
            baseline.ValueChanged += (sender, args) => {
                if (loading) return;
                plan.BaseDate = Forecast.Iso(baseline.Value);
                Changed();
            };
            adjustment.TextChanged += (sender, args) => {
                if (loading) return;
                decimal? percent;
                if (!TryAdjustment(out percent))
                {
                    adjustment.BackColor = Theme.DangerBg;
                    status.Text = "同比调整请输入不低于 −100 的百分数，或留空。";
                    return;
                }
                adjustment.BackColor = Theme.Input;
                plan.AdjustmentPercent = percent;
                Changed();
            };
            BindNumber(onHand, value => plan.OnHand = value);
            BindNumber(inbound, value => plan.CountedInTransit = value);
            BindNumber(safety, value => plan.SafetyStock = value);
            BindNumber(production, value => plan.ProductionDays = value);
            BindNumber(transit, value => plan.TransitDays = value);
            batches.CellValidating += ValidateGrid;
            sales.CellValidating += ValidateGrid;
            batches.CellEndEdit += CommitGrid;
            sales.CellEndEdit += CommitGrid;
        }

        private bool TryAdjustment(out decimal? value)
        {
            value = null;
            if (String.IsNullOrWhiteSpace(adjustment.Text)) return true;
            decimal amount;
            var input = adjustment.Text.Trim().TrimEnd('%', '％').Replace('−', '-');
            if (!Decimal.TryParse(input, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out amount) || amount < -100m || amount > Forecast.MaximumAdjustment) return false;
            value = amount;
            return true;
        }

        private void BindNumber(NumericUpDown input, Action<int> assign)
        {
            input.ValueChanged += (sender, args) => {
                if (loading) return;
                assign(Decimal.ToInt32(input.Value));
                Changed();
            };
        }

        private static bool TryQuantity(string text, out int? quantity)
        {
            quantity = null;
            if (String.IsNullOrWhiteSpace(text)) return true;
            int parsed;
            if (!Int32.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out parsed) || parsed < 0 || parsed > Forecast.UnitLimit) return false;
            quantity = parsed;
            return true;
        }

        private void ValidateGrid(object sender, DataGridViewCellValidatingEventArgs args)
        {
            if (loading || args.RowIndex < 0 || args.ColumnIndex < 0) return;
            var grid = (DataGridView)sender;
            if (grid.Columns[args.ColumnIndex].ReadOnly) return;
            var text = Convert.ToString(args.FormattedValue).Trim();
            int? quantity;
            DateTime date;
            bool valid = grid == batches && args.ColumnIndex == 0
                ? text.Length <= 100
                : grid == batches && args.ColumnIndex == 1
                    ? text.Length == 0 || Forecast.TryDate(text, out date)
                    : TryQuantity(text, out quantity);
            args.Cancel = !valid;
            grid.Rows[args.RowIndex].ErrorText = valid ? "" : "请输入有效日期或非负整数。";
            if (!valid) status.Text = grid == batches && args.ColumnIndex == 1
                ? "日期格式示例 2027-01-15，也可留空。"
                : "请检查输入。数量可填 0 或留空，批次名称最多 100 个字符。";
        }

        private void CommitGrid(object sender, DataGridViewCellEventArgs args)
        {
            if (loading || args.RowIndex < 0 || args.ColumnIndex < 0) return;
            var grid = (DataGridView)sender;
            if (grid.Columns[args.ColumnIndex].ReadOnly) return;
            var row = grid.Rows[args.RowIndex];
            row.ErrorText = "";
            var text = Convert.ToString(row.Cells[args.ColumnIndex].Value).Trim();
            int? quantity;
            if (grid == batches)
            {
                var shipment = plan.Shipments[args.RowIndex];
                if (args.ColumnIndex == 0) shipment.Name = text;
                else if (args.ColumnIndex == 1)
                {
                    DateTime date;
                    shipment.OrderDate = Forecast.TryDate(text, out date) ? Forecast.Iso(date) : "";
                }
                else if (TryQuantity(text, out quantity)) shipment.Quantity = quantity;
            }
            else if (TryQuantity(text, out quantity))
            {
                DateTime month = (DateTime)row.Tag;
                bool historical = args.ColumnIndex == 2;
                plan.SetSales(historical ? month.AddYears(-1) : month, quantity, historical);
            }
            dirty = true;
            BeginInvoke((Action)(() => { if (!IsDisposed) RefreshForecast(); }));
        }

        private void Changed()
        {
            dirty = true;
            RefreshForecast();
        }

        private void LoadPlan(Plan next, string filename)
        {
            loading = true;
            try
            {
                plan = next;
                file = filename;
                product.Text = plan.Product ?? "";
                DateTime date;
                Forecast.TryDate(plan.BaseDate, out date);
                baseline.Value = date;
                onHand.Value = plan.OnHand;
                inbound.Value = plan.CountedInTransit;
                safety.Value = plan.SafetyStock;
                production.Value = plan.ProductionDays;
                transit.Value = plan.TransitDays;
                adjustment.Text = plan.AdjustmentPercent.HasValue
                    ? plan.AdjustmentPercent.Value.ToString("0.############", CultureInfo.InvariantCulture) : "";
                adjustment.BackColor = Theme.Input;
                tabs.SelectedIndex = 0;
                dirty = false;
            }
            finally { loading = false; }
            RefreshForecast();
        }

        private void RefreshForecast()
        {
            forecast = Forecast.Calculate(plan);
            loading = true;
            try
            {
                period.Text = Forecast.Iso(forecast.Start) + "  至  " + Forecast.Iso(forecast.End);
                Text = "补货预测工作台" + (String.IsNullOrWhiteSpace(plan.Product) ? "" : " · " + plan.Product) + (dirty ? " *" : "");
                startMetric.Text = Qty(forecast.Opening) + " 台";
                sentMetric.Text = Qty(forecast.Planned) + " 台";
                finishMetric.Text = forecast.Closing.HasValue ? Qty(forecast.Closing) + " 台" : "待补录";
                finishMetric.ForeColor = forecast.Closing < 0 ? Theme.Danger : Theme.Ink;
                demandMetric.Text = forecast.MissingMonths == 0 ? "已齐全" : forecast.MissingMonths + " 个月待填";
                demandMetric.ForeColor = forecast.MissingMonths == 0 ? Theme.Green : Theme.Warn;
                SourceLabel();

                while (batches.Rows.Count < 3) batches.Rows.Add();
                for (int i = 0; i < plan.Shipments.Count; i++)
                {
                    var batch = plan.Shipments[i];
                    var prediction = forecast.Shipments[i];
                    Cells(batches.Rows[i], batch.Name, batch.OrderDate, DateText(prediction.ShipDate),
                        DateText(prediction.AvailableDate), prediction.MissingDemand ? "待补录" : Qty(prediction.Coverage),
                        prediction.MissingDemand ? "待补录" : Qty(prediction.Needed), Qty(batch.Quantity),
                        prediction.MissingDemand ? "待补录" : Qty(prediction.EndStock), prediction.Status);
                    foreach (var input in new[] { 0, 1, 6 }) batches.Rows[i].Cells[input].Style.BackColor = Theme.Input;
                    ColorStatus(batches.Rows[i].Cells[8], prediction.Status);
                }
                months.Rows.Clear();
                while (sales.Rows.Count > forecast.Months.Count) sales.Rows.RemoveAt(sales.Rows.Count - 1);
                while (sales.Rows.Count < forecast.Months.Count) sales.Rows.Add();
                for (int i = 0; i < forecast.Months.Count; i++)
                {
                    var month = forecast.Months[i];
                    var line = month.Start.Day == 1 ? Forecast.MonthKey(month.Month)
                        : Forecast.Iso(month.Start) + " 至 " + month.End.ToString("MM-dd");
                    int index = months.Rows.Add(line, Qty(month.Opening), Qty(month.Demand), Qty(month.Arrivals),
                        Qty(month.Closing), Qty(month.Minimum), month.Status);
                    ColorStatus(months.Rows[index].Cells[6], month.Status);
                    var row = sales.Rows[i];
                    row.Tag = month.Month;
                    Cells(row, Forecast.MonthKey(month.Month), Forecast.MonthKey(month.Month.AddYears(-1)),
                        Qty(month.Actual), Qty(month.Manual), Qty(month.FullDemand), Qty(month.Demand), month.Source);
                    row.Cells[2].Style.BackColor = row.Cells[3].Style.BackColor = Theme.Input;
                    ColorStatus(row.Cells[6], month.Source);
                }
                months.ClearSelection();
                outages.Rows.Clear();
                foreach (var outage in forecast.Outages)
                {
                    int index = outages.Rows.Add(Forecast.Iso(outage.Start),
                        outage.Recovery.HasValue ? DateText(outage.Recovery) : outage.DemandMissing ? "待补录" : "待确认",
                        (outage.Recovery.HasValue ? "" : "至少 ") + outage.Days + " 天");
                    outages.Rows[index].DefaultCellStyle.BackColor = Theme.DangerBg;
                    outages.Rows[index].DefaultCellStyle.ForeColor = Theme.Danger;
                }
                outages.ClearSelection();
                chart.Set(forecast, plan.SafetyStock);
                alertPanel.Visible = forecast.Outages.Count > 0;
                chart.Visible = forecast.Outages.Count == 0;
                if (alertPanel.Visible) alertPanel.BringToFront();
                status.Text = dirty ? "有未保存的修改 · Ctrl+S 保存方案"
                    : file == null ? "新方案 · 可手填销量，也可从领星导入。" : "方案已保存 · " + Path.GetFileName(file);
            }
            finally { loading = false; }
        }

        private void SourceLabel()
        {
            source.Text = String.IsNullOrWhiteSpace(plan.LookupValue) ? "录入的数据按年月保存，改基准日不会挪动以前填过的月份。"
                : "领星来源  " + plan.StoreName + " / " + plan.LookupValue + "      黄色输入仍可手动修改。";
        }

        private static void Cells(DataGridViewRow row, params object[] values)
        {
            for (int i = 0; i < values.Length; i++) row.Cells[i].Value = values[i];
        }

        private static void ColorStatus(DataGridViewCell cell, string text)
        {
            if (text == "预计缺货")
            {
                cell.Style.BackColor = Theme.DangerBg;
                cell.Style.ForeColor = Theme.Danger;
            }
            else
            {
                bool warning = text.Contains("待") || text.Contains("低于") || text.Contains("预测期") || text.Contains("超出");
                cell.Style.BackColor = warning ? Theme.Input : Color.Empty;
                cell.Style.ForeColor = warning ? Theme.Warn : Theme.Muted;
            }
        }

        private static string Qty(long? value) { return value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : ""; }
        private static string DateText(DateTime? date) { return date.HasValue ? Forecast.Iso(date.Value) : ""; }

        private bool FlushEdits()
        {
            decimal? ignored;
            if (!batches.EndEdit() || !sales.EndEdit() || !TryAdjustment(out ignored))
            {
                status.Text = "请先修正当前输入，或按 Esc 取消单元格编辑。";
                return false;
            }
            return true;
        }

        private bool CanReplacePlan()
        {
            if (!FlushEdits()) return false;
            if (!dirty) return true;
            var answer = MessageBox.Show(this, "当前方案有未保存的修改。要先保存吗？", "补货预测工作台",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            return answer == DialogResult.No || (answer == DialogResult.Yes && SavePlan(false));
        }

        private void NewPlan()
        {
            if (CanReplacePlan()) LoadPlan(Plan.Create(), null);
        }

        private void OpenPlan()
        {
            if (!CanReplacePlan()) return;
            using (var dialog = new OpenFileDialog { Filter = "补货方案 (*.json)|*.json", Title = "打开补货方案" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { LoadPlan(PlanFile.Read(dialog.FileName), dialog.FileName); }
                catch (Exception error) { Report("无法打开方案", error); }
            }
        }

        private bool SavePlan(bool saveAs)
        {
            if (!FlushEdits()) return false;
            var destination = file;
            if (saveAs || destination == null)
            {
                var name = String.Concat((plan.Product ?? "").Where(ch => !Path.GetInvalidFileNameChars().Contains(ch))).Trim();
                using (var dialog = new SaveFileDialog { Filter = "补货方案 (*.json)|*.json", DefaultExt = "json",
                    AddExtension = true, Title = "保存补货方案", FileName = name.Length == 0 ? "补货方案" : name + " 补货方案" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                    destination = dialog.FileName;
                }
            }
            try
            {
                PlanFile.Write(destination, plan);
                file = destination;
                dirty = false;
                RefreshForecast();
                return true;
            }
            catch (Exception error) { Report("无法保存方案", error); return false; }
        }

        private void Import()
        {
            if (!FlushEdits()) return;
            using (var dialog = new ImportDialog(plan, Forecast.Calculate(plan), sessionKey))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                sessionKey = dialog.ConnectedKey;
                var data = dialog.Data;
                if (data.Stock.HasValue) plan.OnHand = data.Stock.Value;
                if (dialog.CountInbound && data.Inbound.HasValue) plan.CountedInTransit = data.Inbound.Value;
                foreach (var month in data.Sales) plan.SetSales(month.Key, month.Value, true);
                plan.StoreId = data.Store.Id;
                plan.StoreName = data.Store.Name;
                plan.LookupValue = data.Identifier;
                plan.LookupType = data.Field;
                if (String.IsNullOrWhiteSpace(plan.Product)) plan.Product = data.Identifier;
                LoadPlan(plan, file);
                dirty = true;
                RefreshForecast();
                status.Text = "已导入 " + data.Sales.Count + " 个月销量" + (data.Stock.HasValue ? "和今日 FBA 库存" : "") +
                    " · 有未保存的修改";
            }
        }

        private void Report(string title, Exception error)
        {
            MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
