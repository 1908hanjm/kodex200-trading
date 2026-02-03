using System;
using XA_DATASETLib;

namespace Exercise_1
{
    /// <summary>
    /// CSPAT00600(현물 매수/매도), CSPAT00700(정정), CSPAT00800(취소) 전담 서비스
    /// - PlaceOrder 로 주문 실행
    /// - 결과/메시지는 이벤트로 Form에 전달
    /// </summary>
    public class OrderService : IDisposable
    {
        private readonly string _accountNo;  // AcntNo
        private readonly string _inputPwd;   // InptPwd (주문비밀번호)
        private readonly string _shcode;     // IsuNo (종목코드)
        private readonly string _resRoot;    // RES 파일 루트 경로 (예: C:\LS_SEC\xingAPI\Res)

        private readonly XAQueryClass _cspat00600 = new XAQueryClass();
        private readonly XAQueryClass _cspat00700 = new XAQueryClass();
        private readonly XAQueryClass _cspat00800 = new XAQueryClass();

        public OrderService(string accountNo, string inputPwd, string shcode, string resRoot)
        {
            _accountNo = accountNo;
            _inputPwd = inputPwd;
            _shcode = shcode;
            _resRoot = resRoot.TrimEnd('\\');

            // RES 파일 세팅
            _cspat00600.ResFileName = System.IO.Path.Combine(_resRoot, "CSPAT00600.res");
            _cspat00700.ResFileName = System.IO.Path.Combine(_resRoot, "CSPAT00700.res");
            _cspat00800.ResFileName = System.IO.Path.Combine(_resRoot, "CSPAT00800.res");

            // 이벤트 바인딩
            _cspat00600.ReceiveData += On00600ReceiveData;
            _cspat00600.ReceiveMessage += On00600ReceiveMessage;

            _cspat00700.ReceiveData += On00700ReceiveData;
            _cspat00700.ReceiveMessage += On00700ReceiveMessage;

            _cspat00800.ReceiveData += On00800ReceiveData;
            _cspat00800.ReceiveMessage += On00800ReceiveMessage;
        }

        #region 외부로 내보낼 이벤트/DTO
        public class OrderResult
        {
            public DateTime Timestamp { get; set; }
            public string TrCode { get; set; }        // CSPAT00600, 00700, 00800
            public string Side { get; set; }          // BUY/SELL/AMEND/CANCEL
            public int Qty { get; set; }
            public int Price { get; set; }
            public string OrdNo { get; set; }         // 주문번호
            public string MessageCode { get; set; }   // 시스템 메시지 코드
            public string MessageText { get; set; }   // 시스템 메시지 텍스트
            public bool IsSystemError { get; set; }   // 시스템 에러 여부
        }

        public event Action<OrderResult> OrderAccepted;    // OutBlock 수신(주문번호 등)
        public event Action<OrderResult> OrderMessage;     // ReceiveMessage(시스템 메시지)
        #endregion

        #region 주문 실행 API
        /// <summary>
        /// 현물 매수/매도 (CSPAT00600)
        /// </summary>
        public int PlaceOrder(bool isBuy, int qty, int price)
        {
            _cspat00600.SetFieldData("CSPAT00600InBlock1", "AcntNo", 0, _accountNo);
            _cspat00600.SetFieldData("CSPAT00600InBlock1", "InptPwd", 0, _inputPwd);
            _cspat00600.SetFieldData("CSPAT00600InBlock1", "IsuNo", 0, _shcode);
            _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdQty", 0, qty.ToString());
            _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdPrc", 0, price.ToString());
            _cspat00600.SetFieldData("CSPAT00600InBlock1", "BnsTpCode", 0, isBuy ? "2" : "1"); // 2=매수, 1=매도
            _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdprcPtnCode", 0, "00");              // 지정가
            _cspat00600.SetFieldData("CSPAT00600InBlock1", "MgntrnCode", 0, "000");             // 현금
            _cspat00600.SetFieldData("CSPAT00600InBlock1", "OrdCndiTpCode", 0, "0");               // 보통

            return _cspat00600.Request(false); // 음수면 실패
        }

        // 필요 시 정정/취소도 공개 API로 제공 (현재는 폼에서 버튼 안 씀)
        public int AmendOrder(string orgOrdNo, int newPrice)
        {
            _cspat00700.SetFieldData("CSPAT00700InBlock1", "AcntNo", 0, _accountNo);
            _cspat00700.SetFieldData("CSPAT00700InBlock1", "InptPwd", 0, _inputPwd);
            _cspat00700.SetFieldData("CSPAT00700InBlock1", "OrgOrdNo", 0, orgOrdNo);
            _cspat00700.SetFieldData("CSPAT00700InBlock1", "IsuNo", 0, _shcode);
            _cspat00700.SetFieldData("CSPAT00700InBlock1", "OrdPrc", 0, newPrice.ToString());
            _cspat00700.SetFieldData("CSPAT00700InBlock1", "OrdprcPtnCode", 0, "00");
            _cspat00700.SetFieldData("CSPAT00700InBlock1", "OrdCndiTpCode", 0, "0");
            return _cspat00700.Request(false);
        }

