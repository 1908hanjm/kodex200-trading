using System;
using System.Collections.Generic;

namespace Exercise_1.Repositories
{
    /// <summary>
    /// 잔고/정산 엔티티용 저장소 (스켈레톤)
    /// </summary>
    public sealed class BalanceRepository : IRepository<Balance>
    {
        private readonly string _connStr;

        public BalanceRepository(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentException("dbPath is null or empty.", nameof(dbPath));

            _connStr = $"Data Source={dbPath};Version=3;";
        }

        public void Add(Balance entity)
        {
            throw new NotImplementedException();
        }

        public void Update(Balance entity)
        {
            throw new NotImplementedException();
        }

        public void Delete(object id)
        {
            throw new NotImplementedException();
        }

        public Balance GetById(object id)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<Balance> GetAll()
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 최소 필드만 가진 잔고 엔티티 스텁
    /// (이미 프로젝트에 Balance 클래스가 있다면 이 부분은 삭제하세요)
    /// </summary>
    public sealed class Balance
    {
        public long Id { get; set; }
        public DateTimeOffset AsOfTs { get; set; }
        public double Cash { get; set; }
        public double Equity { get; set; }
        public double PnL { get; set; }
    }
}
