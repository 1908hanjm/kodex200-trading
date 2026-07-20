using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using XA_SESSIONLib;

namespace Exercise_1
{
    public sealed class XingBrokerClient : IBrokerClient, IDisposable
    {
        private XASessionClass _session;

        private volatile bool _disposed;

        // 로그인 이벤트 상태
        private volatile bool _loginArrived;
        private volatile string _loginCode;
        private volatile string _loginMsg;

        private readonly AutoResetEvent _loginEvent = new AutoResetEvent(false);

        public XingBrokerClient()
        {
            _session = new XASessionClass();

            // COM 이벤트 구독
            var ev = (_IXASessionEvents_Event)_session;
            ev.Login += OnLogin;
            ev.Logout += OnLogout;
            ev.Disconnect += OnDisconnect;
        }

        /// <summary>
        /// serverType: 0=실서버, 1=모의서버 (환경에 따라 다를 수 있음)
        /// </summary>
        public void ConnectAndLogin(
            string serverAddr,
            int port,
            string userId,
            string userPw,
            string certPw,
            int serverType)
        {
            EnsureNotDisposed();

            if (string.IsNullOrWhiteSpace(serverAddr)) throw new ArgumentException("serverAddr is empty.");
            if (port <= 0) throw new ArgumentException("port is invalid.");
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("userId is empty.");
            if (string.IsNullOrWhiteSpace(userPw)) throw new ArgumentException("userPw is empty.");
            if (certPw == null) certPw = "";

            ResetLoginState();

            Console.WriteLine("==================================================");
            Console.WriteLine($"[XING] ConnectAndLogin START addr='{serverAddr}' port={port} type={serverType} id='{userId}'");
            Console.WriteLine($"[XING] pwLen={userPw.Trim().Length} certLen={certPw.Trim().Length} now={KoreaTime.Timestamp()} KST");
            Console.WriteLine("==================================================");

            // 1) 서버 연결
            bool connOk = _session.ConnectServer(serverAddr, port);
            if (!connOk)
            {
                int err = _session.GetLastError();
                string msg = _session.GetErrorMessage(err);
                throw new Exception($"ConnectServer 실패: err={err} msg='{msg}' addr='{serverAddr}' port={port}");
            }

            // 2) 로그인 요청
            bool requestOk = _session.Login(userId, userPw, certPw, serverType, false);
            if (!requestOk)
            {
                int err = _session.GetLastError();
                string msg = _session.GetErrorMessage(err);
                throw new Exception($"Login 호출 실패(요청 자체 실패): err={err} msg='{msg}'");
            }

            // 3) 로그인 이벤트 대기
            var sw = Stopwatch.StartNew();
            while (!_loginArrived)
            {
                // ★ UI 스레드면 DoEvents로 메시지펌프 유지 (로그인 이벤트 수신 안정화)
                try { Application.DoEvents(); } catch { }

                if (_loginEvent.WaitOne(1))
                    break;

                if (sw.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException("XING 로그인 타임아웃(로그인 이벤트 미도착)");
            }

            // 4) 이벤트 도착 → 성공/실패 판정
            string code = _loginCode ?? "";
            string msg2 = _loginMsg ?? "";

            Console.WriteLine($"[XING] LoginEvent ARRIVED code='{code}' msg='{msg2}' elapsedMs={sw.ElapsedMilliseconds}");

            if (!string.Equals(code, "0000", StringComparison.OrdinalIgnoreCase))
            {
                // 실패 코드를 그대로 올려서 “왜 실패인지” 바로 보이게 함
                throw new Exception($"XING 로그인 실패: code='{code}' msg='{msg2}'");
            }

            Console.WriteLine($"[XING] LOGIN OK elapsedMs={sw.ElapsedMilliseconds}");
        }

        private void OnLogin(string code, string msg)
        {
            _loginCode = code;
            _loginMsg = msg;
            _loginArrived = true;

            try { _loginEvent.Set(); } catch { }
        }

        private void OnLogout()
        {
            // 필요 시 로그 추가 가능
        }

        private void OnDisconnect()
        {
            // 필요 시 로그 추가 가능
        }

        private void ResetLoginState()
        {
            _loginArrived = false;
            _loginCode = null;
            _loginMsg = null;

            try { _loginEvent.Reset(); } catch { }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(XingBrokerClient));
            if (_session == null) throw new ObjectDisposedException(nameof(XingBrokerClient));
        }

        public DailyBalanceSnapshot GetDailyBalanceToday(string accountNo)
        {
            // 지금은 사용하지 않는다고 하셨으니,
            // 기존 구조 유지 목적의 최소 구현만 남깁니다.
            // (추후 0900/0424로 대체 사용 가능)
            return new DailyBalanceSnapshot
            {
                Ymd = DateTime.Now.ToString("yyyyMMdd"),
                보유량 = 0,
                현금 = 0,
                D2 = 0,
                당일손익 = 0,
                총자산 = 0,
                UpdatedTsKst = DateTimeOffset.Now
            };
        }
        //ddd
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_session != null)
                {
                    try { _session.Logout(); } catch { }
                    try { _session.DisconnectServer(); } catch { }
                }
            }
            catch { }

            try { _loginEvent.Dispose(); } catch { }

            _session = null;
        }
    }
}

// 2026-02-02 58371
