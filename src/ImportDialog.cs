using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Replenishment
{
    internal sealed class ImportDialog : Form
    {
        private readonly Plan plan;
        private readonly Forecast forecast;
        private readonly TextBox key = new TextBox(), identifier = new TextBox();
        private readonly ComboBox store = new ComboBox(), dimension = new ComboBox();
        private readonly CheckBox readStock = new CheckBox(), countInbound = new CheckBox();
        private readonly TextBox preview = new TextBox();
        private readonly Button connect, lookup, apply;
        private CancellationTokenSource cancellation;
        private LingXing client;

        internal ImportData Data { get; private set; }
        internal bool CountInbound { get { return countInbound.Checked; } }
        internal string ConnectedKey { get; private set; }

        internal ImportDialog(Plan current, Forecast window, string previousKey)
        {
            plan = current;
            forecast = window;
            Text = "从领星 MCP 导入";
            Font = Theme.Normal;
            ClientSize = new Size(656, 550);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = Theme.Paper;
            var top = new Panel { Dock = DockStyle.Top, Height = 67, BackColor = Theme.Dark };
            Controls.Add(top);
            var title = Theme.Label("从领星导入", 18, true, Color.White);
            title.SetBounds(17, 3, 600, 36);
            top.Controls.Add(title);
            var subtitle = Theme.Label("选择店铺和商品，预览 FBA 库存及去年同期 FBA 销量。", 10, false, Color.White);
            subtitle.SetBounds(20, 39, 607, 21);
            top.Controls.Add(subtitle);

            AddLabel("MCP 密钥", 18, 83);
            key.SetBounds(122, 83, 389, 28);
            key.UseSystemPasswordChar = true;
            key.Text = String.IsNullOrEmpty(previousKey) ? LingXing.LocalKey() : previousKey;
            key.AccessibleName = "领星 MCP 密钥";
            Controls.Add(key);
            connect = Theme.Button("连接并取店铺", Connect);
            connect.SetBounds(520, 79, 113, 32);
            Controls.Add(connect);

            AddLabel("店铺 / 站点", 18, 124);
            store.SetBounds(122, 124, 503, 28);
            store.DropDownStyle = ComboBoxStyle.DropDownList;
            store.AccessibleName = "店铺 / 站点";
            Controls.Add(store);
            AddLabel("商品识别", 18, 165);
            dimension.SetBounds(122, 165, 94, 28);
            dimension.DropDownStyle = ComboBoxStyle.DropDownList;
            dimension.Items.AddRange(new object[] { "ASIN", "MSKU" });
            dimension.SelectedIndex = plan.LookupType == "seller_sku" ? 1 : 0;
            Controls.Add(dimension);
            identifier.SetBounds(225, 165, 400, 28);
            identifier.Text = plan.LookupValue ?? "";
            identifier.MaxLength = 100;
            identifier.AccessibleName = "输入 ASIN 或 MSKU";
            Controls.Add(identifier);

            bool today = forecast.Start.AddDays(-1) == DateTime.Today;
            readStock.Text = today ? "导入今日 FBA 可售库存" : "当前基准日非今日，仅导入历史销量";
            readStock.SetBounds(122, 202, 490, 26);
            readStock.Checked = today;
            readStock.Enabled = today;
            Controls.Add(readStock);

            preview.SetBounds(19, 242, 607, 216);
            preview.Multiline = true;
            preview.ReadOnly = true;
            preview.ScrollBars = ScrollBars.Vertical;
            preview.BackColor = Color.White;
            preview.Text = "先连接领星，然后选择店铺并输入 ASIN 或 MSKU。\r\n已配置的本机密钥会自动填入。";
            Controls.Add(preview);
            countInbound.Text = "把查到的已发运及接收中数量填入「按可售计入的在途」";
            countInbound.SetBounds(19, 461, 610, 25);
            countInbound.Enabled = false;
            Controls.Add(countInbound);
            var cancel = Theme.Button("关闭", Close);
            cancel.SetBounds(19, 504, 102, 32);
            Controls.Add(cancel);
            lookup = Theme.Button("查询并预览", Lookup);
            lookup.SetBounds(372, 504, 125, 32);
            lookup.Enabled = false;
            Controls.Add(lookup);
            apply = Theme.Button("应用到方案", Apply, true);
            apply.SetBounds(510, 504, 116, 32);
            apply.Enabled = false;
            Controls.Add(apply);
            key.TextChanged += (sender, args) => {
                ClearPreview();
                store.Items.Clear();
                if (client != null) client.Dispose();
                client = null;
                lookup.Enabled = false;
            };
            store.SelectedIndexChanged += (sender, args) => ClearPreview();
            dimension.SelectedIndexChanged += (sender, args) => ClearPreview();
            identifier.TextChanged += (sender, args) => ClearPreview();
            readStock.CheckedChanged += (sender, args) => ClearPreview();
            FormClosing += (sender, args) => { if (cancellation != null) cancellation.Cancel(); };
            FormClosed += (sender, args) => { if (client != null) client.Dispose(); };
            Shown += (sender, args) => { if (key.Text.Trim().Length > 0) Connect(); };
        }

        private void AddLabel(string text, int x, int y)
        {
            var label = Theme.Label(text);
            label.SetBounds(x, y, 100, 26);
            Controls.Add(label);
        }

        private void ClearPreview()
        {
            Data = null;
            apply.Enabled = false;
            countInbound.Checked = false;
            countInbound.Enabled = false;
        }

        private async void Connect()
        {
            if (cancellation != null) return;
            var secret = key.Text.Trim();
            if (secret.Length == 0)
            {
                preview.Text = "请填写领星 MCP 密钥。";
                key.Focus();
                return;
            }
            ClearPreview();
            store.Items.Clear();
            if (client != null) client.Dispose();
            client = new LingXing(secret);
            await Run("正在连接领星并读取店铺……", async token => {
                var stores = await client.Connect(token);
                if (IsDisposed) return;
                if (stores.Count == 0) throw new InvalidOperationException("当前账号没有可读取的店铺。");
                store.Items.AddRange(stores.Cast<object>().ToArray());
                int previous = stores.FindIndex(item => item.Id == plan.StoreId);
                if (previous >= 0) store.SelectedIndex = previous;
                else if (stores.Count == 1) store.SelectedIndex = 0;
                ConnectedKey = secret;
                preview.Text = "已连接领星。请选择店铺、填写商品，再点「查询并预览」。";
            });
        }

        private async void Lookup()
        {
            if (client == null || cancellation != null) return;
            var selected = store.SelectedItem as StoreOption;
            var value = identifier.Text.Trim();
            var field = dimension.SelectedIndex == 0 ? "asin" : "seller_sku";
            if (selected == null || value.Length == 0 || (field == "asin" && !Regex.IsMatch(value, "^[a-zA-Z0-9]{10}$")))
            {
                preview.Text = "请选择店铺，并输入有效的 ASIN 或 MSKU。";
                return;
            }
            if (field == "asin") value = value.ToUpperInvariant();
            if (!String.IsNullOrEmpty(plan.LookupValue) &&
                (selected.Id != plan.StoreId || field != plan.LookupType || !String.Equals(value, plan.LookupValue, StringComparison.OrdinalIgnoreCase)))
            {
                preview.Text = "这个方案已关联另一个店铺或商品。请在主窗口选择「新建产品」，再导入所选商品。";
                return;
            }
            ClearPreview();
            await Run("正在读取库存和历史销量，请稍候……", async token => {
                var result = await client.Load(selected, field, value, forecast, readStock.Checked, token);
                if (IsDisposed) return;
                Data = result;
                var summary = new StringBuilder();
                summary.AppendLine(selected.ToString() + "  /  " + value);
                if (result.Stock.HasValue)
                    summary.AppendLine("今日 FBA 可售 " + result.Stock.Value + " 台，已发运或接收中 " + result.Inbound.Value + " 台。");
                else if (readStock.Checked) summary.AppendLine("库存未导入。 " + result.StockProblem);
                else summary.AppendLine("保留方案中的库存快照。");
                summary.AppendLine();
                foreach (var item in result.Sales.OrderBy(item => item.Key))
                    summary.AppendLine(Forecast.MonthKey(item.Key) + "  FBA 销量 " + item.Value + " 台");
                if (result.Unavailable.Count > 0)
                    summary.AppendLine("未返回可用销量的月份可手填  " + String.Join("、", result.Unavailable));
                preview.Text = summary.ToString();
                countInbound.Enabled = result.Inbound.HasValue;
                apply.Enabled = result.Stock.HasValue || result.Sales.Count > 0;
            });
        }

        private async Task Run(string pending, Func<CancellationToken, Task> action)
        {
            cancellation = new CancellationTokenSource();
            try
            {
                SetBusy(true);
                preview.Text = pending;
                await action(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) preview.Text = "查询取消或超时，请重试。";
            }
            catch (Exception error)
            {
                if (!IsDisposed) preview.Text = error is InvalidOperationException ? error.Message : "连接未完成，请检查网络和密钥后重试。";
            }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
                if (!IsDisposed) SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            UseWaitCursor = busy;
            key.Enabled = connect.Enabled = store.Enabled = dimension.Enabled = identifier.Enabled = !busy;
            readStock.Enabled = !busy && forecast.Start.AddDays(-1) == DateTime.Today;
            lookup.Enabled = !busy && store.Items.Count > 0;
        }

        private void Apply()
        {
            if (Data == null || cancellation != null) return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
