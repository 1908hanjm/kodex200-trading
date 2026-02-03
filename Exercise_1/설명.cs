//로그인시 비밀번호는 002cds
//거래시 비밀번호는 1908
//1) 프로그램 시작 & 폼 초기화

//Login 폼이 생성되면서 InitializeComponent() 실행.

//폼 생성자에서 각종 이벤트 연결:

//XING 세션 로그인 이벤트: myXASessionClass._IXASessionEvents_Event_Login

//일반 조회/메시지 이벤트: myXAQueryClass.ReceiveData, myXAQueryClass.ReceiveMessage

//실시간 시세: realS3.ReceiveRealData

//버튼: button2.Click(주문), button3.Click(주문취소)

//listView3.SelectedIndexChanged(행 선택 시 컨트롤 채우기)

//주문 서비스 인스턴스 생성(_orderSvc = new OrderService(...)) 후 이벤트 연결:

//OrderAccepted, OrderMessage → OnOrderEvent로 수신

//2) 폼 로드(MainForm_Load)

//솔루션 루트 경로를 찾아 mydb.db 경로 결정.

//DbFuncs로 리스트뷰 데이터 로드:

//listView1: kodex200_new 불러오기(+ 강조/합계 텍스트박스 갱신 옵션)

//listView2: daily_balance 불러오기

//(t0167 제거 후 적용했다면) 한국시간 표시 타이머 시작

//InitKoreanClockMinuteAligned() → 분 경계에 맞춰 textBox8에 yyyyMMdd - hh:mm tt 형식으로 갱신

//3) 폼 표시 후(OnShown) → 서버 연결 & 로그인

//OnShown에서 XING 서버에 접속하고 Login(...) 호출.

//(선택) T1101 한 번 요청해 RES/연결 점검.

//실시간 구독 시작: SubscribeS3(currentShcode)

//RES 파일 로드 → AdviseRealData()

//이후 체결/가격 수신 시 RealS3_ReceiveRealData에서 textBox1 등 UI 갱신

//4) 로그인 성공 콜백(myXASessionClass__..._Login)

//szCode == "0000"이면 로그인 성공 처리:

//실시간 시세는 이미 구독 중.

//당일 주문/체결 조회 비동기 실행: _ = LoadTodayOrdersToListView3();

//        t0425.res를 사용해 서버에서 당일 주문/체결 조회

//결과를 OrderInquiryService.FetchTodayAsync(...)로 받아서 집계

//주문번호별로 마지막 상태만 1행으로 만들고, 시간/구분/수량/가격/주문번호/상태 6개 컬럼으로 listView3에 표시

//5) 주문 버튼(button2_Click)

//사용자가 수량(textBox4), 가격(textbox11), 매수/매도 라디오 버튼을 입력/선택 후 button2 클릭.

//입력값 검증(수량/가격/매수·매도 선택).

//_orderSvc.PlaceOrder(isBuy, qty, price) 호출:

//반환값< 0 이면 요청 실패 → 에러행 추가

//그 외는 “요청 전송됨” 상태로 임시 행 추가

//실제 주문 접수/메시지는 비동기로 OnOrderEvent 에 들어옴.

//6) 주문 이벤트 처리(OnOrderEvent)

//OrderService에서 올라온 OrderResult 수신.

//UI 스레드로 마샬링(BeginInvoke) 후:

//AppendOrderRow(r) 로 listView2(주문 이벤트 로그 같은 용도)에 한 줄 추가

//중복 방지: OrdNo 기준으로 이미 본 주문번호는 스킵

//주문 완료 시 갱신 로직

//OrdNo가 있고 IsSystemError == false 면 주문이 정상 접수된 것으로 간주

//await LoadTodayOrdersToListView3() 또는 _ = LoadTodayOrdersToListView3() 호출 → listView3 최신화
//(이때 t0425 재조회하여 최종 상태/수량 등을 반영)

//7) 주문 취소 버튼(button3_Click)

//textBox7에 주문번호가 있어야 함(또는 listView3에서 행을 선택하면 자동 채움).

//취소 수량은 textBox4나 선택 행의 수량에서 가져옴.

//사용자 확인 대화상자(Yes/No).

//_orderSvc.CancelOrder(ordNo, qtyToCancel) 호출:

//요청 전송 성공 안내

//약간 대기 후 LoadTodayOrdersToListView3() 재호출 → 취소 반영된 당일 주문/체결 목록으로 업데이트

//8) listView3 행 선택(listView3_SelectedIndexChanged)

//선택된 행의 구분(매수/매도) 에 맞춰 라디오 버튼 세팅.

//수량/주문번호를 textBox4, textBox7에 채워서 주문/취소를 바로 이어서 할 수 있게 함.

//9) 실시간 체결 수신(RealS3_ReceiveRealData)

//S3_ 실시간에서 가격/시간 필드 후보 중 값이 들어오는 첫 필드를 찾아 표시.

//BeginInvoke로 UI 스레드에서 panel1 색상, textBox1 텍스트 등을 갱신(수신 시각화).

//10) 보조 폼 열기(button1_Click)

//DB_Control 폼을 열어 DB 관련 작업(테이블 생성/삽입/조회 등)을 별도로 수행.

//11) 종료 처리(OnFormClosed)

//주문 서비스 등 리소스 해제: _orderSvc?.Dispose();

//        (한국시간 타이머 사용 시) _krTimer.Stop()/Dispose() 로 타이머 정리 권장.
//        → 폼이 닫힌 뒤에도 불필요한 Tick이 발생하지 않게 함.


//        정리된 데이터 흐름(요약)

//        UI 준비 → DB에서 기본 목록 로딩 → 한국시간 타이머 시작

//        서버 로그인 → 실시간 구독 → t0425 당일 주문/체결 조회(listView3 표시)

//        주문 버튼 → 서버 전송 → 주문 이벤트 수신(OrdNo) → t0425 재조회로 listView3 최신화


//        행 선택으로 취소 준비 → 취소 버튼 → 서버 전송 → t0425 재조회로 최신화

//        실시간 체결은 상시 들어와 UI에 시세/신호 표시


//        이 흐름대로라면, 사용자는 주문/취소 직후에 자동으로 최신 “당일 주문/체결” 목록을 보게 되고, 실시간 시세도 따로 갱신됩니다.

//h 030_밴드매칭.cs — 교정본
//h 규칙:
//h ① 현재가가 위치한 밴드(locatedBand) 찾기 (@price > low AND @price <= high)
//h ② 매수: 거래밴드 = locatedBand - 1
//h    매도: 거래밴드 = locatedBand
//h ③ loop: 거래밴드부터 시작밴드까지(하향) 순서대로 매매 실행

