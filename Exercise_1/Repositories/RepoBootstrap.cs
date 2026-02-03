// -------------------------------------------------------------
// FILE: Repositories/RepoBootstrap.cs
// -------------------------------------------------------------
using System;

namespace Exercise_1
{
    /// <summary>
    /// Login.cs에서 간단히 Repo들을 만들 수 있는 부트스트랩 헬퍼
    /// </summary>
    public static class RepoBootstrap
    {
        public static (IBandRepo bands, ICycleLogRepo cycles) Create(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentNullException(nameof(dbPath));

            var bands = new SqliteBandRepo(dbPath);
            var cycles = new SqliteCycleLogRepo(dbPath);
            return (bands, cycles);
        }
    }
}

