using System;
using System.Collections.Generic;

namespace Exercise_1.Repositories
{
    /// <summary>
    /// 주문 엔티티용 저장소 (스켈레톤)
    /// </summary>
    public sealed class OrderRepository : IRepository<Order>
    {
        private readonly string _connStr;

        public OrderRepository(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentException("dbPath is null or empty.", nameof(dbPath));

            _connStr = $"Data Source={dbPath};Version=3;";
        }

        public void Add(Order entity)
        {
            throw new NotImplementedException();
        }

        public void Update(Order entity)
        {
            throw new NotImplementedException();
        }

        public void Delete(object id)
        {
            throw new NotImplementedException();
        }

        public Order GetById(object id)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<Order> GetAll()
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 최소 필드만 가진 주문 엔티티 스텁
    /// (이미 프로젝트에 Order 클래스가 있다면 이 부분은 삭제하세요)
    /// </summary>
    public sealed class Order
    {
        public long Id { get; set; }
        public string OrdNo { get; set; }
        public string Symbol { get; set; }
        public string Side { get; set; }   // "B"/"S"
        public long Qty { get; set; }
        public double Price { get; set; }
        public string Status { get; set; }
        public DateTimeOffset CreatedTs { get; set; }
        public DateTimeOffset? UpdatedTs { get; set; }
    }
}
