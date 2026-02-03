//using System;
//using System.Collections.Generic;
//using System.Globalization;
//using System.Reflection;
//using XA_DATASETLib;

//namespace Exercise_1
//{
//    // ------------------------------------------------------------
//    // 0650_SC1_수신처리.cs  (복붙용 / C# 7.3)
//    // ------------------------------------------------------------
//    // 역할:
//    // - SC1 실시간 체결 수신
//    // - execNo 중복 제거(dedup)
//    // - ordNo -> (side, band) 매핑 조회 (Login.OrdMap)
//    // - 0700 호출 (Login.AfterFillUpdate70)
//    // - 0550 pending 해제 (Login.TradeWait.EndOnFilled)
//    //
//    // 중요:
//    // - ✅ 당신 환경의 XAReal에는 OnReceiveRealData 이벤트가 없음 -> ReceiveRealData만 사용
//    // - ✅ SetFieldData 오버로드가 환경마다 다름 -> Reflection으로 "있는 것만" 호출
//    // ------------------------------------------------------------
//    public sealed class _0650_SC1_수신처리 : IDisposable
//    {
//        private XAReal _realSC1;
//        private bool _isAdvised;

//        private readonly object _lock = new object();
//        private readonly HashSet<long> _seenExecNos = new HashSet<long>(capacity: 4096);
//        public event Action<string, int, int, double, long> Filled;

//        public _0650_SC1_수신처리()
//        {
//        }

//        public _0650_SC1_수신처리(XAReal realSC1)
//        {
//            SetReal(realSC1);
//        }

//        public void SetReal(XAReal realSC1)
//        {
//            _realSC1 = realSC1 ?? throw new ArgumentNullException(nameof(realSC1));
//            Console.WriteLine($"[0650] SetReal OK hash={_realSC1.GetHashCode()}");
//        }

//        public void Start()
//        {
//            if (_realSC1 == null)
//            {
//                Console.WriteLine("[0650][START FAIL] _realSC1 is null. Call SetReal(real) or Start(real) first.");
//                return;
//            }

//            AttachAndAdvise();
//        }

//        public void Start(XAReal realSC1)
//        {
//            SetReal(realSC1);
//            AttachAndAdvise();
//        }

//        private void AttachAndAdvise()
//        {
//            lock (_lock)
//            {
//                Console.WriteLine("==================================================");
//                Console.WriteLine("[0650][SC1 INIT] START");
//                Console.WriteLine($"[0650][SC1 INIT] real hash={_realSC1.GetHashCode()}");

//                if (_isAdvised)
//                {
//                    Console.WriteLine("[0650][SC1 INIT] already advised -> skip");
//                    Console.WriteLine("==================================================");
//                    return;
//                }

//                // ✅ 당신 환경: ReceiveRealData 이벤트만 존재
//                _realSC1.ReceiveRealData += OnSC1_ReceiveRealData;
//                Console.WriteLine("[0650][SC1 HOOK] ReceiveRealData attached");

//                // (선택) InBlock 구독키 넣기: 오버로드 불일치로 컴파일 깨지지 않게 Reflection 호출
//                // - Login에서 이미 SetFieldData/주입을 하고 있으면, 여기서는 스킵돼도 정상입니다.
//                TrySetInBlockKeys_Safe();

//                try
//                {
//                    _realSC1.AdviseRealData();
//                    _isAdvised = true;
//                    Console.WriteLine("[0650] SC1 AdviseRealData 호출");
//                }
//                catch (Exception ex)
//                {
//                    Console.WriteLine("[0650][SC1 INIT FAIL] AdviseRealData exception: " + ex);
//                    _isAdvised = false;
//                    Console.WriteLine("==================================================");
//                    return;
//                }

//                Console.WriteLine("[0650][SC1 INIT] END");
//                Console.WriteLine("==================================================");
//            }
//        }

