
using Exercise_1.Repositories;
using System;
using System.ComponentModel;
using System.IO;

namespace Exercise_1
{
    public partial class Login
    {
        private void ApplyEnvAndReloadDb_Safe(string reason)
        {
            lock (_envReloadLock)
            {
                if (_envReloading) return;
                _envReloading = true;
            }

            try
            {
                if (!File.Exists(DbPath))
                {
                    UpdateStatus("DB 파일이 없습니다: " + DbPath);
                    SetPanel2Color(System.Drawing.Color.Red);
                    Console.WriteLine("[ENV][DB] NOT FOUND: " + DbPath);
                    return;
                }

                _replayConnStr = ConnStr;

                try
                {
                    var repos = RepoBootstrap.Create(DbPath);
                    _bandRepo = repos.bands;
                    _cycleLogRepo = repos.cycles;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][RepoBootstrap] " + ex.Message);
                }

                try
                {
                    _dbFuncs = new DbFuncs(DbPath, 10);
                    try { _dbFuncs.EnsureDailyBalanceTable(); } catch { }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][DbFuncs] " + ex.Message);
                }

                try
                {
                    RefreshBandListView();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][RefreshBandListView] " + ex.Message);
                }

                try { _dbFuncs?.LoadDailyBalanceToListView(listView2); } catch { }

                try
                {
                    BandList = 시작밴드Read.LoadBandsAndSetStartBand();
                    SetFocusBand(Login.시작밴드변수, "ENV(" + reason + ")->" + (_0050_Real_Test환경결정.IsTest ? "TEST" : "REAL"));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][LoadBandsAndSetStartBand] " + ex.Message);
                }

                try
                {
                    if (_tickFromDb != null)
                    {
                        try { _tickFromDb.Stop(); } catch { }
                        try { _tickFromDb.Dispose(); } catch { }
                        _tickFromDb = null;
                    }
                }
                catch { }

                try
                {
                    _tickFromDb = new _0210_Tick_fromDB(ConnStr);
                    _tickFromDb.OnPrice += TickFromDb_OnPrice;
                    _tickFromDb.OnLog += TickFromDb_OnLog;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ENV][_0210_Tick_fromDB] " + ex.Message);
                }

                try
                {
                    if (_자료수집 != null)
                    {
                        try { _자료수집.Dispose(); } catch { }
                        _자료수집 = null;
                    }
                }
                catch { }

                try { _자료수집 = new 자료수집(ConnStr); } catch { }
                try { RefreshBandsAndTriggers(); } catch { }

                UpdateStatus(_0050_Real_Test환경결정.LogPrefix + " DB 적용완료: " + DbPath);
                SetPanel2Color(_0050_Real_Test환경결정.IsTest ? System.Drawing.Color.LightSkyBlue : System.Drawing.Color.LightGreen);
            }
            catch (Exception ex)
            {
                UpdateStatus("환경 적용 오류: " + ex.Message);
                SetPanel2Color(System.Drawing.Color.Red);
                Console.WriteLine("[ENV][APPLY] EX: " + ex);
            }
            finally
            {
                lock (_envReloadLock) { _envReloading = false; }
            }
        }

        public static void RefreshBandListView()
        {
            if (ListView1Ref == null) return;

            if (ListView1Ref.InvokeRequired)
            {
                ListView1Ref.BeginInvoke(new Action(() =>
                {
                    사용밴드.LoadIntoListView(ConnStr, ListView1Ref);
                }));
            }
            else
            {
                사용밴드.LoadIntoListView(ConnStr, ListView1Ref);
            }
        }
        public void RefreshBandsAndTriggers()
        {
            try
            {
                if (!File.Exists(DbPath))
                {
                    UpdateStatus("DB 파일이 없습니다: " + DbPath);
                    return;
                }

                if (_dbFuncs == null) _dbFuncs = new DbFuncs(DbPath);

                // ✅ UI용 DataTable 다시 읽기
                _bands = _dbFuncs.GetKodexBandsDataTable();

                // ✅ 핵심: 매매 로직이 실제로 쓰는 BandList 다시 읽기
                Login.BandList = new BindingList<BandRange>(
                    사용밴드.ReadAll(Login.ConnStr)
                );

                // ✅ [BUG FIX 2026-05-13] BandList 재로드 후 시작밴드변수 동기화
                // RefreshBandsAndTriggers()가 BandList를 갱신해도 시작밴드변수를 갱신하지 않으면
                // 0250/HandleUiTick/OnFilled_FromSc1 등에서 stale한 이전 밴드 번호를 참조하게 된다.
                // 체결 후 매도 완료된 밴드(예: 34)가 qty=0이 됐음에도
                // 시작밴드변수=34 그대로 남아 다음 틱에서 0300이 RANGE=34~34로 잡히는 문제의 원인.
                // BandList와 시작밴드변수를 항상 같이 갱신하여 일관성 유지.
                try
                {
                    int newStartBand = 시작밴드Read.CalcStartBand(Login.BandList);
                    if (newStartBand > 0 && newStartBand != Login.시작밴드변수)
                    {
                        Login.시작밴드변수 = newStartBand;
                        SetFocusBand(newStartBand, "REFRESH");
                    }
                }
                catch (Exception exSb)
                {
                    Console.WriteLine("[REFRESH][StartBand] sync EX: " + exSb.Message);
                }

                Console.WriteLine("[REFRESH] DataTable + BandList reloaded from DB");
            }
            catch (Exception ex)
            {
                UpdateStatus("밴드 계산 오류: " + ex.Message);
            }
        }

    }
}
// 2026-04-14 46281