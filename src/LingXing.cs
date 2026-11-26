using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Replenishment
{
    internal sealed class StoreOption
    {
        public string Id, Name, Country;
        public override string ToString() { return Name + " · " + Country + " (" + Id + ")"; }
    }

    internal sealed class ImportData
    {
        public StoreOption Store;
        public string Field, Identifier;
        public int? Stock, Inbound;
        public readonly Dictionary<DateTime, int> Sales = new Dictionary<DateTime, int>();
        public readonly List<string> Unavailable = new List<string>();
        public string StockProblem;
    }

    internal sealed class LingXing : IDisposable
    {
        internal const string Endpoint = "https://openmcp.lingxing.com/mcp-servers/lingxing-mcp";
        private const string StoreTool = "customer_review_country_sellers";
        private const string SalesTool = "statistics_sales_analysis_list";
        private const string StockTool = "get_fba_stock_list";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private readonly HttpClient http;
        private readonly string key;
        private string protocol = "2025-03-26";
        private string session;
        private int sequence;

        internal LingXing(string apiKey)
        {
            key = apiKey.Trim();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            http.Timeout = TimeSpan.FromSeconds(25);
        }

        internal static string LocalKey()
        {
            var env = Environment.GetEnvironmentVariable("LINGXING_MCP_KEY");
            if (!String.IsNullOrWhiteSpace(env)) return env.Trim();
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            try
            {
                var text = File.ReadAllText(Path.Combine(home, "config.toml"));
                var block = Regex.Match(text, @"(?im)^\s*\[mcp_servers\.LingXing-MCP\][ \t]*\r?\n([\s\S]*?)(?=^[ \t]*\[|\z)");
                var header = Regex.Match(block.Groups[1].Value, "(?i)\"X-Mcp-Key\"\\s*=\\s*\"([^\"]+)\"");
                return header.Groups[1].Value;
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        internal async Task<List<StoreOption>> Connect(CancellationToken token)
        {
            var reply = await Rpc("initialize", Obj("protocolVersion", protocol, "capabilities", Obj(),
                "clientInfo", Obj("name", "ReplenishmentDesktop", "version", "1.0")), false, token).ConfigureAwait(false);
            var remoteProtocol = Str(reply, "protocolVersion");
            if (!String.IsNullOrEmpty(remoteProtocol)) protocol = remoteProtocol;
            await Rpc("notifications/initialized", Obj(), true, token).ConfigureAwait(false);
            await Tool("help", Obj("query", StoreTool, "limit", 5), token).ConfigureAwait(false);
            foreach (var id in new[] { StoreTool, SalesTool, StockTool })
                await Tool("search", Obj("toolId", id), token).ConfigureAwait(false);
            var data = await Action(StoreTool, Obj(), token).ConfigureAwait(false);
            return Rows(data, "sellers").Select(item => new StoreOption {
                Id = Str(item, "sid"), Name = Str(item, "seller_simple_name"), Country = Str(item, "country")
            }).Where(store => store.Id.Length > 0).GroupBy(store => store.Id).Select(group => group.First()).ToList();
        }

        internal async Task<ImportData> Load(StoreOption store, string field, string identifier,
            Forecast forecast, bool readStock, CancellationToken token)
        {
            var output = new ImportData { Store = store, Field = field, Identifier = identifier };
            if (readStock)
            {
                try
                {
                    var quantities = await Inventory(store, field, identifier, token).ConfigureAwait(false);
                    output.Stock = quantities.Item1;
                    output.Inbound = quantities.Item2;
                }
                catch (Exception error)
                {
                    token.ThrowIfCancellationRequested();
                    output.StockProblem = error is InvalidOperationException ? error.Message : "库存查询未完成。";
                }
            }
            var cutoff = DateTime.Today.AddDays(-1);
            foreach (var month in forecast.Months.Select(item => item.Month.AddYears(-1)))
            {
                token.ThrowIfCancellationRequested();
                var end = month.AddMonths(1).AddDays(-1);
                if (end > cutoff || end >= forecast.Start)
                {
                    output.Unavailable.Add(Forecast.MonthKey(month));
                    continue;
                }
                try
                {
                    var volume = await Volume(store, field, identifier, month, end, token).ConfigureAwait(false);
                    if (volume.HasValue) output.Sales[month] = volume.Value;
                    else output.Unavailable.Add(Forecast.MonthKey(month));
                }
                catch (Exception)
                {
                    token.ThrowIfCancellationRequested();
                    output.Unavailable.Add(Forecast.MonthKey(month));
                }
            }
            return output;
        }

        private async Task<Tuple<int, int>> Inventory(StoreOption store, string field, string value, CancellationToken token)
        {
            // Each FNSKU represents one inventory pool even when several listing rows refer to it.
            var pools = new Dictionary<string, Tuple<int, int>>(StringComparer.OrdinalIgnoreCase);
            for (int offset = 0; ; offset += 100)
            {
                var data = await Action(StockTool, Obj("sid", store.Id, "search_field", field, "search_value", value,
                    "offset", offset, "length", 100, "fulfillment_channel_type", "FBA", "is_hide_zero_stock", "0",
                    "is_parant_asin_merge", "0"), token).ConfigureAwait(false);
                var rows = Rows(data, "list");
                foreach (var row in rows.Where(item => Matches(item, field, value) && Matches(item, "sid", store.Id)))
                {
                    var fnsku = Str(row, "fnsku").Trim();
                    if (fnsku.Length == 0)
                        throw new InvalidOperationException("库存行没有 FNSKU，无法核对库存归属。");
                    var pair = Tuple.Create(Units(row, "afn_fulfillable_quantity"),
                        checked(Units(row, "afn_inbound_shipped_quantity") + Units(row, "afn_inbound_receiving_quantity")));
                    Tuple<int, int> previous;
                    if (pools.TryGetValue(fnsku, out previous) && !pair.Equals(previous))
                        throw new InvalidOperationException("同一 FNSKU 返回了不同库存，请核对后手填。");
                    pools[fnsku] = pair;
                }
                if (rows.Count == 0 || offset + rows.Count >= Units(data, "total")) break;
                if (offset >= 9900) throw new InvalidOperationException("库存记录太多，请改用更精确的 MSKU。");
            }
            if (pools.Count == 0) throw new InvalidOperationException("没有找到匹配的 FBA 库存记录。");
            var stock = pools.Values.Sum(item => (long)item.Item1);
            var inbound = pools.Values.Sum(item => (long)item.Item2);
            if (stock > Forecast.UnitLimit || inbound > Forecast.UnitLimit)
                throw new InvalidOperationException("库存数量超出范围。");
            return Tuple.Create((int)stock, (int)inbound);
        }

        private async Task<int?> Volume(StoreOption store, string field, string value, DateTime start, DateTime end,
            CancellationToken token)
        {
            var data = await Action(SalesTool, Obj("asin_type", field, "date_type", "5", "offset", 0, "length", 200,
                "start_date", Forecast.Iso(start), "end_date", Forecast.Iso(end), "sids", store.Id,
                "search_field", field, "search_value", new[] { value }, "stat_field", "volume",
                "delivery_methods", new[] { "AFN" }), token).ConfigureAwait(false);
            if (Units(data, "total") > 200) return null;
            var matches = Rows(data, "list").Where(row => Matches(row, field, value) && Matches(row, "sid", store.Id)).ToList();
            if (matches.Count != 1) return null;
            return Units(Dict(Value(matches[0], "date1")), "volume");
        }

        private async Task<Dictionary<string, object>> Action(string id, Dictionary<string, object> parameters, CancellationToken token)
        {
            var result = await Tool("action", Obj("toolId", id, "params", parameters), token).ConfigureAwait(false);
            var data = Dict(Value(result, "data"));
            var inner = Value(data, "data") as Dictionary<string, object>;
            return inner ?? data;
        }

        private async Task<Dictionary<string, object>> Tool(string name, Dictionary<string, object> args, CancellationToken token)
        {
            var result = await Rpc("tools/call", Obj("name", name, "arguments", args), false, token).ConfigureAwait(false);
            if (Object.Equals(Value(result, "isError"), true))
                throw new InvalidOperationException("领星拒绝了这次查询，请核对连接和权限。");
            var payload = Value(result, "structuredContent") as Dictionary<string, object>;
            if (payload == null)
            {
                var body = Rows(result, "content").FirstOrDefault(line => Str(line, "type") == "text");
                if (body == null) throw new InvalidOperationException("领星没有返回查询数据。");
                payload = Json(Str(body, "text"));
            }
            if (Object.Equals(Value(payload, "success"), false))
                throw new InvalidOperationException("领星查询失败，请检查该工具权限和筛选条件。");
            return payload;
        }

        private async Task<Dictionary<string, object>> Rpc(string method, Dictionary<string, object> args,
            bool notification, CancellationToken token)
        {
            var body = Obj("jsonrpc", "2.0", "method", method, "params", args);
            if (!notification) body["id"] = Interlocked.Increment(ref sequence);
            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                request.Headers.Add("X-Mcp-Key", key);
                request.Headers.Add("MCP-Protocol-Version", protocol);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
                if (!String.IsNullOrEmpty(session)) request.Headers.Add("Mcp-Session-Id", session);
                request.Content = new StringContent(new JavaScriptSerializer().Serialize(body), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("领星 MCP 返回 HTTP " + (int)response.StatusCode + "，请检查连接或密钥。");
                    IEnumerable<string> headers;
                    if (response.Headers.TryGetValues("Mcp-Session-Id", out headers)) session = headers.FirstOrDefault();
                    if (notification) return Obj();
                    var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.Content.Headers.ContentType != null && response.Content.Headers.ContentType.MediaType == "text/event-stream")
                        content = content.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                            .Select(line => line.Substring(5).Trim()).LastOrDefault(line => line.Contains("\"id\"")) ?? "";
                    var envelope = Json(content);
                    if (Value(envelope, "error") != null)
                        throw new InvalidOperationException("领星 MCP 请求未完成，请检查连接和权限。");
                    return Dict(Value(envelope, "result"));
                }
            }
        }

        private static bool Matches(Dictionary<string, object> row, string field, string expected)
        {
            var found = Value(row, field);
            var array = found as IList;
            return array != null
                ? array.Cast<object>().Any(item => String.Equals(Convert.ToString(item, Invariant), expected, StringComparison.OrdinalIgnoreCase))
                : String.Equals(Convert.ToString(found, Invariant), expected, StringComparison.OrdinalIgnoreCase);
        }

        private static int Units(Dictionary<string, object> row, string field)
        {
            int quantity;
            if (!Int32.TryParse(Str(row, field), NumberStyles.Integer | NumberStyles.AllowThousands, Invariant, out quantity)
                || quantity < 0 || quantity > Forecast.UnitLimit)
                throw new InvalidOperationException("领星返回的库存或销量字段不完整。");
            return quantity;
        }

        private static List<Dictionary<string, object>> Rows(Dictionary<string, object> node, string field)
        {
            var values = Value(node, field) as IList;
            if (values == null) throw new InvalidOperationException("领星返回的数据格式有变化。");
            return values.Cast<object>().Select(Dict).ToList();
        }

        private static Dictionary<string, object> Json(string input)
        {
            return Dict(new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(input));
        }

        private static Dictionary<string, object> Dict(object value)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) throw new InvalidOperationException("领星返回的数据格式有变化。");
            return result;
        }

        private static string Str(Dictionary<string, object> data, string field) { return Convert.ToString(Value(data, field), Invariant) ?? ""; }
        private static object Value(Dictionary<string, object> data, string field)
        {
            object result;
            return data.TryGetValue(field, out result) ? result : null;
        }
        private static Dictionary<string, object> Obj(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1];
            return result;
        }
        public void Dispose() { http.Dispose(); }
    }
}
