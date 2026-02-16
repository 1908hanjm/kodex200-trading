// _1010_증권사보유주수.cs  (C# 7.3)
// ------------------------------------------------------------
// 역할:
// - CSPAQ12300 TR 호출
// - "보유수량"을 올바른 블록에서 읽는다:
//     ✅ CSPAQ12300OutBlock3 의 BalQty (정식 필드)
// - ✅ DEBUG 전용: ReceiveMessage / ReceiveData / BlockCount / OutBlock3 덤프
//
// 포인트(중요):
// - 이전 코드처럼 OutBlock2의 'balqty' 같은 걸 읽으면 공백이 나올 수 있음.
// - OutBlock3의 BalQty가 "보유수량" 필드다. (IsuNo, IsuNm, BalQty, AvrUprc, NowPrc 등)
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using XA_DATASETLib;

namespace Exercise_1
{
    public sealed class _1010_증권사보유주수 : IDisposable
    {
        private XAQueryClass _query;
        private bool _disposed;

        public async Task<long> RequestAsync(string account, string pwd, string shcode)
        {
            if (string.IsNullOrWhiteSpace(account))
                throw new ArgumentException("account empty");
            if (string.IsNullOrWhiteSpace(pwd))
                throw new ArgumentException("pwd empty");
            if (string.IsNullOrWhiteSpace(shcode))
                throw new ArgumentException("shcode empty");

            string acc = account.Trim();
            string pw = pwd.Trim();
            string code = NormalizeCode(shcode);

            var tcs = new TaskCompletionSource<long>();

            _query = new XAQueryClass();
            _query.LoadFromResFile(@"C:\LS_SEC\xingAPI\Res\CSPAQ12300.res");

            Debug.WriteLine("======================================");
            Debug.WriteLine("[CSPAQ12300][REQUEST START]");
            Debug.WriteLine($"ACCOUNT   : '{acc}'");
            Debug.WriteLine($"PWD LEN   : {pw.Length}");
            Debug.WriteLine($"SHCODE    : '{code}'");
            if (pw.Length != 4)
                Debug.WriteLine("[CSPAQ12300][WARN] Pwd length != 4 (계좌비번 4자리인지 확인 필요)");
            Debug.WriteLine("======================================");

            _query.ReceiveMessage += (bIsSystemError, nMessageCode, szMessage) =>
            {
                Debug.WriteLine("======================================");
                Debug.WriteLine("[CSPAQ12300][MESSAGE]");
                Debug.WriteLine($"SystemError : {bIsSystemError}");
                Debug.WriteLine($"MessageCode : {nMessageCode}");
                Debug.WriteLine($"MessageText : {szMessage}");
                Debug.WriteLine("======================================");
            };

            _query.ReceiveData += (trCode) =>
            {
                try
                {
                    Debug.WriteLine("======================================");
                    Debug.WriteLine("[CSPAQ12300][RECEIVE DATA]");
                    Debug.WriteLine($"TRCODE : {trCode}");
                    Debug.WriteLine("======================================");

                    DumpBlockCount("CSPAQ12300OutBlock1");
                    DumpBlockCount("CSPAQ12300OutBlock2");
                    DumpBlockCount("CSPAQ12300OutBlock3");
                    DumpBlockCount("CSPAQ12300OutBlock4");

                    // (선택) OutBlock1/2 핵심 필드 몇 개만 덤프
                    DumpFields_OneRow("CSPAQ12300OutBlock1", 0, new[]
                    {
                        "rspcode","rspmsg","msgcode","msg","message","errmsg",
                        "AcntNo","acntno","RecCnt","recnt","IsuNo","isuNo","IsuCode","shcode","expcode"
                    });

                    DumpFields_OneRow("CSPAQ12300OutBlock2", 0, new[]
                    {
                        "balqty","qty","janqty","hldgqty","posqty","sunamt","totamt","evlamt","pamt","appamt",
                        "dps","d2","d2amt","d2bal","marg","margamt","ordableamt","ordablecash","rate",
                        "AcntNo","acntno"
                    });

                    // ✅ 핵심: OutBlock3에서 종목별 보유수량을 찾는다.
                    long resultQty = FindQtyFromOutBlock3_ByIsuNo(code);

                    Debug.WriteLine("======================================");
                    Debug.WriteLine("[CSPAQ12300][FINAL]");
                    Debug.WriteLine($"RESULT QTY : {resultQty}");
                    Debug.WriteLine("======================================");

                    tcs.TrySetResult(resultQty);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[CSPAQ12300][RECEIVE DATA ERROR] " + ex);
                    tcs.TrySetException(ex);
                }
            };

            // InBlock 세팅
            _query.SetFieldData("CSPAQ12300InBlock1", "RecCnt", 0, "1");
            _query.SetFieldData("CSPAQ12300InBlock1", "AcntNo", 0, acc);
            _query.SetFieldData("CSPAQ12300InBlock1", "Pwd", 0, pw);
            _query.SetFieldData("CSPAQ12300InBlock1", "BalCreTp", 0, "0");
            _query.SetFieldData("CSPAQ12300InBlock1", "CmsnAppTpCode", 0, "0");
            _query.SetFieldData("CSPAQ12300InBlock1", "D2balBaseQryTp", 0, "0");
            _query.SetFieldData("CSPAQ12300InBlock1", "UprcTpCode", 0, "0");

            Debug.WriteLine("[CSPAQ12300][INBLOCK SET COMPLETE]");

            int requestResult = _query.Request(false);
            Debug.WriteLine($"[CSPAQ12300][REQUEST RETURN] r={requestResult}");

            if (requestResult < 0)
                throw new Exception("CSPAQ12300 Request 실패 (r<0)");

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(10000));
            if (completed != tcs.Task)
                throw new TimeoutException("CSPAQ12300 Timeout");

