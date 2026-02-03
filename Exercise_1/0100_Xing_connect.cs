// 0100_Xing_connect.cs  (복붙용 / C# 7.3)
// ------------------------------------------------------------
// 책임(확정):
// - XING 접속/로그인: XingBrokerClient 단일 사용
// - REAL/TEST 판단: _0050_Real_Test환경결정(static Apply)
// - 매매_Xing 생성 후 Login에게 제공
// - ❌ SC1 시작하지 않음 (0650이 담당)
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class _0100_Xing_connect : IDisposable
    {
        // ===== 서버 정보 (필요 시 수정) =====
        private const string REAL_SERVER_ADDR = "hts.ebestsec.co.kr";
        private const string TEST_SERVER_ADDR = "demo.ebestsec.co.kr";
        private const int DEFAULT_PORT = 20001;

        private readonly string _jmid;     // Login에서 넘어오는 ID (fallback)
        private readonly string _jmauth;   // 인증서 비번 용도
        private readonly Action<string> _updateStatus;
        private readonly Action<Color> _setPanel2Color;

        private XingBrokerClient _broker;
        private 매매_Xing _mmXing;

        private bool _disposed;
        private bool _loggedIn;

        public bool IsLoggedIn => _loggedIn && _broker != null;
        public 매매_Xing TradeXing => _mmXing;

        public _0100_Xing_connect(
            string jmid,
            string jmauth,
            Action<string> updateStatus,
            Action<Color> setPanel2Color)
        {
            _jmid = (jmid ?? "").Trim();
            _jmauth = (jmauth ?? "").Trim();
            _updateStatus = updateStatus ?? (_ => { });
            _setPanel2Color = setPanel2Color ?? (_ => { });
        }

        /// <summary>
        /// 로그인 + 매매_Xing 생성 후 반환
        /// </summary>
        public async Task<매매_Xing> ConnectAsync(string shcode)
        {
            EnsureNotDisposed();

            shcode = (shcode ?? "").Trim();
            if (string.IsNullOrWhiteSpace(shcode))
                throw new ArgumentException("shcode is empty.", nameof(shcode));

            // 0050.Apply(runMode) 선행 필수
            if (string.IsNullOrWhiteSpace(_0050_Real_Test환경결정.RunMode))
                throw new InvalidOperationException("0050.Apply(runMode)가 먼저 호출되어야 합니다.");

            var sw = Stopwatch.StartNew();

            try
            {
                Console.WriteLine("==================================================");
                Console.WriteLine($"[0100] ConnectAsync START shcode='{shcode}' now={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                Console.WriteLine($"[0100] mode={_0050_Real_Test환경결정.RunMode} db='{_0050_Real_Test환경결정.DbPath}'");
                Console.WriteLine("==================================================");

                _updateStatus("XING 접속/로그인 시도중...");
                _setPanel2Color(Color.Khaki);

                if (_broker == null)
                {
                    _broker = new XingBrokerClient();
                    Console.WriteLine("[0100] XingBrokerClient created");
                }

                string serverAddr = _0050_Real_Test환경결정.IsReal ? REAL_SERVER_ADDR : TEST_SERVER_ADDR;
                int port = DEFAULT_PORT;
                int serverType = _0050_Real_Test환경결정.IsReal ? 0 : 1;

                // 자격증명은 0050 기준이 정답
                string userId = !string.IsNullOrWhiteSpace(_0050_Real_Test환경결정.UserId)
                    ? _0050_Real_Test환경결정.UserId.Trim()
                    : _jmid;

                string userPw = (_0050_Real_Test환경결정.Password ?? "").Trim();
                string certPw = _jmauth ?? "";

                if (string.IsNullOrWhiteSpace(userId))
                    throw new InvalidOperationException("userId가 비었습니다.");
                if (string.IsNullOrWhiteSpace(userPw))
                    throw new InvalidOperationException("userPw가 비었습니다.");

                Console.WriteLine($"[0100] server='{serverAddr}:{port}' type={serverType} id='{userId}'");

                _broker.ConnectAndLogin(
    serverAddr: serverAddr,
    port: port,
    userId: userId,
    userPw: userPw,
    certPw: certPw ?? "",
    serverType: serverType
);

                _loggedIn = true;

                _updateStatus("XING 로그인 성공 " + _0050_Real_Test환경결정.LogPrefix);
                _setPanel2Color(Color.LightGreen);

                Console.WriteLine($"[0100] LOGIN OK elapsedMs={sw.ElapsedMilliseconds}");

                if (_mmXing == null)
                {
                    _mmXing = new 매매_Xing(
                        getAcntNo: () => (_0050_Real_Test환경결정.Account ?? "").Trim(),
                        getPwd4: () =>
                        {
                            try { return (Login.JMpass ?? "").Trim(); }
                            catch { return ""; }
                        },
                        getShcode: () => shcode
                    );

                    Console.WriteLine($"[0100] 매매_Xing created act='{_0050_Real_Test환경결정.Account}' shcode='{shcode}'");
                }

                Console.WriteLine("==================================================");
                Console.WriteLine($"[0100] ConnectAsync END (SUCCESS) now={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                Console.WriteLine("==================================================");

                return _mmXing;
            }
            catch (Exception ex)
            {
                _loggedIn = false;

                _updateStatus("XING Connect 오류: " + ex.Message);
                _setPanel2Color(Color.Red);

                Console.WriteLine("[0100] ConnectAsync EXCEPTION:");
                Console.WriteLine(ex);
                Console.WriteLine($"[0100] elapsedMs={sw.ElapsedMilliseconds}");
                Console.WriteLine("==================================================");

                return null;
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

// 2026-02-02 73914