//        // ------------------------------------------------------------
//        // InBlock 키 넣기 (Reflection 기반: 오버로드가 뭐든 "있는 것만" 시도)
//        // ------------------------------------------------------------
//        private void TrySetInBlockKeys_Safe()
//        {
//            try
//            {
//                string acct =
//                    GetStaticStringFromLogin("AcntNo")
//                    ?? GetStaticStringFromLogin("AccountNo")
//                    ?? GetStaticStringFromLogin("계좌번호")
//                    ?? GetStaticStringFromLogin("계좌")
//                    ?? "";

//                string isu =
//                    GetStaticStringFromLogin("IsuNo")
//                    ?? GetStaticStringFromLogin("Isu")
//                    ?? GetStaticStringFromLogin("Shcode")
//                    ?? GetStaticStringFromLogin("shcode")
//                    ?? GetStaticStringFromLogin("종목코드")
//                    ?? "";

//                if (!string.IsNullOrWhiteSpace(isu))
//                {
//                    var t = isu.Trim();
//                    if (t.Length == 6 && char.IsDigit(t[0])) t = "A" + t;
//                    isu = t;
//                }

//                Console.WriteLine($"[0650][SC1 INBLOCK] acct='{acct}' isu='{isu}' (empty면 스킵)");

//                if (!string.IsNullOrWhiteSpace(acct))
//                {
//                    TrySetFieldDataAny("InBlock", "accno", 0, acct);
//                    TrySetFieldDataAny("InBlock", "AcntNo", 0, acct);
//                    TrySetFieldDataAny("InBlock", "account", 0, acct);
//                }

//                if (!string.IsNullOrWhiteSpace(isu))
//                {
//                    TrySetFieldDataAny("InBlock", "IsuNo", 0, isu);
//                    TrySetFieldDataAny("InBlock", "isu_no", 0, isu);
//                    TrySetFieldDataAny("InBlock", "shcode", 0, isu);
//                    TrySetFieldDataAny("InBlock", "shtcode", 0, isu);
//                }
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine("[0650][SC1 INBLOCK WARN] " + ex.Message);
//            }
//        }

//        // 핵심: XAReal.SetFieldData 오버로드를 "있는 것"으로만 호출
//        // - (string block, string field, int index, string value)
//        // - (string block, string field, string value)
//        // - (string field, string value)
//        private void TrySetFieldDataAny(string block, string field, int index, string value)
//        {
//            if (_realSC1 == null) return;

//            try
//            {
//                var t = _realSC1.GetType();

//                // 1) (string, string, int, string)
//                var m4 = t.GetMethod("SetFieldData", new Type[] { typeof(string), typeof(string), typeof(int), typeof(string) });
//                if (m4 != null)
//                {
//                    m4.Invoke(_realSC1, new object[] { block, field, index, value });
//                    Console.WriteLine($"[0650][SC1 INBLOCK OK] SetFieldData(block,field,int,val) {block}.{field}='{value}'");
//                    return;
//                }

//                // 2) (string, string, string)
//                var m3 = t.GetMethod("SetFieldData", new Type[] { typeof(string), typeof(string), typeof(string) });
//                if (m3 != null)
//                {
//                    m3.Invoke(_realSC1, new object[] { block, field, value });
//                    Console.WriteLine($"[0650][SC1 INBLOCK OK] SetFieldData(block,field,val) {block}.{field}='{value}'");
//                    return;
//                }

//                // 3) (string, string)
//                var m2 = t.GetMethod("SetFieldData", new Type[] { typeof(string), typeof(string) });
//                if (m2 != null)
//                {
//                    // 이 경우 block/field 개념이 없을 수도 있으니 field에 값을 넣는 방식으로 fallback
//                    m2.Invoke(_realSC1, new object[] { field, value });
//                    Console.WriteLine($"[0650][SC1 INBLOCK OK] SetFieldData(field,val) {field}='{value}'");
//                    return;
//                }

//                Console.WriteLine("[0650][SC1 INBLOCK FAIL] SetFieldData overload not found in this XAReal");
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"[0650][SC1 INBLOCK FAIL] {block}.{field}='{value}' ex={ex.Message}");
//            }
//        }

//        private static string GetStaticStringFromLogin(string name)
//        {
//            try
//            {
//                var t = typeof(Login);

