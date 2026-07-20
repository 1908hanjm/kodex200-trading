// 0830_이월슬라이딩판정.cs
// ------------------------------------------------------------
// ✅ 목적:
// - 구 pending 로직 완전 제거
// - 0800은 이제 pending을 반환하지 않음
// - 0830은 항상 NORMAL 판단만 수행
// - 실제 pending 복구는 이후 Pending 테이블 기반으로 처리
// ------------------------------------------------------------

using System;

namespace Exercise_1
{
    public sealed class _0830_이월슬라이딩판정
    {
        private readonly Action<string> _log;

        public _0830_이월슬라이딩판정(Action<string> log = null)
        {
            _log = log ?? (_ => { });
        }

        public enum CarryState
        {
            NORMAL = 0,
            BROKEN_STATE = 1
        }

        public sealed class Decision
        {
            public CarryState State { get; set; }
            public bool CanStartAutoTrading { get; set; }
            public string Reason { get; set; }

            public override string ToString()
            {
                return string.Format(
                    "State={0}, CanStartAutoTrading={1}, Reason={2}",
                    State,
                    CanStartAutoTrading,
                    Reason ?? ""
                );
            }
        }

        public Decision Decide(_0800_이월상태점검.Snapshot snap)
        {
            if (snap == null) throw new ArgumentNullException("snap");

            var d = new Decision();

            // =====================================================
            // pending 완전 제거 상태
            // =====================================================

            // 보유 없으면 비정상
            if (snap.HeldCount <= 0)
            {
                d.State = CarryState.BROKEN_STATE;
                d.CanStartAutoTrading = false;
                d.Reason = "heldCount <= 0";
                _log("[0830] " + d);
                return d;
            }

            // 1~10개 → 정상
            if (snap.HeldCount <= 10)
            {
                d.State = CarryState.NORMAL;
                d.CanStartAutoTrading = true;

                if (snap.HeldCount == 10)
                    d.Reason = "heldCount == 10";
                else
                    d.Reason = "heldCount < 10 (allowed during trading)";

                _log("[0830] " + d);
                return d;
            }

            // 10 초과 → 비정상
            d.State = CarryState.BROKEN_STATE;
            d.CanStartAutoTrading = false;
            d.Reason = "heldCount > 10";
            _log("[0830] " + d);
            return d;
        }
    }
}
// 2026-04-09 55291