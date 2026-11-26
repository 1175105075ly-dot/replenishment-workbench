using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Replenishment
{
    [DataContract]
    public sealed class Plan
    {
        [DataMember] public int Version = 1;
        [DataMember] public string Product = "";
        [DataMember] public string BaseDate = "";
        [DataMember] public int OnHand;
        [DataMember] public int CountedInTransit;
        [DataMember] public int SafetyStock;
        [DataMember] public int ProductionDays;
        [DataMember] public int TransitDays = 40;
        [DataMember] public decimal? AdjustmentPercent;
        [DataMember] public string StoreId;
        [DataMember] public string StoreName;
        [DataMember] public string LookupType;
        [DataMember] public string LookupValue;
        [DataMember] public List<Shipment> Shipments = new List<Shipment>();
        [DataMember] public List<MonthSales> Sales = new List<MonthSales>();

        public static Plan Create()
        {
            var plan = new Plan { BaseDate = Forecast.Iso(DateTime.Today) };
            foreach (var name in new[] { "第 1 批", "第 2 批", "第 3 批" })
                plan.Shipments.Add(new Shipment { Name = name });
            return plan;
        }

        public void SetSales(DateTime date, int? quantity, bool historical)
        {
            var key = Forecast.MonthKey(date);
            var item = Sales.FirstOrDefault(sale => sale.Month == key);
            if (item == null)
            {
                if (!quantity.HasValue) return;
                item = new MonthSales { Month = key };
                Sales.Add(item);
            }
            if (historical) item.Actual = quantity;
            else item.Manual = quantity;
            if (!item.Actual.HasValue && !item.Manual.HasValue) Sales.Remove(item);
        }
    }

    [DataContract]
    public sealed class MonthSales
    {
        [DataMember] public string Month;
        [DataMember] public int? Actual;
        [DataMember] public int? Manual;
    }

    [DataContract]
    public sealed class Shipment
    {
        [DataMember] public string Name;
        [DataMember] public string OrderDate;
        [DataMember] public int? Quantity;
    }

    internal sealed class MonthForecast
    {
        public DateTime Month, Start, End;
        public int? Actual, Manual;
        public long? FullDemand, Demand, Opening, Closing, Minimum;
        public long Arrivals;
        public string Source, Status;
    }

    internal sealed class ShipmentForecast
    {
        public DateTime? ShipDate, AvailableDate;
        public long? Coverage, Needed, EndStock;
        public string Status = "";
        public bool InWindow, MissingDemand;
    }

    internal sealed class Outage
    {
        public DateTime Start;
        public DateTime? Recovery;
        public DateTime CountThrough;
        public bool DemandMissing;
        public int Days { get { return ((Recovery ?? CountThrough.AddDays(1)) - Start).Days; } }
    }

    internal sealed class Forecast
    {
        public const int UnitLimit = 1000000000;
        public const int LeadLimit = 3650;
        public const decimal MaximumAdjustment = 100000000m;
        public static readonly DateTime MinDate = new DateTime(1901, 1, 1);
        public static readonly DateTime MaxBaseDate = new DateTime(9998, 12, 31);
        public static readonly DateTime MaxOrderDate = new DateTime(9998, 12, 31);
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        public readonly List<MonthForecast> Months = new List<MonthForecast>();
        public readonly List<ShipmentForecast> Shipments = new List<ShipmentForecast>();
        public readonly List<Outage> Outages = new List<Outage>();
        public DateTime Start, End;
        public long Opening, Planned;
        public DateTime? FirstMissing;
        public int MissingMonths { get { return Months.Count(month => !month.Demand.HasValue); } }
        public long? Closing { get { return Months.Last().Closing; } }

        public static string Iso(DateTime date) { return date.ToString("yyyy-MM-dd", Invariant); }
        public static string MonthKey(DateTime date) { return date.ToString("yyyy-MM", Invariant); }
        public static bool TryDate(string value, out DateTime date)
        {
            return DateTime.TryParseExact((value ?? "").Trim(),
                new[] { "yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyy年M月d日" }, Invariant,
                DateTimeStyles.None, out date) && date >= MinDate && date <= MaxOrderDate;
        }

        private static long Round(decimal value)
        {
            return (long)Math.Round(value, 0, MidpointRounding.AwayFromZero);
        }

        public static Forecast Calculate(Plan plan)
        {
            DateTime baseDate;
            if (!TryDate(plan.BaseDate, out baseDate)) throw new ArgumentException("库存基准日无效。");
            var baseMonth = new DateTime(baseDate.Year, baseDate.Month, 1);
            var result = new Forecast {
                Start = baseDate.AddDays(1),
                End = baseMonth.AddMonths(7).AddDays(-1),
                Opening = (long)plan.OnHand + plan.CountedInTransit,
                Planned = plan.Shipments.Sum(batch => (long)batch.Quantity.GetValueOrDefault())
            };
            var sales = plan.Sales.ToDictionary(item => item.Month, StringComparer.Ordinal);
            result.BuildMonths(plan, sales);
            result.BuildShipments(plan);
            result.Simulate(plan.SafetyStock, plan.Shipments);
            return result;
        }

        private void BuildMonths(Plan plan, Dictionary<string, MonthSales> sales)
        {
            for (var start = Start; start <= End; )
            {
                var month = new DateTime(start.Year, start.Month, 1);
                var stop = month.AddMonths(1).AddDays(-1);
                MonthSales prior, current;
                sales.TryGetValue(MonthKey(month.AddYears(-1)), out prior);
                sales.TryGetValue(MonthKey(month), out current);
                var item = new MonthForecast {
                    Month = month, Start = start, End = stop,
                    Actual = prior == null ? null : prior.Actual,
                    Manual = current == null ? null : current.Manual,
                    Source = "待补录"
                };
                if (item.Actual.HasValue && plan.AdjustmentPercent.HasValue)
                {
                    item.FullDemand = Round(item.Actual.Value * (1m + plan.AdjustmentPercent.Value / 100m));
                    item.Source = "去年同期";
                }
                else if (item.Manual.HasValue)
                {
                    item.FullDemand = item.Manual.Value;
                    item.Source = "手填";
                }
                if (item.FullDemand.HasValue)
                    item.Demand = Round(item.FullDemand.Value * (decimal)((stop - start).Days + 1) /
                        DateTime.DaysInMonth(month.Year, month.Month));
                else if (!FirstMissing.HasValue)
                    FirstMissing = start;
                Months.Add(item);
                start = stop.AddDays(1);
            }
        }

        private void BuildShipments(Plan plan)
        {
            foreach (var shipment in plan.Shipments)
            {
                var item = new ShipmentForecast();
                Shipments.Add(item);
                DateTime order;
                if (!TryDate(shipment.OrderDate, out order))
                {
                    if (shipment.Quantity.HasValue) item.Status = "待填订购日";
                    continue;
                }
                if ((DateTime.MaxValue.Date - order).Days < plan.ProductionDays + plan.TransitDays)
                {
                    item.Status = "可售日期超出范围";
                    continue;
                }
                item.ShipDate = order.AddDays(plan.ProductionDays);
                item.AvailableDate = item.ShipDate.Value.AddDays(plan.TransitDays);
                var date = item.AvailableDate.Value;
                item.InWindow = date >= Start && date <= End;
                if (date < Start) item.Status = "可售日早于预测期";
                else if (date > End) item.Status = "可售日晚于预测期";
                else item.Status = shipment.Quantity.HasValue ? "已计入预测" : "数量待填";
            }
        }

        private void Simulate(int safetyStock, List<Shipment> original)
        {
            var groups = Shipments.Select((shipment, index) => new { shipment, index })
                .Where(item => item.shipment.InWindow)
                .GroupBy(item => item.shipment.AvailableDate.Value)
                .OrderBy(group => group.Key).ToList();
            var receipts = groups.ToDictionary(group => group.Key,
                group => group.Sum(item => (long)original[item.index].Quantity.GetValueOrDefault()));
            var demandBefore = new Dictionary<DateTime, long>();
            long cumulative = 0;
            long? balance = Opening;
            Outage active = null;
            var demandEnd = FirstMissing.HasValue ? FirstMissing.Value.AddDays(-1) : End;
            foreach (var month in Months)
            {
                month.Opening = balance;
                month.Arrivals = receipts.Where(item => item.Key >= month.Start && item.Key <= month.End).Sum(item => item.Value);
                if (!month.Demand.HasValue || !balance.HasValue)
                {
                    balance = null;
                    month.Status = "待补录";
                    continue;
                }
                long minimum = balance.Value;
                int days = (month.End - month.Start).Days + 1;
                for (int i = 0; i < days; i++)
                {
                    DateTime date = month.Start.AddDays(i);
                    long dailyDemand = Round(month.Demand.Value * (i + 1m) / days)
                        - Round(month.Demand.Value * (decimal)i / days);
                    demandBefore[date] = cumulative;
                    cumulative += dailyDemand;
                    long receipt;
                    receipts.TryGetValue(date, out receipt);
                    balance += receipt - dailyDemand;
                    minimum = Math.Min(minimum, balance.Value);
                    if (balance.Value < 0 && active == null)
                    {
                        active = new Outage {
                            Start = date, CountThrough = demandEnd, DemandMissing = FirstMissing.HasValue
                        };
                        Outages.Add(active);
                    }
                    else if (balance.Value >= 0 && active != null)
                    {
                        active.Recovery = date;
                        active = null;
                    }
                }
                month.Closing = balance;
                month.Minimum = minimum;
                month.Status = minimum < 0 ? "预计缺货" : minimum < safetyStock ? "低于安全库存" : "达标";
            }
            demandBefore[demandEnd.AddDays(1)] = cumulative;
            long earlierReceipts = 0;
            for (int index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var primary = group.First().shipment;
                foreach (var duplicate in group.Skip(1))
                    if (duplicate.shipment.Status == "已计入预测") duplicate.shipment.Status = "同日合并到货";
                var next = index + 1 < groups.Count ? groups[index + 1].Key : End.AddDays(1);
                long coverageEnd, coverageStart = 0;
                long received = receipts[group.Key];
                if (demandBefore.TryGetValue(next, out coverageEnd) &&
                    (index == 0 || demandBefore.TryGetValue(group.Key, out coverageStart)))
                {
                    primary.Coverage = coverageEnd - coverageStart;
                    primary.Needed = Math.Max(0, coverageEnd + safetyStock - Opening - earlierReceipts);
                    primary.EndStock = Opening + earlierReceipts + received - coverageEnd;
                }
                else primary.MissingDemand = true;
                earlierReceipts += received;
            }
        }
    }

    internal static class PlanFile
    {
        public static Plan Read(string path)
        {
            Plan plan;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("方案文件过大。");
                plan = (Plan)new DataContractJsonSerializer(typeof(Plan)).ReadObject(stream);
            }
            if (plan == null || plan.Version != 1) throw new InvalidDataException("无法读取这个版本的补货方案。");
            DateTime date;
            if (!Forecast.TryDate(plan.BaseDate, out date) ||
                !ValidUnits(plan.OnHand) || !ValidUnits(plan.CountedInTransit) || !ValidUnits(plan.SafetyStock) ||
                plan.ProductionDays < 0 || plan.ProductionDays > Forecast.LeadLimit ||
                plan.TransitDays < 0 || plan.TransitDays > Forecast.LeadLimit ||
                plan.AdjustmentPercent < -100m || plan.AdjustmentPercent > Forecast.MaximumAdjustment ||
                plan.Shipments == null || plan.Shipments.Count != 3 ||
                plan.Shipments.Any(item => item == null || !ValidUnits(item.Quantity) ||
                    (!String.IsNullOrWhiteSpace(item.OrderDate) && !Forecast.TryDate(item.OrderDate, out date))) ||
                plan.Sales == null || plan.Sales.Any(item => item == null || !ValidUnits(item.Actual) ||
                    !ValidUnits(item.Manual) || !ValidMonth(item.Month)) ||
                plan.Sales.Select(item => item.Month).Distinct(StringComparer.Ordinal).Count() != plan.Sales.Count)
                throw new InvalidDataException("方案数据不完整或含有无效值。");
            return plan;
        }

        private static bool ValidUnits(int? value) { return !value.HasValue || (value >= 0 && value <= Forecast.UnitLimit); }

        private static bool ValidMonth(string value)
        {
            DateTime date;
            return DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        public static void Write(string path, Plan plan)
        {
            var directory = Path.GetDirectoryName(path);
            var pending = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    new DataContractJsonSerializer(typeof(Plan)).WriteObject(stream, plan);
                if (File.Exists(path)) File.Replace(pending, path, null);
                else File.Move(pending, path);
            }
            finally
            {
                if (File.Exists(pending)) File.Delete(pending);
            }
        }
    }
}
