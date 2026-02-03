// 0090_LoginFormAccessor.cs (복붙용 / C# 7.3)
// - static 영역(0300)에서 Login 인스턴스의 Exec에 접근하기 위한 최소 헬퍼

using System;

namespace Exercise_1
{
    public static class LoginFormAccessor
    {
        private static Login _current;

        public static void Set(Login login)
        {
            _current = login;
        }

        public static 매매실행 TryGetExec()
        {
            try
            {
                return _current?.Exec;
            }
            catch
            {
                return null;
            }
        }
    }
}

// 2026-01-16-00-00-00
