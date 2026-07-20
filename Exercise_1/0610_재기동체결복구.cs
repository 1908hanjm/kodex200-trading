using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Globalization;

namespace Exercise_1
{
    public sealed class RestartRecoveryOrder
    {
        public long Id;
        public string OrderDate;
        public long OrdNo;
        public string Side;
        public string TradeType;
        public int Band;
        public long FromBand;
        public long FromQty;
        public long ExtraQty;
        public int OrderQty;
        public int CumFill;
        public int Remain;
        public bool Complete;
        public bool DbApplied;
        public string ExecCsv;
        public string RecoverySource;
    }

    /// <summary>
    /// 정상 주문 흐름을 변경하지 않고 재기동 복구용 영속 원장만 기록한다.
    /// 이 클래스의 모든 public 메서드는 예외를 외부로 전파하지 않는다.
    /// </summary>
    public static class RestartExecutionRecovery
    {
        private const string TableName = "재기동체결복구";
        private static readonly object Sync = new object();
        private static readonly string ProcessSessionId =
            DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) +
            "_PID" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

        private sealed class ExistingOrder
        {
            public long Id;
            public string Side;
            public int Band;
            public int OrderQty;
            public string SessionId;
        }

        public static string SessionId
        {
            get { return ProcessSessionId; }
        }

