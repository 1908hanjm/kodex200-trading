// 0150_ConnectServer실행.cs  (복붙용 / C# 7.3)  [COM Late-binding 버전]
// ------------------------------------------------------------
// 책임:
// - XING 서버 접속(ConnectServer)
// - 로그인 실행(Login)
// - ※ XA_SESSIONLib 참조 없이도 컴파일되게 late-binding 사용
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;

namespace Exercise_1
{
    public sealed class _0150_ConnectServer실행 : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _userId;
        private readonly string _password;

        private readonly Action<string> _updateStatus;
        private readonly Action<Color> _setPanel2Color;

        private dynamic _session;   // late-bound COM object
        private bool _disposed;

        public bool IsLoggedIn { get; private set; }

        public _0150_ConnectServer실행(
            string host,
            int port,
            string userId,
            string password,
            Action<string> updateStatus,
            Action<Color> setPanel2Color)
        {
            _host = (host ?? "").Trim();
            _port = port;
            _userId = (userId ?? "").Trim();
            _password = (password ?? "").Trim();

            _updateStatus = updateStatus ?? (_ => { });
            _setPanel2Color = setPanel2Color ?? (_ => { });

            if (string.IsNullOrWhiteSpace(_host)) throw new ArgumentException("host is empty");
            if (_port <= 0) throw new ArgumentException("port is invalid");
            if (string.IsNullOrWhiteSpace(_userId)) throw new ArgumentException("userId is empty");
            if (string.IsNullOrWhiteSpace(_password)) throw new ArgumentException("password is empty");
        }

        public Task<bool> ConnectAndLoginAsync()
        {
            EnsureNotDisposed();

            return Task.Run(() =>
            {
                try
                {
                    EnsureSessionCreated();

                    _updateStatus($"XING 접속: {_host}:{_port}");
                    Debug.WriteLine($"[0150][CONNECT] {_host}:{_port} id={_userId}");

                    bool connected = _session.ConnectServer(_host, _port);
                    if (!connected)
                    {
                        IsLoggedIn = false;
                        _updateStatus("ConnectServer 실패");
                        _setPanel2Color(Color.Red);
                        return false;
                    }

                    // 공인인증서 비밀번호 불필요 → 빈 문자열
                    int ret = _session.Login(_userId, _password, "", 0, false);

                    if (ret < 0)
                    {
                        IsLoggedIn = false;
                        _updateStatus($"Login 호출 실패 (ret={ret})");
                        _setPanel2Color(Color.Red);
                        return false;
                    }

                    // 이벤트를 late-binding으로 안정적으로 받기 어렵기 때문에,
                    // 여기서는 호출 성공(ret>=0) + 서버접속 성공을 로그인 성공으로 간주.
                    IsLoggedIn = true;
                    _updateStatus("Login 호출 성공");
                    _setPanel2Color(Color.LightGreen);
                    return true;
                }
                catch (Exception ex)
                {
                    IsLoggedIn = false;
                    _updateStatus("XING 접속 오류: " + ex.Message);
                    _setPanel2Color(Color.Red);
                    Debug.WriteLine("[0150][EX] " + ex);
                    return false;
                }
            });
        }

        private void EnsureSessionCreated()
        {
            if (_session != null) return;

            // 환경별 ProgID 후보
            // (설치 버전에 따라 다를 수 있어 2개 시도)
            string[] progIds = new[]
            {
                "XA_Session.XASession",
                "XA_Session.XASession.1"
            };

            Exception last = null;

            foreach (var progId in progIds)
            {
                try
                {
                    var t = Type.GetTypeFromProgID(progId);
                    if (t == null) continue;

                    _session = Activator.CreateInstance(t);
                    if (_session != null)
                    {
                        Debug.WriteLine("[0150] COM session created progId=" + progId);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw new InvalidOperationException(
                "XING Session COM 객체 생성 실패. (XING API 설치/참조 확인 필요) " +
                (last != null ? last.Message : "")
            );
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _session = null; } catch { }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(_0150_ConnectServer실행));
        }
    }
}

// 2026-01-30 93014
