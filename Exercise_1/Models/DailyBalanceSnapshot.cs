
using System;

namespace Exercise_1
{
    public sealed class DailyBalanceSnapshot
    {
        public string Ymd { get; set; }                 // "yyyyMMdd"
        public long 보유량 { get; set; }
        public double 현금 { get; set; }
        public double D2 { get; set; }
        public double 당일손익 { get; set; }
        public double 총자산 { get; set; }              // 비워도 됨(없으면 DbFuncs에서 계산)
        public DateTimeOffset UpdatedTsKst { get; set; }
    }
}