            return await tcs.Task;
        }

        private long FindQtyFromOutBlock3_ByIsuNo(string targetShcodeNormalized)
        {
            int cnt = SafeGetBlockCount("CSPAQ12300OutBlock3");
            Debug.WriteLine("---------- [CSPAQ12300OutBlock3 Dump] ----------");
            Debug.WriteLine($"[CSPAQ12300OutBlock3] cnt={cnt}");

            long foundQty = 0;
            bool foundTargetRow = false;

            for (int i = 0; i < cnt; i++)
            {
                string isuNoRaw = GetField("CSPAQ12300OutBlock3", "IsuNo", i);
                if (string.IsNullOrWhiteSpace(isuNoRaw))
                    isuNoRaw = GetField("CSPAQ12300OutBlock3", "isuNo", i); // 혹시 몰라서

                string isuNm = GetField("CSPAQ12300OutBlock3", "IsuNm", i);
                if (string.IsNullOrWhiteSpace(isuNm))
                    isuNm = GetField("CSPAQ12300OutBlock3", "isuNm", i);

                string isuNoNorm = NormalizeCode(isuNoRaw);

                Debug.WriteLine($"[CSPAQ12300OutBlock3] i={i} IsuNo='{isuNoRaw}'(norm='{isuNoNorm}') IsuNm='{isuNm}'");

                // 수량 후보 (정식: BalQty)
                string qtyRaw = FirstNonEmpty(
                    GetField("CSPAQ12300OutBlock3", "BalQty", i),
                    GetField("CSPAQ12300OutBlock3", "balqty", i),
                    GetField("CSPAQ12300OutBlock3", "Qty", i),
                    GetField("CSPAQ12300OutBlock3", "qty", i),
                    GetField("CSPAQ12300OutBlock3", "Janqty", i),
                    GetField("CSPAQ12300OutBlock3", "janqty", i),
                    GetField("CSPAQ12300OutBlock3", "Hldgqty", i),
                    GetField("CSPAQ12300OutBlock3", "hldgqty", i)
                );

                Debug.WriteLine($"[CSPAQ12300OutBlock3] i={i} qtyRaw(Candidates)='{qtyRaw}'");

                // 타겟 종목이면 상세 덤프
                if (!string.IsNullOrWhiteSpace(targetShcodeNormalized) &&
                    !string.IsNullOrWhiteSpace(isuNoNorm) &&
                    isuNoNorm == targetShcodeNormalized)
                {
                    foundTargetRow = true;

                    Debug.WriteLine($"[CSPAQ12300OutBlock3][MATCH] target='{targetShcodeNormalized}' row={i} -> dump row candidates");

                    DumpFields_OneRow("CSPAQ12300OutBlock3", i, new[]
                    {
                        "IsuNo","IsuNm",
                        "BalQty","BnsBaseBalQty",
                        "AvrUprc","NowPrc","PchsAmt",
                        "SellAbleQty","OrdAbleAmt","MnyOrdAbleAmt",
                        "balqty","qty","janqty","hldgqty","posqty",
                        "expcode","shcode","isu_no","stkcode","name"
                    });

                    long parsed = ParseLongSafe(qtyRaw);
                    Debug.WriteLine($"[CSPAQ12300OutBlock3][MATCH] qtyRaw='{qtyRaw}' parsed={parsed}");

                    // 타겟 종목은 즉시 반환(원하시면 누적합으로 바꿔도 됨)
                    foundQty = parsed;
                    break;
                }
            }

            if (!foundTargetRow)
            {
                Debug.WriteLine($"[CSPAQ12300OutBlock3][WARN] target '{targetShcodeNormalized}' row not found. (계좌에 해당 종목 보유 없거나, 응답 포맷/필드명 점검 필요)");
            }

            Debug.WriteLine("----------------------------------------");
            return foundQty;
        }

