// 0550_매매전송후대기.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 역할:
// - "매매전송 후 대기" = 1건 제한(pending-lock) + TTL + cooldown 관리
// - 주문전송확인 실패(OrdNo 없음/타임아웃/요청 실패/주문 메시지 실패) 시 즉시 Unlock
// - 체결 처리 완료(0700 이후) 시 Unlock
//
// 주의:
// - 이 파일은 "주문번호 매핑"과 "execNo 중복 방지"를 담당하지 않는다.
//   (매핑은 0600, execNo dedup은 0650에서 담당)
//
// side 규칙:
// - 외부에서 side는 반드시 "매수" 또는 "매도"로 호출한다.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Exercise_1
{
    public sealed class _0550_매매전송후대기
    {
        // ─────────────────────────────────────────────
        // 설정값
        // ─────────────────────────────────────────────
        private readonly TimeSpan _pendingTtl;
        private readonly TimeSpan _cooldownOnFail;
        private readonly TimeSpan _cooldownOnFill;

        // ─────────────────────────────────────────────
        // 내부 상태
        // ─────────────────────────────────────────────
        private readonly object _lock = new object();

        // key(side:band) -> pending state
        private readonly Dictionary<string, PendingState> _pendingByKey
            = new Dictionary<string, PendingState>(StringComparer.Ordinal);

        // ─────────────────────────────────────────────
        // ctor
        // ─────────────────────────────────────────────
        public _0550_매매전송후대기(
            int pendingTtlSeconds = 15,
            int cooldownOnFailSeconds = 3,
            int cooldownOnFillSeconds = 1)
        {
            _pendingTtl = TimeSpan.FromSeconds(Math.Max(1, pendingTtlSeconds));
            _cooldownOnFail = TimeSpan.FromSeconds(Math.Max(0, cooldownOnFailSeconds));
            _cooldownOnFill = TimeSpan.FromSeconds(Math.Max(0, cooldownOnFillSeconds));
        }

        // ─────────────────────────────────────────────
        // 1) 주문 진입(중복 차단)
        // ─────────────────────────────────────────────

        /// <summary>
        /// 주문 보내기 직전에 호출.
        /// 이미 pending(또는 쿨다운) 상태면 false를 반환하여 재주문을 차단한다.
        /// side는 "매수"/"매도"만 허용.
        /// </summary>
        public bool TryBegin(string side, int band, long price, out string reason)
        {
            side = NormalizeSideKorOnly(side);
            if (band <= 0)
            {
                reason = "INVALID_BAND";
                return false;
            }

            string key = MakeKey(side, band);
            DateTime now = DateTime.Now;

            lock (_lock)
            {
                CleanupExpired_NoLock(now);

                PendingState st;
                if (_pendingByKey.TryGetValue(key, out st))
                {
                    // 쿨다운
                    if (st.CooldownUntil.HasValue && now < st.CooldownUntil.Value)
                    {
                        reason = $"COOLDOWN until {st.CooldownUntil.Value:HH:mm:ss}";
                        return false;
                    }

                    // pending
                    if (st.IsPending)
                    {
                        reason = $"PENDING since {st.PendingSince:HH:mm:ss}";
                        return false;
                    }
                }

                // 새 pending 등록
                _pendingByKey[key] = new PendingState
                {
                    Side = side,
                    Band = band,
                    LastPrice = price,
                    IsPending = true,
                    PendingSince = now,
                    PendingExpires = now.Add(_pendingTtl),
                    CooldownUntil = null
                };

                reason = "OK";
                Debug.WriteLine($"[0550] BEGIN key={key} price={price} ttl={_pendingTtl.TotalSeconds}s");
                return true;
            }
        }

        // ─────────────────────────────────────────────
        // 2) 주문전송확인(=OrdNo 존재 여부) 실패 처리
        // ─────────────────────────────────────────────

        /// <summary>
        /// 주문 요청(Request) 자체가 실패했을 때 호출
        /// </summary>
        public void OnRequestFailed(string side, int band, string why)
        {
            EndOnFailed(side, band, "REQ_FAIL:" + (why ?? ""));
        }

        /// <summary>
        /// 주문전송확인에서 OrdNo가 비었거나(미접수) 예외/타임아웃 등 실패 시 호출
        /// </summary>
        public void OnOrderSendConfirmFailed(string side, int band, string why)
        {
            EndOnFailed(side, band, "주문전송확인_FAIL:" + (why ?? ""));
        }

        /// <summary>
        /// 주문 메시지(장종료 등) 기반 실패 감지 시 호출
        /// </summary>
        public void OnOrderMessageFail(string side, int band, string code, string msg)
        {
            EndOnFailed(side, band, $"ORDERMSG code={code} msg={msg}");
        }

        // ─────────────────────────────────────────────
        // 3) 체결 처리 완료 / 실패 종료
        // ─────────────────────────────────────────────

        /// <summary>
        /// 체결 처리 완료(0700 이후) 시 호출: pending 해제 + fill 쿨다운
        /// </summary>
        public void EndOnFilled(string side, int band, long execNo, string memo = null)
        {
            side = NormalizeSideKorOnly(side);
            string key = MakeKey(side, band);

            lock (_lock)
            {
                ReleasePending_NoLock(key, failCooldown: false, why: $"FILLED execNo={execNo} {memo ?? ""}".Trim());
            }
        }

        /// <summary>
        /// 주문 실패/거부/예외/타임아웃 등: pending 해제 + fail 쿨다운
        /// </summary>
        public void EndOnFailed(string side, int band, string why)
        {
            side = NormalizeSideKorOnly(side);
            string key = MakeKey(side, band);

            lock (_lock)
            {
                ReleasePending_NoLock(key, failCooldown: true, why: $"FAILED {why}");
            }
        }

        // ─────────────────────────────────────────────
        // 4) 디버그
        // ─────────────────────────────────────────────
        public string DumpStatus()
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                CleanupExpired_NoLock(now);

                var lines = new List<string>();
                lines.Add($"[0550] now={now:HH:mm:ss} pendingKeys={_pendingByKey.Count}");

                foreach (var kv in _pendingByKey.OrderBy(x => x.Key))
                {
                    var st = kv.Value;
                    lines.Add(
                        $"  key={kv.Key} pending={st.IsPending} since={st.PendingSince:HH:mm:ss} exp={st.PendingExpires:HH:mm:ss} " +
                        $"cool={(st.CooldownUntil.HasValue ? st.CooldownUntil.Value.ToString("HH:mm:ss") : "-")} lastPrice={st.LastPrice}"
                    );
                }
                return string.Join(Environment.NewLine, lines);
            }
        }

        // ─────────────────────────────────────────────
        // 내부 유틸
        // ─────────────────────────────────────────────

        private void CleanupExpired_NoLock(DateTime now)
        {
            var expired = _pendingByKey
                .Where(kv => kv.Value.IsPending && now >= kv.Value.PendingExpires)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in expired)
            {
                ReleasePending_NoLock(key, failCooldown: true, why: "PENDING TTL EXPIRED");
            }
        }

        private void ReleasePending_NoLock(string key, bool failCooldown, string why)
        {
            PendingState st;
            if (!_pendingByKey.TryGetValue(key, out st))
                return;

            st.IsPending = false;

            if (failCooldown)
            {
                if (_cooldownOnFail > TimeSpan.Zero)
                    st.CooldownUntil = DateTime.Now.Add(_cooldownOnFail);
            }
            else
            {
                if (_cooldownOnFill > TimeSpan.Zero)
                    st.CooldownUntil = DateTime.Now.Add(_cooldownOnFill);
            }

            st.PendingSince = DateTime.Now;
            st.PendingExpires = DateTime.Now;
            _pendingByKey[key] = st;

            Debug.WriteLine($"[0550] RELEASE key={key} why={why} cooldown={(st.CooldownUntil.HasValue ? st.CooldownUntil.Value.ToString("HH:mm:ss") : "-")}");
        }

        private static string MakeKey(string sideKor, int band)
            => sideKor + ":" + band.ToString(CultureInfo.InvariantCulture);

        private static string NormalizeSideKorOnly(string side)
        {
            side = (side ?? "").Trim();
            if (side == "매수" || side == "매도") return side;
            throw new ArgumentException($"side는 반드시 '매수' 또는 '매도'여야 합니다. side='{side}'");
        }

        private struct PendingState
        {
            public string Side;
            public int Band;
            public long LastPrice;

            public bool IsPending;
            public DateTime PendingSince;
            public DateTime PendingExpires;

            public DateTime? CooldownUntil;
        }
    }
}
//2026-01-17-00-00-00
