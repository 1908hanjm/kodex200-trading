using System.Collections.Generic;

namespace Exercise_1.Repositories
{
    /// <summary>
    /// 데이터 접근 계층 공통 인터페이스 (CRUD 스켈레톤)
    /// </summary>
    public interface IRepository<T>
    {
        void Add(T entity);
        void Update(T entity);
        void Delete(object id);

        T GetById(object id);
        IEnumerable<T> GetAll();
    }
}