//                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
//                if (p != null && p.PropertyType == typeof(string))
//                {
//                    var v = p.GetValue(null, null) as string;
//                    if (!string.IsNullOrWhiteSpace(v)) return v;
//                }

//                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
//                if (f != null && f.FieldType == typeof(string))
//                {
//                    var v = f.GetValue(null) as string;
//                    if (!string.IsNullOrWhiteSpace(v)) return v;
//                }
//            }
//            catch { }
//            return null;
//        }

//        // ------------------------------------------------------------
//        // SC1 콜백
//        // ------------------------------------------------------------
//        private void OnSC1_ReceiveRealData(string trCode)
//        {
//            Console.WriteLine($"[0650][SC1 ENTER] trCode={trCode} time={DateTime.Now:HH:mm:ss.fff}");

//            try
//            {
//                string ordNoStr = SafeTrim(_realSC1.GetFieldData("OutBlock", "ordno"));
//                string execNoStr = SafeTrim(_realSC1.GetFieldData("OutBlock", "execno"));
//                string qtyStr = SafeTrim(_realSC1.GetFieldData("OutBlock", "execqty"));
//                string prcStr = SafeTrim(_realSC1.GetFieldData("OutBlock", "execprc"));
//                string bnsTp = SafeTrim(_realSC1.GetFieldData("OutBlock", "bnstp"));

//                // 후보
//                if (string.IsNullOrWhiteSpace(ordNoStr)) ordNoStr = SafeTrim(_realSC1.GetFieldData("OutBlock", "ordno1"));
//                if (string.IsNullOrWhiteSpace(ordNoStr)) ordNoStr = SafeTrim(_realSC1.GetFieldData("OutBlock", "ordno2"));

//                Console.WriteLine($"[0650][SC1 RAW] ordno='{ordNoStr}' execno='{execNoStr}' execqty='{qtyStr}' execprc='{prcStr}' bnstp='{bnsTp}'");

//                long ordNo;
//                if (!long.TryParse(ordNoStr, out ordNo) || ordNo <= 0)
//                {
//                    Console.WriteLine($"[0650][SC1 DROP] invalid ordNo raw='{ordNoStr}'");
//                    return;
//                }

//                long execNo = 0;
//                long.TryParse(execNoStr, out execNo);

//                if (!TryAcceptExecNo(execNo))
//                {
//                    Console.WriteLine($"[0650][SC1 DUP] execNo={execNo}");
//                    return;
//                }

//                int qty;
//                if (!int.TryParse(qtyStr, out qty) || qty <= 0)
//                {
//                    Console.WriteLine($"[0650][SC1 DROP] invalid execqty raw='{qtyStr}' ordNo={ordNo}");
//                    return;
//                }

//                double price;
//                if (!double.TryParse(prcStr, NumberStyles.Any, CultureInfo.InvariantCulture, out price))
//                {
//                    var prcStr2 = (prcStr ?? "").Replace(",", "");
//                    if (!double.TryParse(prcStr2, NumberStyles.Any, CultureInfo.InvariantCulture, out price))
//                    {
//                        Console.WriteLine($"[0650][SC1 DROP] invalid execprc raw='{prcStr}' ordNo={ordNo}");
//                        return;
//                    }
//                }

//                string sideFromMap;
//                int band;

//                if (Login.OrdMap == null || !Login.OrdMap.TryGet(ordNo, out sideFromMap, out band))
//                {
//                    Console.WriteLine($"[0650][SC1 MAP FAIL] ordNo={ordNo} (0600 매핑 없음)");
//                    return;
//                }

//                string sideKor = NormalizeSideKor(sideFromMap, bnsTp);
//                if (string.IsNullOrEmpty(sideKor))
//                {
//                    Console.WriteLine($"[0650][SC1 SIDE FAIL] ordNo={ordNo} sideFromMap='{sideFromMap}' bnstp='{bnsTp}'");
//                    return;
//                }

//                Console.WriteLine($"[0650][SC1 FILLED] side={sideKor}, band={band}, qty={qty}, price={price}, execNo={execNo}, ordNo={ordNo}");