        public int CancelOrder(string orgOrdNo, int qtyToCancel)
        {
            _cspat00800.SetFieldData("CSPAT00800InBlock1", "AcntNo", 0, _accountNo);
            _cspat00800.SetFieldData("CSPAT00800InBlock1", "InptPwd", 0, _inputPwd);
            _cspat00800.SetFieldData("CSPAT00800InBlock1", "OrgOrdNo", 0, orgOrdNo);
            _cspat00800.SetFieldData("CSPAT00800InBlock1", "IsuNo", 0, _shcode);
            _cspat00800.SetFieldData("CSPAT00800InBlock1", "OrdQty", 0, qtyToCancel.ToString());
            return _cspat00800.Request(false);
        }
        #endregion

        #region 내부: 이벤트 핸들러
        private void On00600ReceiveData(string tr)
        {
            try
            {
                var ordNo = _cspat00600.GetFieldData("CSPAT00600OutBlock2", "OrdNo", 0);
                OrderAccepted?.Invoke(new OrderResult
                {
                    Timestamp = DateTime.Now,
                    TrCode = "CSPAT00600",
                    Side = "BUY/SELL", // 실제 매수/매도 구분은 호출부에서 알 수 있으나 여기선 포괄 표기
                    OrdNo = ordNo,
                    MessageCode = null,
                    MessageText = string.IsNullOrWhiteSpace(ordNo) ? "주문 접수 (주문번호 미수신)" : "주문 접수",
                    IsSystemError = false
                });
            }
            catch (Exception ex)
            {
                OrderMessage?.Invoke(new OrderResult
                {
                    Timestamp = DateTime.Now,
                    TrCode = "CSPAT00600",
                    Side = "BUY/SELL",
                    OrdNo = null,
                    MessageCode = "LOCAL",
                    MessageText = "주문 응답 처리 오류: " + ex.Message,
                    IsSystemError = true
                });
            }
        }

        private void On00600ReceiveMessage(bool isSysErr, string code, string msg)
        {
            OrderMessage?.Invoke(new OrderResult
            {
                Timestamp = DateTime.Now,
                TrCode = "CSPAT00600",
                Side = "BUY/SELL",
                OrdNo = null,
                MessageCode = code,
                MessageText = msg,
                IsSystemError = isSysErr
            });
        }

        private void On00700ReceiveData(string tr)
        {
            var ordNo = _cspat00700.GetFieldData("CSPAT00700OutBlock2", "OrdNo", 0);
            OrderAccepted?.Invoke(new OrderResult
            {
                Timestamp = DateTime.Now,
                TrCode = "CSPAT00700",
                Side = "AMEND",
                OrdNo = ordNo,
                MessageText = string.IsNullOrWhiteSpace(ordNo) ? "정정 접수 (주문번호 미수신)" : "정정 접수",
                IsSystemError = false
            });
        }

        private void On00700ReceiveMessage(bool isSysErr, string code, string msg)
        {
            OrderMessage?.Invoke(new OrderResult
            {
                Timestamp = DateTime.Now,
                TrCode = "CSPAT00700",
                Side = "AMEND",
                MessageCode = code,
                MessageText = msg,
                IsSystemError = isSysErr
            });
        }

        private void On00800ReceiveData(string tr)
        {
            var ordNo = _cspat00800.GetFieldData("CSPAT00800OutBlock2", "OrdNo", 0);
            OrderAccepted?.Invoke(new OrderResult
            {
                Timestamp = DateTime.Now,
                TrCode = "CSPAT00800",
                Side = "CANCEL",
                OrdNo = ordNo,
                MessageText = string.IsNullOrWhiteSpace(ordNo) ? "취소 접수 (주문번호 미수신)" : "취소 접수",
                IsSystemError = false
            });
        }

        private void On00800ReceiveMessage(bool isSysErr, string code, string msg)
        {
            OrderMessage?.Invoke(new OrderResult
            {
                Timestamp = DateTime.Now,
                TrCode = "CSPAT00800",
                Side = "CANCEL",
                MessageCode = code,
                MessageText = msg,
                IsSystemError = isSysErr
            });
        }
        #endregion

        public void Dispose()
        {
            // XAQueryClass는 COM 객체라서 명시 Dispose는 없지만, 이벤트 핸들러 제거는 안전상 권장
            _cspat00600.ReceiveData -= On00600ReceiveData;
            _cspat00600.ReceiveMessage -= On00600ReceiveMessage;

            _cspat00700.ReceiveData -= On00700ReceiveData;
            _cspat00700.ReceiveMessage -= On00700ReceiveMessage;

            _cspat00800.ReceiveData -= On00800ReceiveData;
            _cspat00800.ReceiveMessage -= On00800ReceiveMessage;
        }
    }
}
