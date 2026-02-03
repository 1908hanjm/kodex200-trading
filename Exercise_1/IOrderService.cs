// IOrderService.cs  (C# 7.3 호환)
// - 인터페이스만 정의. 모든 DTO/Enums는 OrderModels.cs에서 참조함.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Exercise_1
{
    public interface IOrderService : IDisposable
    {
        // ───────────── 이벤트 ─────────────
        event Action<OrderAck> OrderAccepted;
        event Action<string> OrderRejected;
        event Action<OrderRow> OrderUpdated;
        event Action<FillEvent> FillReceived;

        // ───────────── 메서드 ─────────────
        Task<OrderAck> PlaceAsync(OrderRequest req);
        Task<bool> CancelAsync(string accountNo, string pwd, string orderNo, string symbol);
        Task<IList<OrderRow>> LoadOpenOrdersAsync(string accountNo, string pwd, string symbol = null);
    }
}

