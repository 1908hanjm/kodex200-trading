using System;

namespace Exercise_1
{
    public static class 거래후초기화
    {
        public static object RichBoxRef;

        public static event Action ClearTickLists;

        public static void ClearRichBox()
        {
            try { ClearTickLists?.Invoke(); } catch { }
        }
    }
}