        private void DumpBlockCount(string blockName)
        {
            int cnt = SafeGetBlockCount(blockName);
            Debug.WriteLine($"[CSPAQ12300][BLOCKCOUNT] {blockName} cnt={cnt}");
        }

        private int SafeGetBlockCount(string blockName)
        {
            try { return _query.GetBlockCount(blockName); }
            catch { return -1; }
        }

        private void DumpFields_OneRow(string blockName, int row, string[] fields)
        {
            try
            {
                int cnt = SafeGetBlockCount(blockName);
                if (cnt <= 0 || row < 0 || row >= cnt)
                {
                    Debug.WriteLine($"---------- [{blockName} Candidates] row={row} (no rows) ----------");
                    return;
                }

                Debug.WriteLine($"---------- [{blockName} Candidates] ({blockName}[{row}]) ----------");
                foreach (var f in fields)
                {
                    string v = "";
                    try { v = _query.GetFieldData(blockName, f, row) ?? ""; } catch { v = ""; }
                    v = v.Trim();
                    if (!string.IsNullOrEmpty(v))
                        Debug.WriteLine($"[FIELD] {f} = '{v}'");
                }
                Debug.WriteLine("--------------------------------------------------------");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DUMP][ERROR] block={blockName} row={row} ex={ex.Message}");
            }
        }

        private string GetField(string blockName, string fieldName, int row)
        {
            try
            {
                return (_query.GetFieldData(blockName, fieldName, row) ?? "").Trim();
            }
            catch
            {
                return "";
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return "";
            for (int i = 0; i < values.Length; i++)
            {
                var v = (values[i] ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return "";
        }

        private static long ParseLongSafe(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;

            // 콤마 제거 등
            raw = raw.Trim().Replace(",", "");

            if (long.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out long v))
                return v;

            // 숫자만 남겨보기
            var sb = new System.Text.StringBuilder();
            foreach (char c in raw)
            {
                if (char.IsDigit(c) || c == '-' || c == '+')
                    sb.Append(c);
            }

            if (long.TryParse(sb.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                return v;

            return 0;
        }

        private static string NormalizeCode(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";

            s = s.Trim();

            // 흔한 케이스: "A069500" / "069500 " 등
            if (s.Length >= 2 && (s[0] == 'A' || s[0] == 'a') && char.IsDigit(s[1]))
                s = s.Substring(1);

            // 숫자만 남김
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (char.IsDigit(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_query != null)
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_query);
            }
            catch { }
        }
    }
}

// 2026-02-10 59381
