using System;
using System.Collections.Generic;

namespace Exercise_1.Repositories
{
    /// <summary>
    /// Tick 엔티티용 저장소 (스켈레톤)
    /// </summary>
    public sealed class TickRepository : IRepository<Tick>
    {
        private readonly string _connStr;

        public TickRepository(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentException("dbPath is null or empty.", nameof(dbPath));

            _connStr = $"Data Source={dbPath};Version=3;";
        }

        public void Add(Tick entity)
        {
            throw new NotImplementedException();
        }

        public void Update(Tick entity)
        {
            throw new NotImplementedException();
        }

        public void Delete(object id)
        {
            throw new NotImplementedException();
        }

        public Tick GetById(object id)
        {
            throw new NotImplementedException();
        }

        public IEnumerable<Tick> GetAll()
        {
            throw new NotImplementedException();
        }
    }
}
