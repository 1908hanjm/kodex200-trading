// 0100_Xing_connect.cs  (복붙용 / C# 7.3)  [0050(LoginPw/CertPw) 고정 연동 최종본]
// ------------------------------------------------------------
// 책임(확정):
// - XING 접속/로그인: XingBrokerClient로 단일화
// - REAL/TEST는 0050(static Apply)로만 결정
// - ✅ 로그인(userPw) = 0050.LoginPw 만 사용 (절대 Login.JMpass 사용 금지)
// - ✅ 인증서(certPw) = 0050.CertPw 만 사용
// - 매매_Xing 생성(Func 주입) 후 Login에게 제공
// - ✅ SC1 시작은 하지 않음 (0650이 담당)
// - ✅ (중요) "2번 호출처럼 보이는" 중복 [XING] START 로그는 0100에서 절대 출력하지 않음
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class _0100_Xing_connect : IDisposable
    {
        // ===== 서버 정보(환경에 맞게 필요 시 수정) =====
        private const string REAL_SERVER_ADDR = "hts.ls_sec.co.kr";
        private const string TEST_SERVER_ADDR = "demo.ebestsec.co.kr";   // [데모에서는 꼭 이주소 사용하세요]
        private const int DEFAULT_PORT = 20001;

        private readonly Action<string> _updateStatus;
        private readonly Action<Color> _setPanel2Color;

        private XingBrokerClient _broker;
        private 매매_Xing _mmXing;

        private bool _disposed;
        private bool _loggedIn;

        // ✅ ConnectAsync 중복 진입 방지(실제 “2번 호출”도 방지)
        private int _connectGate = 0;

        public bool IsLoggedIn => _loggedIn && _broker != null;
        public bool IsDisposed => _disposed;
        public 매매_Xing TradeXing => _mmXing;

        public _0100_Xing_connect(
            string jmid,   // (호환용) 이제 사용 안 함: 0050.UserId만 씀
            string jmauth, // (호환용) 이제 사용 안 함: 0050.CertPw만 씀
            Action<string> updateStatus,
            Action<Color> setPanel2Color)
        {
            _updateStatus = updateStatus ?? (_ => { });
            _setPanel2Color = setPanel2Color ?? (_ => { });
        }

        /// <summary>
        /// ✅ 로그인 + 매매_Xing 생성 후 반환
        /// - Login.cs 에서: _mmXing = await _xingConn.ConnectAsync(shcode);
        /// </summary>
        public async Task<매매_Xing> ConnectAsync(string shcode)
        {
            EnsureNotDisposed();

            // ✅ “진짜 2번 호출”도 막아버림
            if (Interlocked.Exchange(ref _connectGate, 1) == 1)
            {
                Console.WriteLine("[0100] ConnectAsync DUPLICATE CALL BLOCKED");
                return _mmXing; // 이미 생성돼 있으면 재사용
            }

            var sw = Stopwatch.StartNew();

            try
            {
                shcode = (shcode ?? "").Trim();
                if (string.IsNullOrWhiteSpace(shcode))
                    throw new ArgumentException("shcode is empty.", nameof(shcode));

                // 0050.Apply(runMode) 선행 필수
                if (string.IsNullOrWhiteSpace(_0050_Real_Test환경결정.RunMode))
                    throw new InvalidOperationException("0050.Apply(runMode)가 호출되기 전에 0100.ConnectAsync()가 호출되었습니다.");

                Console.WriteLine("==================================================");
                Console.WriteLine($"[0100] ConnectAsync START shcode='{shcode}' now={KoreaTime.Timestamp()} KST");
                Console.WriteLine($"[0100] mode={_0050_Real_Test환경결정.RunMode} db='{_0050_Real_Test환경결정.DbPath}'");
                Console.WriteLine("==================================================");

                _updateStatus("XING 접속/로그인 시도중...");
                _setPanel2Color(Color.Khaki);

                if (_broker == null)
                {
                    _broker = new XingBrokerClient();
                    Console.WriteLine("[0100] XingBrokerClient created");
                }
                else
                {
                    Console.WriteLine("[0100] XingBrokerClient reused");
                }

                // 환경별 서버/타입 결정
                string serverAddr = _0050_Real_Test환경결정.IsReal ? REAL_SERVER_ADDR : TEST_SERVER_ADDR;
                int port = DEFAULT_PORT;
                int serverType = _0050_Real_Test환경결정.IsReal ? 0 : 1; // 0=실, 1=모의

                // ✅ 여기서부터 “0050만” 사용 (절대 Login.JMpass 섞지 않음)
                string userId = (_0050_Real_Test환경결정.UserId ?? "").Trim();
                string userPw = (_0050_Real_Test환경결정.LoginPw ?? "").Trim(); // ✅ 로그인 비밀번호
                string certPw = (_0050_Real_Test환경결정.CertPw ?? "").Trim();  // ✅ 인증서 비밀번호

                if (string.IsNullOrWhiteSpace(userId))
                    throw new InvalidOperationException("[0100] 0050.UserId가 비었습니다.");
                if (string.IsNullOrWhiteSpace(userPw))
                    throw new InvalidOperationException("[0100] 0050.LoginPw(로그인비번)가 비었습니다.");
                if (string.IsNullOrWhiteSpace(certPw))
                    throw new InvalidOperationException("[0100] 0050.CertPw(인증서비번)가 비었습니다.");

                // ✅ 진단 로그 (중요: JMpass 길이도 같이 찍어서 절대 섞이지 않게 감시)
                int jmLen;
                try { jmLen = (Login.JMpass ?? "").Trim().Length; } catch { jmLen = -1; }

                Console.WriteLine($"[0100] server='{serverAddr}:{port}' type={serverType} id='{userId}'");
                Console.WriteLine($"[0100] pwLen(LoginPw)={userPw.Length} certLen(CertPw)={certPw.Length} jmLen(OrderPw)={jmLen}");
                Console.WriteLine("[0100] calling XingBrokerClient.ConnectAndLogin ... (XING 상세 로그는 Broker가 출력)");

                // ✅ 실제 로그인 호출 (XingBrokerClient가 [XING] START/RESULT 로그를 출력함)
                await Task.Run(() =>
                {
                    _broker.ConnectAndLogin(
                        serverAddr: serverAddr,
                        port: port,
                        userId: userId,
                        userPw: userPw,
                        certPw: certPw,
                        serverType: serverType
                    );
                }).ConfigureAwait(true);

                _loggedIn = true;

                Console.WriteLine($"[0100] LOGIN OK elapsedMs={sw.ElapsedMilliseconds}");
                _updateStatus("XING 로그인 성공 " + _0050_Real_Test환경결정.LogPrefix);
                _setPanel2Color(Color.LightGreen);

                // ✅ 매매_Xing 생성
                // - 계좌번호: 0050.Account
                // - 주문비번(4자리): Login.JMpass  (여기서만 사용!)
                if (_mmXing == null)
                {
                    _mmXing = new 매매_Xing(
                        getAcntNo: () => (_0050_Real_Test환경결정.Account ?? "").Trim(),
                        getPwd4: () =>
                        {
                            try { return (Login.JMpass ?? "").Trim(); } catch { return ""; }
                        },
                        getShcode: () => shcode
                    );

                    Console.WriteLine($"[0100] 매매_Xing created act='{_0050_Real_Test환경결정.Account}' shcode='{shcode}'");
                }
                else
                {
                    Console.WriteLine("[0100] 매매_Xing reused");
                }

                Console.WriteLine("==================================================");
                Console.WriteLine($"[0100] ConnectAsync END (SUCCESS) now={KoreaTime.Timestamp()} KST");
                Console.WriteLine("==================================================");

                return _mmXing;
            }
            catch (Exception ex)
            {
                _loggedIn = false;

                _updateStatus("XING Connect 오류: " + ex.Message);
                _setPanel2Color(Color.Red);

                Console.WriteLine("[0100] ConnectAsync EXCEPTION:");
                Console.WriteLine(ex.ToString());
                Console.WriteLine($"[0100] elapsedMs={sw.ElapsedMilliseconds}");
                Console.WriteLine("==================================================");

                return null;
            }
            finally
            {
                // ✅ 게이트 해제
                Interlocked.Exchange(ref _connectGate, 0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _mmXing?.Dispose(); } catch { }
            _mmXing = null;

            try { _broker?.Dispose(); } catch { }
            _broker = null;

            _loggedIn = false;
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(_0100_Xing_connect));
        }
    }
}

// 2026-02-03 27184