//                // 0700
//                if (Login.AfterFillUpdate70 == null)
//                {
//                    Console.WriteLine("[0650][CALL 0700 FAIL] Login.AfterFillUpdate70 is null");
//                }
//                else
//                {
//                    Console.WriteLine($"[0650][CALL 0700 START] side={sideKor} band={band} qty={qty} price={price} execNo={execNo} ordNo={ordNo}");
//                    try
//                    {
//                        Login.AfterFillUpdate70.AfterFillUpdate(band, qty, price, sideKor, execNo);
//                        Console.WriteLine("[0650][CALL 0700 END] OK");
//                    }
//                    catch (Exception ex)
//                    {
//                        Console.WriteLine("[0650][CALL 0700 FAIL] ex=" + ex);
//                    }
//                }

//                // ordNo 매핑 제거(전량 체결 가정)
//                try
//                {
//                    Login.OrdMap?.Remove(ordNo);
//                    Console.WriteLine($"[0650][SC1 MAP REMOVE] ordNo={ordNo} removed");
//                }
//                catch (Exception ex)
//                {
//                    Console.WriteLine("[0650][SC1 WARN] OrdMap.Remove failed: " + ex);
//                }

//                // 0550 해제
//                if (Login.TradeWait == null)
//                {
//                    Console.WriteLine("[0650][RELEASE 0550 FAIL] Login.TradeWait is null");
//                }
//                else
//                {
//                    Console.WriteLine($"[0650][RELEASE 0550 START] side={sideKor} band={band} execNo={execNo} ordNo={ordNo}");
//                    try
//                    {
//                        Login.TradeWait.EndOnFilled(sideKor, band, execNo, memo: $"ordNo={ordNo}");
//                        Console.WriteLine("[0650][RELEASE 0550 END] OK");
//                    }
//                    catch (Exception ex)
//                    {
//                        Console.WriteLine("[0650][RELEASE 0550 FAIL] ex=" + ex);
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine("[0650][SC1 ERROR] " + ex);
//            }
//        }

//        private static string SafeTrim(string s) => (s ?? "").Trim();

//        private bool TryAcceptExecNo(long execNo)
//        {
//            if (execNo <= 0) return true;

//            lock (_lock)
//            {
//                if (_seenExecNos.Contains(execNo))
//                    return false;

//                _seenExecNos.Add(execNo);

//                if (_seenExecNos.Count > 100000)
//                {
//                    _seenExecNos.Clear();
//                    Console.WriteLine("[0650][SC1 DEDUP] cleared (count>100000)");
//                }

//                return true;
//            }
//        }

//        private string NormalizeSideKor(string sideKorFromMap, string bnsTp)
//        {
//            var s = (sideKorFromMap ?? "").Trim();
//            if (s == "매수" || s == "매도") return s;

//            var b = (bnsTp ?? "").Trim();

//            if (b == "2") return "매수";
//            if (b == "1") return "매도";

//            if (b.IndexOf("매수", StringComparison.OrdinalIgnoreCase) >= 0) return "매수";
//            if (b.IndexOf("매도", StringComparison.OrdinalIgnoreCase) >= 0) return "매도";

//            return "";
//        }

//        public void Dispose()
//        {
//            lock (_lock)
//            {
//                try
//                {
//                    if (_realSC1 != null)
//                    {
//                        try { _realSC1.ReceiveRealData -= OnSC1_ReceiveRealData; } catch { }

//                        try
//                        {
//                            if (_isAdvised)
//                                _realSC1.UnadviseRealData();
//                        }
//                        catch { }

//                        _isAdvised = false;
//                    }

//                    _seenExecNos.Clear();
//                }
//                catch (Exception ex)
//                {
//                    Console.WriteLine("[0650][DISPOSE ERROR] " + ex);
//                }
//            }
//        }
//    }
//}
////2026-01-27 94731
///using System;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using XA_DATASETLib;