        public static bool ExistsOrder(string orderDate, long ordNo)
        {
            try
            {
                lock (Sync)
                {
                    using (var conn = Open())
                    {
                        return ReadExisting(conn, orderDate, ordNo) != null;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("ExistsOrder", ordNo, ex);
                return false;
            }
        }

        public static bool InsertAck(
            long ordNo,
            string shcode,
            string side,
            string tradeType,
            int band,
            long fromBand,
            long fromQty,
            long extraQty,
            int orderQty,
            double orderPrice,
            long originalOrdNo = 0,
            string note = "")
        {
            if (ordNo <= 0 || orderQty <= 0)
                return false;

            string orderDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string nowTime = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string normalizedSide = NormalizeSide(side);

            try
            {
                lock (Sync)
                {
                    using (var conn = Open())
                    using (var tx = conn.BeginTransaction())
                    {
                        ExistingOrder old = ReadExisting(conn, orderDate, ordNo, tx);
                        if (old != null)
                        {
                            bool sameSession = string.Equals(
                                old.SessionId ?? "",
                                ProcessSessionId,
                                StringComparison.Ordinal);

                            if (!string.Equals(old.Side, normalizedSide, StringComparison.OrdinalIgnoreCase) ||
                                old.Band != band || old.OrderQty != orderQty)
                            {
                                Log("[RESTART_RECOVERY][INSERT_CONFLICT] " +
                                    "ordNo=" + ordNo +
                                    " oldSide=" + old.Side + " newSide=" + normalizedSide +
                                    " oldBand=" + old.Band + " newBand=" + band +
                                    " oldQty=" + old.OrderQty + " newQty=" + orderQty);
                            }

                            using (var touch = conn.CreateCommand())
                            {
                                touch.Transaction = tx;
                                touch.CommandText =
                                    "UPDATE [" + TableName + "] SET [최종수정시각]=@now WHERE [id]=@id";
                                touch.Parameters.AddWithValue("@now", nowTime);
                                touch.Parameters.AddWithValue("@id", old.Id);
                                touch.ExecuteNonQuery();
                            }

                            tx.Commit();
                            if (!sameSession)
                            {
                                Log("[RECOVERY_INSERT][SKIP_EXISTS_DIFFERENT_SESSION] " +
                                    "ordNo=" + ordNo +
                                    " oldSessionId=" + (old.SessionId ?? "") +
                                    " currentSessionId=" + ProcessSessionId);
                            }

                            Log("[RESTART_RECOVERY][INSERT_SKIP_EXISTS] ordNo=" + ordNo +
                                " date=" + orderDate +
                                " sameSession=" + sameSession);
                            return sameSession;
                        }

                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText =
                                "INSERT INTO [" + TableName + "] (" +
                                "[주문일자],[주문번호],[원주문번호],[주문시각],[최종수정시각]," +
                                "[종목코드],[매매구분],[거래유형],[밴드번호],[from_band],[from_qty],[extra_qty]," +
                                "[주문수량],[누적체결수량],[잔여수량],[주문가격],[평균체결가격],[주문상태]," +
                                "[체결완료여부],[취소여부],[체결번호목록],[세션ID],[DB반영완료],[DB반영시각],[복구출처],[비고]) " +
                                "VALUES (@date,@ordNo,@original,@orderTime,@modified,@shcode,@side,@tradeType,@band," +
                                "@fromBand,@fromQty,@extraQty,@orderQty,0,@remain,@price,@avgPrice,'ACK',0,0,''," +
                                "@sessionId,0,@dbAppliedTime,'ACK',@note)";
                            cmd.Parameters.AddWithValue("@date", orderDate);
                            cmd.Parameters.AddWithValue("@ordNo", ordNo);
                            cmd.Parameters.AddWithValue("@original", originalOrdNo > 0 ? (object)originalOrdNo : DBNull.Value);
                            cmd.Parameters.AddWithValue("@orderTime", nowTime);
                            cmd.Parameters.AddWithValue("@modified", nowTime);
                            cmd.Parameters.AddWithValue("@shcode", (shcode ?? "").Trim());
                            cmd.Parameters.AddWithValue("@side", normalizedSide);
                            cmd.Parameters.AddWithValue("@tradeType", (tradeType ?? "").Trim());
                            cmd.Parameters.AddWithValue("@band", band);
                            cmd.Parameters.AddWithValue("@fromBand", fromBand);
                            cmd.Parameters.AddWithValue("@fromQty", fromQty);
                            cmd.Parameters.AddWithValue("@extraQty", extraQty);
                            cmd.Parameters.AddWithValue("@orderQty", orderQty);
                            cmd.Parameters.AddWithValue("@remain", orderQty);
                            cmd.Parameters.AddWithValue("@price", orderPrice);
                            cmd.Parameters.AddWithValue("@avgPrice", DBNull.Value);
                            cmd.Parameters.AddWithValue("@sessionId", ProcessSessionId);
                            cmd.Parameters.AddWithValue("@dbAppliedTime", DBNull.Value);
                            cmd.Parameters.AddWithValue("@note", note ?? "");
                            cmd.ExecuteNonQuery();
                        }

                        tx.Commit();
                    }
                }

                Log("[RESTART_RECOVERY][INSERT] " +
                    "ordNo=" + ordNo + " side=" + normalizedSide + " band=" + band +
                    " qty=" + orderQty + " price=" + orderPrice + " tradeType=" + (tradeType ?? "") +
                    " from_band=" + fromBand + " from_qty=" + fromQty + " extra_qty=" + extraQty +
                    " sessionId=" + ProcessSessionId);
                return true;
            }
            catch (Exception ex)
            {
                LogError("InsertAck", ordNo, ex);
                return false;
            }
        }

        public static bool AppendExecution(
            long ordNo,
            long execNo,
            int fillQty,
            double fillPrice,
            string side,
            int band,
            int orderQty,
            int ordMapCumFill,
            int ordMapRemain,
            bool complete,
            int chainExtendFromBand = 0,
            int chainExtendTargetBand = 0)
        {
            if (ordNo <= 0 || execNo <= 0 || fillQty <= 0)
                return false;

            string orderDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string nowTime = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

            try
            {
                lock (Sync)
                {
                    using (var conn = Open())
                    using (var tx = conn.BeginTransaction())
                    using (var read = conn.CreateCommand())
                    {
                        read.Transaction = tx;
                        read.CommandText =
                            "SELECT [id],[주문수량],[누적체결수량],[평균체결가격],[체결번호목록] " +
                            "FROM [" + TableName + "] WHERE [주문일자]=@date AND [주문번호]=@ordNo " +
                            "ORDER BY [id] DESC LIMIT 1";
                        read.Parameters.AddWithValue("@date", orderDate);
                        read.Parameters.AddWithValue("@ordNo", ordNo);

                        long id;
                        int storedOrderQty;
                        int oldCum;
                        double oldAverage;
                        string csv;
                        using (var rd = read.ExecuteReader())
                        {
                            if (!rd.Read())
                            {
                                tx.Commit();
                                Log("[RESTART_RECOVERY][SC_UPDATE_MISS] ordNo=" + ordNo +
                                    " execNo=" + execNo + " reason=no_row_in_recovery_table");
                                return false;
                            }

                            id = Convert.ToInt64(rd["id"], CultureInfo.InvariantCulture);
                            storedOrderQty = ToInt(rd["주문수량"]);
                            oldCum = ToInt(rd["누적체결수량"]);
                            oldAverage = ToDouble(rd["평균체결가격"]);
                            csv = rd["체결번호목록"] == DBNull.Value ? "" : Convert.ToString(rd["체결번호목록"]);
                        }

                        if (SafeCsvContainsExecNo(csv, execNo))
                        {
                            tx.Commit();
                            Log("[RESTART_RECOVERY][DUP_EXEC_SKIP] ordNo=" + ordNo +
                                " execNo=" + execNo + " fillQty=" + fillQty);
                            return false;
                        }

                        int effectiveOrderQty = storedOrderQty > 0 ? storedOrderQty : orderQty;
                        int newCum = oldCum + fillQty;
                        if (effectiveOrderQty > 0 && newCum > effectiveOrderQty)
                            newCum = effectiveOrderQty;
                        int newRemain = effectiveOrderQty > newCum ? effectiveOrderQty - newCum : 0;
                        bool filled = complete || newRemain == 0;
                        string status = filled ? "FILLED" : "PARTIAL";
                        string newCsv = string.IsNullOrWhiteSpace(csv)
                            ? execNo.ToString(CultureInfo.InvariantCulture)
                            : csv.Trim().TrimEnd(',') + "," + execNo.ToString(CultureInfo.InvariantCulture);
                        double newAverage = newCum > 0
                            ? ((oldAverage * oldCum) + (fillPrice * fillQty)) / newCum
                            : fillPrice;

                        using (var update = conn.CreateCommand())
                        {
                            update.Transaction = tx;
                            bool updateChainExtendQty = chainExtendFromBand > 0 && chainExtendTargetBand > 0;
                            update.CommandText =
                                "UPDATE [" + TableName + "] SET " +
                                "[누적체결수량]=@cum,[잔여수량]=@remain,[평균체결가격]=@avg," +
                                "[주문상태]=@status,[체결완료여부]=@complete,[체결번호목록]=@csv," +
                                "[최종수정시각]=@now,[복구출처]='SC'" +
                                (updateChainExtendQty ? ",[from_band]=@chainFromBand,[from_qty]=@chainFromQty" : "") +
                                " WHERE [id]=@id";
                            update.Parameters.AddWithValue("@cum", newCum);
                            update.Parameters.AddWithValue("@remain", newRemain);
                            update.Parameters.AddWithValue("@avg", newAverage);
                            update.Parameters.AddWithValue("@status", status);
                            update.Parameters.AddWithValue("@complete", filled ? 1 : 0);
                            update.Parameters.AddWithValue("@csv", newCsv);
                            update.Parameters.AddWithValue("@now", nowTime);
                            if (updateChainExtendQty)
                            {
                                update.Parameters.AddWithValue("@chainFromBand", chainExtendFromBand);
                                update.Parameters.AddWithValue("@chainFromQty", newCum);
                            }
                            update.Parameters.AddWithValue("@id", id);
                            update.ExecuteNonQuery();
                        }

                        tx.Commit();
                        Log("[RESTART_RECOVERY][SC_UPDATE] ordNo=" + ordNo +
                            " execNo=" + execNo + " fillQty=" + fillQty + " price=" + fillPrice +
                            " cumFill=" + newCum + " remain=" + newRemain + " status=" + status +
                            " ordMapCum=" + ordMapCumFill + " ordMapRemain=" + ordMapRemain +
                            " side=" + NormalizeSide(side) + " band=" + band);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("AppendExecution", ordNo, ex);
                return false;
            }
        }

        public static bool MarkDbApplied(
            long ordNo,
            long execNo,
            int band,
            string side,
            int fillQty,
            int cumFill,
            int remain,
            string source = "ORDMAP")
        {
            if (ordNo <= 0)
            {
                Log("[RESTART_RECOVERY][DB_APPLIED_DEFERRED] reason=ordNo_not_available_in_0700 " +
                    "side=" + NormalizeSide(side) + " band=" + band + " fillQty=" + fillQty);
                LogDbApplyMarkFail(ordNo, execNo, band, side, "ordNo_not_available");
                return false;
            }

            string orderDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string nowTime = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

            try
            {
                int affected;
                lock (Sync)
                {
                    using (var conn = Open())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "UPDATE [" + TableName + "] SET [DB반영완료]=1,[DB반영시각]=@now," +
                            "[복구출처]=@source,[최종수정시각]=@now " +
                            "WHERE [주문일자]=@date AND [주문번호]=@ordNo";
                        cmd.Parameters.AddWithValue("@now", nowTime);
                        cmd.Parameters.AddWithValue("@source", string.IsNullOrWhiteSpace(source) ? "ORDMAP" : source.Trim());
                        cmd.Parameters.AddWithValue("@date", orderDate);
                        cmd.Parameters.AddWithValue("@ordNo", ordNo);
                        affected = cmd.ExecuteNonQuery();
                    }
                }

                if (affected <= 0)
                {
                    Log("[RESTART_RECOVERY][ERROR] where=MarkDbApplied ordNo=" + ordNo +
                        " message=no_row_in_recovery_table");
                    LogDbApplyMarkFail(ordNo, execNo, band, side, "no_row_in_recovery_table");
                    return false;
                }

                Log("[RESTART_RECOVERY][DB_APPLIED] ordNo=" + ordNo + " band=" + band +
                    " side=" + NormalizeSide(side) + " fillQty=" + fillQty +
                    " cumFill=" + cumFill + " remain=" + remain + " dbApplied=1");
                return true;
            }
            catch (Exception ex)
            {
                LogError("MarkDbApplied", ordNo, ex);
                LogDbApplyMarkFail(ordNo, execNo, band, side,
                    ex.GetType().Name + ":" + (ex.Message ?? "unknown"));
                return false;
            }
        }

        private static void LogDbApplyMarkFail(
            long ordNo, long execNo, int band, string side, string reason)
        {
            string safeReason = (reason ?? "unknown").Replace('\r', ' ').Replace('\n', ' ');
            Log("[RESTART_RECOVERY][DB_APPLY_MARK_FAIL] ordNo=" + ordNo +
                " execNo=" + execNo + " band=" + band +
                " side=" + NormalizeSide(side) + " reason=" + safeReason);
        }

        public static bool SafeCsvContainsExecNo(string csv, long execNo)
        {
            if (execNo <= 0 || string.IsNullOrWhiteSpace(csv))
                return false;

            string expected = execNo.ToString(CultureInfo.InvariantCulture);
            string[] tokens = csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (string.Equals(tokens[i].Trim(), expected, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public static bool TryLoadPendingOrders(
            out List<RestartRecoveryOrder> rows,
            out string reason)
        {
            rows = new List<RestartRecoveryOrder>();
            reason = "";
            try
            {
                lock (Sync)
                {
                    using (var conn = Open())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT * FROM [" + TableName + "] " +
                            "WHERE ([체결완료여부]=0 OR [DB반영완료]=0) " +
                            "AND [취소여부]=0 " +
                            "ORDER BY [주문일자] ASC,[주문시각] ASC";
                        using (var rd = cmd.ExecuteReader())
                        {
                            while (rd.Read()) rows.Add(ReadRecoveryRow(rd));
                        }
                    }
                }

                Log("[RESTART_RECOVERY][BOOT_SCAN] count=" + rows.Count);
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetBaseException().Message;
                LogError("BootScan", 0, ex);
                return false;
            }
        }

        public static bool TryGetTodayOrder(
            long ordNo,
            out RestartRecoveryOrder row,
            out string reason)
        {
            row = null;
            reason = "";
            if (ordNo <= 0) { reason = "invalid_ordNo"; return false; }

            try
            {
                string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                lock (Sync)
                {
                    using (var conn = Open())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT * FROM [" + TableName + "] " +
                            "WHERE [주문일자]=@date AND [주문번호]=@ordNo AND [취소여부]=0 " +
                            "ORDER BY [id] DESC LIMIT 1";
                        cmd.Parameters.AddWithValue("@date", date);
                        cmd.Parameters.AddWithValue("@ordNo", ordNo);
                        using (var rd = cmd.ExecuteReader())
                        {
                            if (rd.Read()) row = ReadRecoveryRow(rd);
                        }
                    }
                }
                reason = row == null ? "no_row_in_recovery_table" : "";
                return row != null;
            }
            catch (Exception ex)
            {
                reason = ex.GetBaseException().Message;
                LogError("GetTodayOrder", ordNo, ex);
                return false;
            }
        }

        public static bool TryHasExecution(
            long ordNo,
            long execNo,
            out bool exists,
            out string reason)
        {
            exists = false;
            reason = "";
            RestartRecoveryOrder row;
            if (!TryGetTodayOrder(ordNo, out row, out reason))
                return reason == "no_row_in_recovery_table";

            exists = SafeCsvContainsExecNo(row.ExecCsv, execNo);
            return true;
        }

        // =====================================================================
        // ✅ [FIX 2026-07-15] T0425 대조 정정 (RECONCILE)
        //
        // 문제: TryLoadPendingOrders는 로컬에 마지막으로 저장된 체결 상태를
        // "사실"로 믿고 그대로 복원한다. 그런데 앱이 재기동되는 사이(또는 SC1
        // 수신이 끊긴 사이) 증권사 쪽에서는 주문이 이미 전량체결됐을 수 있고,
        // 그 구간의 체결 통지를 로컬이 못 받았다면 로컬 원장은 영영 stale한
        // 채로 남는다 (예: ordNo=9513, 로컬 cumFill=62/474, t0425 cheqty=474).
        //
        // 이 함수는 부팅 시 TryLoadPendingOrders() 결과와, t0425 조회로 받은
        // 실제 증권사 체결 결과를 대조한다. 증권사가 "전량체결"이라고 확인해준
        // 주문인데 로컬이 아직 미완결로 믿고 있다면:
        //   1) 부족분(brokerCheQty - localCumFill)을 applyCatchUpFill 델리게이트로
        //      넘겨 기존 체결 반영 파이프라인(0700 AfterFillUpdate 등)을 그대로
        //      태워서 band qty/from_band 등이 정상 경로로 갱신되게 한다.
        //   2) 위가 성공하면 로컬 복구 원장(재기동체결복구)도 체결완료로 정정한다.
        //   3) 실패하면 기존 로컬 상태를 그대로 pending 목록에 남겨 기존 동작을
        //      보존한다 (안전 폴백 - 이 함수가 실패해도 이전 동작보다 나빠지지 않음).
        //
        // 주의: 이 함수는 "탐지 + 위임"만 한다. 실제 band DB 반영/체결완료 처리는
        // applyCatchUpFill 델리게이트(호출부에서 0700 인스턴스에 바인딩)가 담당한다.
        // 이렇게 해야 qty 갱신, from_band 정리 등 기존 검증된 로직을 그대로 타고,
        // 이 파일이 그 로직을 중복 구현하거나 우회하지 않는다.
        //
        // 호출 위치: 기존에 TryLoadPendingOrders() 결과로 OrdMap을 복원하기 전,
        // t0425 부팅 조회 결과가 준비된 시점(예: Login_04)에서
        //     rows = RestartExecutionRecovery.ReconcileWithT0425(rows, t0425Rows, applyCatchUpFill);
        // 로 rows를 한 번 정정한 뒤 기존 RESTORE_ORDMAP 로직에 넘기면 된다.
        // =====================================================================

        /// <summary>
        /// t0425 한 행에서 이 함수가 필요로 하는 최소 정보만 담는 DTO.
        /// 0900 등에서 파싱한 t0425 결과를 이 타입으로 매핑해서 넘기면 된다.
        /// </summary>
        public sealed class T0425BrokerFill
        {
            public long OrdNo;
            public long CheQty;      // 증권사 누적 체결수량 (cheqty)
            public long RemainQty;   // 증권사 미체결 잔량 (ordrem 등에서 파싱한 값)
            public double Price;     // 체결가격 (평균 또는 마지막 체결가)
            public string Status;    // 예: "체결"
        }

        /// <summary>
        /// 미반영분(브로커 체결 - 로컬 체결)을 실제 체결 파이프라인에 반영하기 위한 델리게이트.
        /// 반환값 true면 반영 성공(=이 주문을 pending에서 제외).
        /// 호출부에서 0700._up0700.AfterFillUpdate(...) 등에 바인딩해서 넘긴다.
        /// </summary>
        public delegate bool ApplyCatchUpFill(
            RestartRecoveryOrder localRow,
            long missingQty,
            double brokerPrice);

        public static List<RestartRecoveryOrder> ReconcileWithT0425(
            List<RestartRecoveryOrder> pendingRows,
            IEnumerable<T0425BrokerFill> brokerRows,
            ApplyCatchUpFill applyCatchUpFill)
        {
            var stillPending = new List<RestartRecoveryOrder>();

            if (pendingRows == null || pendingRows.Count == 0)
                return stillPending;

            // ordNo -> broker row 매핑 (동일 ordNo가 여러 번 나오면 마지막 것 사용)
            var brokerByOrdNo = new Dictionary<long, T0425BrokerFill>();
            if (brokerRows != null)
            {
                foreach (var br in brokerRows)
                {
                    if (br == null || br.OrdNo <= 0) continue;
                    brokerByOrdNo[br.OrdNo] = br;
                }
            }

            foreach (var row in pendingRows)
            {
                T0425BrokerFill match;
                if (row == null || !brokerByOrdNo.TryGetValue(row.OrdNo, out match))
                {
                    // t0425에 없음 -> 이 함수 범위 밖(취소/오래된 주문 등). 기존 로직 그대로.
                    if (row != null) stillPending.Add(row);
                    continue;
                }

                bool brokerSaysComplete =
                    string.Equals(match.Status, "체결", StringComparison.Ordinal) &&
                    match.CheQty > row.CumFill &&
                    match.RemainQty <= 0;

                if (!brokerSaysComplete)
                {
                    // 브로커도 아직 로컬과 같은 수준이거나 미체결 -> 정정 불필요.
                    stillPending.Add(row);
                    continue;
                }

                long missingQty = match.CheQty - row.CumFill;

                Log("[RESTART_RECOVERY][T0425_RECONCILE][STALE_DETECTED] " +
                    "ordNo=" + row.OrdNo +
                    " band=" + row.Band +
                    " localCumFill=" + row.CumFill +
                    " localRemain=" + row.Remain +
                    " brokerCheQty=" + match.CheQty +
                    " brokerRemainQty=" + match.RemainQty +
                    " missingQty=" + missingQty);

                bool applied = false;
                try
                {
                    applied = applyCatchUpFill != null &&
                              applyCatchUpFill(row, missingQty, match.Price);
                }
                catch (Exception ex)
                {
                    LogError("T0425Reconcile_ApplyCatchUpFill", row.OrdNo, ex);
                    applied = false;
                }

                if (!applied)
                {
                    Log("[RESTART_RECOVERY][T0425_RECONCILE][APPLY_FAILED] ordNo=" + row.OrdNo +
                        " -> 기존 로컬 상태(pending) 유지, 자동매매 중단 없이 다음 라운드에 재시도 필요");
                    stillPending.Add(row);
                    continue;
                }

                bool marked = TryMarkCompleteFromBroker(row.OrdNo, match.CheQty, match.Price, out string markReason);
                if (!marked)
                {
                    Log("[RESTART_RECOVERY][T0425_RECONCILE][MARK_FAILED] ordNo=" + row.OrdNo +
                        " reason=" + markReason +
                        " (band DB는 이미 갱신됨 -> 로컬 원장만 불일치 가능, 수동 확인 필요)");
                }
                else
                {
                    Log("[RESTART_RECOVERY][T0425_RECONCILE][APPLIED] ordNo=" + row.OrdNo +
                        " cumFill=" + match.CheQty + " avgPrice=" + match.Price);
                }

                // 반영 성공 -> 더 이상 pending 목록에 포함하지 않는다 (RESTORE_ORDMAP 대상 제외).
            }

            return stillPending;
        }

        private static bool TryMarkCompleteFromBroker(
            long ordNo,
            long brokerCumFill,
            double brokerAvgPrice,
            out string reason)
        {
            reason = "";
            try
            {
                string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                lock (Sync)
                {
                    using (var conn = Open())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "UPDATE [" + TableName + "] SET " +
                            "[누적체결수량]=@cum,[잔여수량]=0,[평균체결가격]=@avg," +
                            "[주문상태]='체결',[체결완료여부]=1," +
                            "[복구출처]='T0425_RECONCILE'," +
                            "[비고]='로컬 미수신 체결분을 t0425 기준으로 정정 (2026-07-15 FIX)' " +
                            "WHERE [주문일자]=@date AND [주문번호]=@ordNo";
                        cmd.Parameters.AddWithValue("@cum", brokerCumFill);
                        cmd.Parameters.AddWithValue("@avg", brokerAvgPrice);
                        cmd.Parameters.AddWithValue("@date", date);
                        cmd.Parameters.AddWithValue("@ordNo", ordNo);

                        int affected = cmd.ExecuteNonQuery();
                        if (affected <= 0)
                        {
                            reason = "no_row_updated";
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetBaseException().Message;
                LogError("MarkCompleteFromBroker", ordNo, ex);
                return false;
            }
        }

        private static SQLiteConnection Open()
        {
            var conn = new SQLiteConnection(Login.ConnStr);
            conn.Open();
            return conn;
        }

        private static ExistingOrder ReadExisting(
            SQLiteConnection conn,
            string orderDate,
            long ordNo,
            SQLiteTransaction tx = null)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "SELECT [id],[매매구분],[밴드번호],[주문수량],[세션ID] FROM [" + TableName + "] " +
                    "WHERE [주문일자]=@date AND [주문번호]=@ordNo ORDER BY [id] DESC LIMIT 1";
                cmd.Parameters.AddWithValue("@date", orderDate ?? "");
                cmd.Parameters.AddWithValue("@ordNo", ordNo);
                using (var rd = cmd.ExecuteReader())
                {
                    if (!rd.Read())
                        return null;
                    return new ExistingOrder
                    {
                        Id = Convert.ToInt64(rd["id"], CultureInfo.InvariantCulture),
                        Side = rd["매매구분"] == DBNull.Value ? "" : Convert.ToString(rd["매매구분"]),
                        Band = ToInt(rd["밴드번호"]),
                        OrderQty = ToInt(rd["주문수량"]),
                        SessionId = rd["세션ID"] == DBNull.Value ? "" : Convert.ToString(rd["세션ID"])
                    };
                }
            }
        }

        private static RestartRecoveryOrder ReadRecoveryRow(SQLiteDataReader rd)
        {
            return new RestartRecoveryOrder
            {
                Id = Convert.ToInt64(rd["id"], CultureInfo.InvariantCulture),
                OrderDate = rd["주문일자"] == DBNull.Value ? "" : Convert.ToString(rd["주문일자"]),
                OrdNo = rd["주문번호"] == DBNull.Value ? 0 : Convert.ToInt64(rd["주문번호"], CultureInfo.InvariantCulture),
                Side = rd["매매구분"] == DBNull.Value ? "" : Convert.ToString(rd["매매구분"]),
                TradeType = rd["거래유형"] == DBNull.Value ? "" : Convert.ToString(rd["거래유형"]),
                Band = ToInt(rd["밴드번호"]),
                FromBand = rd["from_band"] == DBNull.Value ? 0 : Convert.ToInt64(rd["from_band"], CultureInfo.InvariantCulture),
                FromQty = rd["from_qty"] == DBNull.Value ? 0 : Convert.ToInt64(rd["from_qty"], CultureInfo.InvariantCulture),
                ExtraQty = rd["extra_qty"] == DBNull.Value ? 0 : Convert.ToInt64(rd["extra_qty"], CultureInfo.InvariantCulture),
                OrderQty = ToInt(rd["주문수량"]),
                CumFill = ToInt(rd["누적체결수량"]),
                Remain = ToInt(rd["잔여수량"]),
                Complete = ToInt(rd["체결완료여부"]) == 1,
                DbApplied = ToInt(rd["DB반영완료"]) == 1,
                ExecCsv = rd["체결번호목록"] == DBNull.Value ? "" : Convert.ToString(rd["체결번호목록"]),
                RecoverySource = rd["복구출처"] == DBNull.Value ? "" : Convert.ToString(rd["복구출처"])
            };
        }

        private static int ToInt(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static double ToDouble(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static string NormalizeSide(string side)
        {
            string value = (side ?? "").Trim();
            if (value == "매수" || value.Equals("BUY", StringComparison.OrdinalIgnoreCase)) return "BUY";
            if (value == "매도" || value.Equals("SELL", StringComparison.OrdinalIgnoreCase)) return "SELL";
            return value.ToUpperInvariant();
        }

        private static void LogError(string where, long ordNo, Exception ex)
        {
            Log("[RESTART_RECOVERY][ERROR] where=" + where + " ordNo=" + ordNo +
                " message=" + (ex == null ? "unknown" : ex.GetBaseException().Message));
        }

        private static void Log(string message)
        {
            try { Console.WriteLine(message); } catch { }
            try { Debug.WriteLine(message); } catch { }
        }
    }
}
