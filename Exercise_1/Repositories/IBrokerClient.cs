//using Exercise_1;

//역할 정리
//1. 외부 API와 내부 프로그램 사이의 번역기

//증권사 API(XING, 키움 등)는 각각 고유한 TR코드, 파라미터, 이벤트 방식이 있습니다.

//그대로 쓰면 프로그램 전체가 특정 증권사 API에 종속됩니다.

//→ 추상화를 두면 GetDailyBalance(), PlaceOrder(), CancelOrder() 같은 공통 메서드만 사용하면 되고, 내부에서는 어떤 증권사 API를 쓰는지 몰라도 됩니다.

//2. 관심사 분리 (Separation of Concerns)

//UI(Login.cs, ListView 출력)나 DB(DbFuncs)는 단순히 추상화된 결과만 받습니다.

//예:

//DailyBalanceSnapshot 클래스(ymd, 보유량, 현금, D2, 총자산 …)

//OrderAck, FillEvent 등

//이렇게 데이터 모델을 통일해 두면 프로그램 나머지는 API 세부사항을 몰라도 됩니다.

//3. 테스트 가능성 확보

//증권사 API는 테스트하기 까다롭습니다(실계좌 필요, 장시간 대기 등).

//추상화 인터페이스만 있으면,

//MockBrokerClient로 가짜 데이터를 반환 → 단위테스트 가능

//실거래는 XingBrokerClient 같은 실제 구현체만 교체

//4. 확장성 / 교체 용이성

//처음엔 XING API로 시작하더라도, 나중에 키움/미래에셋으로 바꿀 수 있습니다.

//IBrokerClient 인터페이스는 그대로 두고, 새로운 증권사 구현체만 추가하면 끝.

//즉, 프로그램 구조가 특정 브로커사에 묶이지 않습니다.
using System;
using Exercise_1;



public interface IBrokerClient
{
    // 증권사에서 "오늘"의 계좌 스냅샷(잔고/현금/평가) 조회
    DailyBalanceSnapshot GetDailyBalanceToday(string accountNo);
}