namespace Exercise_1
{
    // ------------------------------------------------------------
    // 0650_SC1_수신처리.cs  (복붙용 / C# 7.3)
    // ------------------------------------------------------------
    // 역할:
    // - SC1 실시간 체결 수신
    // - execNo 중복 제거(dedup)
    // - ordNo -> (side, band) 매핑 조회 (Login.OrdMap)
    // - 0700 호출 (Login.AfterFillUpdate70)
    // - 0550 pending 해제 (Login.TradeWait.EndOnFilled)
    // - ✅ Filled 이벤트 발생 → Login에서 UI(listView3 등) 갱신
    // ------------------------------------------------------------
    public sealed class _0650_SC1_수신처리 : IDisposable
    {
        private XAReal _realSC1;
        private bool _isAdvised;

        private readonly object _lock = new object();
        private readonly HashSet<long> _seenExecNos = new HashSet<long>(capacity: 4096);

        // ✅ Login이 구독할 체결 이벤트
        // (side, band, deltaQty, price, execNo)
        public event Action<string, int, int, double, long> Filled;

        public _0650_SC1_수신처리() { }

        public _0650_SC1_수신처리(XAReal realSC1)
        {
            SetReal(realSC1);
        }

        public void SetReal(XAReal realSC1)
        {
            _realSC1 = realSC1 ?? throw new ArgumentNullException(nameof(realSC1));
            Console.WriteLine($"[0650] SetReal OK hash={_realSC1.GetHashCode()}");
        }

        public void Start()
        {
            if (_realSC1 == null)
            {
                Console.WriteLine("[0650][START FAIL] _realSC1 is null");
                return;
            }
            AttachAndAdvise();
        }

        public void Start(XAReal realSC1)
        {
            SetReal(realSC1);
            AttachAndAdvise();
        }

        private void AttachAndAdvise()
        {
            lock (_lock)
            {
                Console.WriteLine("==================================================");
                Console.WriteLine("[0650][SC1 INIT] START");

                if (_isAdvised)
                {
                    Console.WriteLine("[0650][SC1 INIT] already advised -> skip");
                    Console.WriteLine("==================================================");
                    return;
                }

                _realSC1.ReceiveRealData += OnSC1_ReceiveRealData;
                Console.WriteLine("[0650][SC1 HOOK] ReceiveRealData attached");

                TrySetInBlockKeys_Safe();

                try
                {
                    _realSC1.AdviseRealData();
                    _isAdvised = true;
                    Console.WriteLine("[0650][SC1 INIT] AdviseRealData OK");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0650][SC1 INIT FAIL] " + ex);
                    _isAdvised = false;
                }

                Console.WriteLine("[0650][SC1 INIT] END");
                Console.WriteLine("==================================================");
            }
        }

        // ------------------------------------------------------------
        // SC1 수신 콜백
        // ------------------------------------------------------------
        private void OnSC1_ReceiveRealData(string trCode)
        {
            Console.WriteLine($"[0650][SC1 ENTER] trCode={trCode}");

            try
            {
                string ordNoStr = SafeTrim(_realSC1.GetFieldData("OutBlock", "ordno"));
                string execNoStr = SafeTrim(_realSC1.GetFieldData("OutBlock", "execno"));
                string qtyStr = SafeTrim(_realSC1.GetFieldData("OutBlock", "execqty"));
                string prcStr = SafeTrim(_realSC1.GetFieldData("OutBlock", "execprc"));
                string bnsTp = SafeTrim(_realSC1.GetFieldData("OutBlock", "bnstp"));

                long ordNo;
                if (!long.TryParse(ordNoStr, out ordNo) || ordNo <= 0)
                {
                    Console.WriteLine("[0650][DROP] invalid ordNo");
                    return;
                }

                long execNo = 0;
                long.TryParse(execNoStr, out execNo);

                if (!TryAcceptExecNo(execNo))
                {
                    Console.WriteLine($"[0650][DUP] execNo={execNo}");
                    return;
                }

                int qty;
                if (!int.TryParse(qtyStr, out qty) || qty <= 0)
                {
                    Console.WriteLine("[0650][DROP] invalid execqty");
                    return;
                }

                double price;
                if (!double.TryParse(prcStr, NumberStyles.Any, CultureInfo.InvariantCulture, out price))
                {
                    Console.WriteLine("[0650][DROP] invalid execprc");
                    return;
                }

                string sideFromMap;
                int band;
                if (Login.OrdMap == null || !Login.OrdMap.TryGet(ordNo, out sideFromMap, out band))
                {
                    Console.WriteLine($"[0650][MAP FAIL] ordNo={ordNo}");
                    return;
                }

                string sideKor = NormalizeSideKor(sideFromMap, bnsTp);
                if (string.IsNullOrEmpty(sideKor))
                {
                    Console.WriteLine("[0650][SIDE FAIL]");
                    return;
                }

                Console.WriteLine($"[0650][FILLED] side={sideKor} band={band} qty={qty} price={price} execNo={execNo}");

                // ------------------------------------------------
                // 1️⃣ 0700 처리
                // ------------------------------------------------
                Login.AfterFillUpdate70?.AfterFillUpdate(
                    band,
                    qty,
                    price,
                    sideKor,
                    execNo
                );

                // ------------------------------------------------
                // 2️⃣ OrdMap 제거
                // ------------------------------------------------
                try { Login.OrdMap?.Remove(ordNo); } catch { }

                // ------------------------------------------------
                // 3️⃣ 0550 pending 해제
                // ------------------------------------------------
                try
                {
                    Login.TradeWait?.EndOnFilled(
                        sideKor,
                        band,
                        execNo,
                        memo: $"ordNo={ordNo}"
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0650][0550 FAIL] " + ex);
                }

                // ------------------------------------------------
                // ✅ 4️⃣ Login으로 체결 이벤트 전달 (핵심)
                // ------------------------------------------------
                try
                {
                    Filled?.Invoke(sideKor, band, qty, price, execNo);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[0650][Filled Invoke FAIL] " + ex);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[0650][SC1 ERROR] " + ex);
            }
        }

        // ------------------------------------------------------------
        // 유틸
        // ------------------------------------------------------------
        private static string SafeTrim(string s) => (s ?? "").Trim();

        private bool TryAcceptExecNo(long execNo)
        {
            if (execNo <= 0) return true;

            lock (_lock)
            {
                if (_seenExecNos.Contains(execNo))
                    return false;

                _seenExecNos.Add(execNo);
                if (_seenExecNos.Count > 100000)
                    _seenExecNos.Clear();

                return true;
            }
        }

        private string NormalizeSideKor(string sideKorFromMap, string bnsTp)
        {
            if (sideKorFromMap == "매수" || sideKorFromMap == "매도")
                return sideKorFromMap;

            if (bnsTp == "2") return "매수";
            if (bnsTp == "1") return "매도";

            return "";
        }

        // ------------------------------------------------------------
        // InBlock 키 설정 (Reflection 안전 호출)
        // ------------------------------------------------------------
        private void TrySetInBlockKeys_Safe()
        {
            try
            {
                string acct = GetStaticStringFromLogin("Actno");
                string isu = GetStaticStringFromLogin("currentShcode");

                if (!string.IsNullOrWhiteSpace(acct))
                    TrySetFieldDataAny("InBlock", "accno", 0, acct);

                if (!string.IsNullOrWhiteSpace(isu))
                {
                    if (isu.Length == 6) isu = "A" + isu;
                    TrySetFieldDataAny("InBlock", "shcode", 0, isu);
                }
            }
            catch { }
        }

        private void TrySetFieldDataAny(string block, string field, int index, string value)
        {
            try
            {
                var t = _realSC1.GetType();
                var m = t.GetMethod("SetFieldData", new[] {
                    typeof(string), typeof(string), typeof(int), typeof(string)
                });
                m?.Invoke(_realSC1, new object[] { block, field, index, value });
            }
            catch { }
        }

        private static string GetStaticStringFromLogin(string name)
        {
            try
            {
                var t = typeof(Login);
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                return f?.GetValue(null) as string;
            }
            catch { }
            return null;
        }

        public void Dispose()
        {
            try
            {
                if (_realSC1 != null)
                {
                    _realSC1.ReceiveRealData -= OnSC1_ReceiveRealData;
                    if (_isAdvised) _realSC1.UnadviseRealData();
                }
            }
            catch { }

            _seenExecNos.Clear();
        }
    }
}
// 2026-01-28 94731

