//// StateAndDecisionUnit.cs — 결정적 모드 + 연속돌파 누적 + CrossUp 팝업
//// h  TICK=50, REV_TICKS=3(150원) 고정
//// h  CrossUp/Down 시 armed 리스트 누적
//// h  turnedDown/Up 시 armed 리스트 전부 체결
//// h  Sell 폴백: armedSell 비어도 turnedDown이면 시작밴드 1건 매도
//// h  CrossUp 감지 시 밴드번호 MessageBox 표시

//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Windows.Forms; // ✅ MessageBox

//namespace Exercise_1.Domain
//{
//    public interface IStateAndDecisionUnit
//    {
//        //void LoadBands(IEnumerable<Exercise_1.BandRange> bands);
//        //void RefreshBands(IEnumerable<Exercise_1.BandRange> bands);
//        void SetAccount(double cash, long position);
//        Exercise_1.Decision OnTick(Exercise_1.Tick tick);                 // 단일 결과(호환)
//        Exercise_1.Domain.BandMultiDecision OnTickMulti(Exercise_1.Tick tick); // 다밴드 결과(권장)
//    }

//    public sealed class StateAndDecisionUnit : Exercise_1.Domain.IStateAndDecisionUnit
//    {
//        const int REV_TICKS = 3;
//        const int TICK = 50;

//        //IReadOnlyList<Exercise_1.BandRange> _bands = new List<Exercise_1.BandRange>();

//        double _cash;
//        long _position;

//        double _lastPrice = double.NaN;
//        int _lastDir = 0;
//        double _swingHigh = double.NaN;
//        double _swingLow = double.NaN;

//        // 누적 armed 리스트
//        readonly SortedSet<int> _armedBuy = new SortedSet<int>();   // 낮은→높은
//        readonly SortedSet<int> _armedSell = new SortedSet<int>();  // 낮은→높은

//        public void LoadBands(IEnumerable<Exercise_1.BandRange> bands)
//        {
//            _bands = (bands ?? Enumerable.Empty<Exercise_1.BandRange>()).OrderBy(b => b.Band).ToList();
//            LogStart();
//        }

//        public void RefreshBands(IEnumerable<Exercise_1.BandRange> bands)
//        {
//            _bands = (bands ?? Enumerable.Empty<Exercise_1.BandRange>()).OrderBy(b => b.Band).ToList();
//            LogStart();
//        }

//        void LogStart()
//        {
//            var start = GetStartBand();
//            Console.WriteLine($"[SDU] bands={_bands.Count}, start={(start?.Band.ToString() ?? "-")}");
//        }

//        public void SetAccount(double cash, long position)
//        {
//            _cash = cash;
//            _position = position;
//        }

//        public Exercise_1.Decision OnTick(Exercise_1.Tick tick)
//        {
//            var md = OnTickMulti(tick);
//            if (md.Signals.Count == 0)
//                return new Exercise_1.Decision { Side = Exercise_1.DecisionSide.None, Reason = "no-signal", Qty = 0 };

//            var sig = md.Signals[0];
//            return new Exercise_1.Decision
//            {
//                Side = sig.Side,
//                TargetBand = sig.TargetBands.FirstOrDefault(),
//                Qty = sig.QtyHint,
//                Reason = sig.Reason
//            };
//        }

//        public Exercise_1.Domain.BandMultiDecision OnTickMulti(Exercise_1.Tick tick)
//        {
//            var result = new Exercise_1.Domain.BandMultiDecision();
//            if (tick == null || _bands.Count == 0)
//                return result;

//            double price = tick.Price;
//            double prevPrice = _lastPrice;
//            int revWon = TICK * REV_TICKS;

//            // 방향/스윙
//            int dir = 0;
//            const double eps = 1e-9;
//            if (!double.IsNaN(prevPrice))
//            {
//                if (price > prevPrice + eps) dir = +1;
//                else if (price < prevPrice - eps) dir = -1;
//            }
//            if (double.IsNaN(_swingHigh) || double.IsNaN(_swingLow))
//                _swingHigh = _swingLow = price;
//            if (dir > 0) _swingHigh = Math.Max(_swingHigh, price);
//            if (dir < 0) _swingLow = Math.Min(_swingLow, price);

//            bool turnedDown = false, turnedUp = false;
//            if (_lastDir >= 0 && (_swingHigh - price) >= revWon)
//            { turnedDown = true; _lastDir = -1; _swingLow = _swingHigh = price; }
//            if (!turnedDown && _lastDir <= 0 && (price - _swingLow) >= revWon)
//            { turnedUp = true; _lastDir = +1; _swingHigh = _swingLow = price; }
//            if (_lastDir == 0 && dir != 0) _lastDir = dir;


//            // 밴드 이벤트
//            var evtInfo = GetBandEvent(price, prevPrice);
////            MessageBox.Show(
////    $"evtInfo.Event = {evtInfo.Event}\n" +
////    $"BandEvent.CrossUp = {BandEvent.CrossUp}\n" +
////    $"evtInfo.Hit = {(evtInfo.Hit == null ? "null" : evtInfo.Hit.Band.ToString())}",
////    "디버그: 이벤트 비교 전 상태",
////    MessageBoxButtons.OK,
////    MessageBoxIcon.Information
////);
//            // ✅ CrossUp 즉시 디버그 팝업 (밴드/High/현재가)
//            //if (evtInfo.Event == BandEvent.CrossUp && evtInfo.Hit != null)
//            //{
//            //    MessageBox.Show(
//            //        $"밴드 {evtInfo.Hit.Band} 상향 돌파\nHigh={evtInfo.Hit.High:#,0}\n현재가={price:#,0}",
//            //        "CrossUp 감지",
//            //        MessageBoxButtons.OK,
//            //        MessageBoxIcon.Information
//            //    );
//            //}

//            // armed 누적
//            if (evtInfo.Event == BandEvent.CrossDn && evtInfo.Hit != null)
//                _armedBuy.Add(evtInfo.Hit.Band);

//            if (evtInfo.Event == BandEvent.CrossUp && evtInfo.Hit != null)
//            {
//                var start = GetStartBand();
//                if (start != null)
//                {
//                    int from = Math.Min(start.Band, evtInfo.Hit.Band);
//                    int to = Math.Max(start.Band, evtInfo.Hit.Band);
//                    for (int b = from; b <= to; b++)
//                    {
//                        var band = _bands.FirstOrDefault(x => x.Band == b);
//                        if (band != null) _armedSell.Add(b);
//                    }
//                }
//                else _armedSell.Add(evtInfo.Hit.Band);
//            }

//            // 체결
//            if (turnedUp && _armedBuy.Count > 0)
//            {
//                var targets = _armedBuy.ToList();
//                targets.Sort();
//                result.Signals.Add(new Exercise_1.Domain.BandTradeSignal
//                {
//                    Side = Exercise_1.DecisionSide.Buy,
//                    TargetBands = targets,
//                    Reason = "armedBuy-list → turnedUp (multi-buy)",
//                    QtyHint = 0
//                });
//                _armedBuy.Clear();
//            }

//            if (turnedDown)
//            {
//                if (_armedSell.Count > 0)
//                {
//                    var targets = _armedSell.ToList();
//                    targets.Sort();
//                    result.Signals.Add(new Exercise_1.Domain.BandTradeSignal
//                    {
//                        Side = Exercise_1.DecisionSide.Sell,
//                        TargetBands = targets,
//                        Reason = "armedSell-list → turnedDown (multi-sell)",
//                        QtyHint = 0
//                    });
//                    _armedSell.Clear();
//                }
//                else
//                {
//                    var start = GetStartBand();
//                    if (start != null && start.Qty > 0)
//                    {
//                        result.Signals.Add(new Exercise_1.Domain.BandTradeSignal
//                        {
//                            Side = Exercise_1.DecisionSide.Sell,
//                            TargetBands = new List<int> { start.Band },
//                            Reason = "fallback: turnedDown → startBand sell",
//                            QtyHint = 0
//                        });
//                    }
//                }
//            }

//            _lastPrice = price;

//            var sb = GetStartBand();
//            Console.WriteLine($"{tick.TsKst:HH:mm:ss} | P={price:#,0} | dir={_lastDir} | evt={evtInfo.Event}({evtInfo.Hit?.Band.ToString() ?? "-"}) | buyArmed=[{string.Join(",", _armedBuy)}] | sellArmed=[{string.Join(",", _armedSell)}] | start={(sb?.Band.ToString() ?? "-")} | tu={turnedUp} | td={turnedDown} | out={result.Signals.Count}");
//            return result;
//        }

//        // ==== 유틸 ====
//        enum BandEvent { None, Inside, CrossUp, CrossDn }
//        struct Evt { public BandEvent Event; public Exercise_1.BandRange Hit; }

//        Evt GetBandEvent(double price, double prevPrice)
//        {
//            if (double.IsNaN(prevPrice)) return new Evt { Event = BandEvent.None, Hit = null };

//            // 가격이 걸쳐 있는 밴드를 먼저 찾음
//            foreach (var b in _bands)
//            {
//                if (price >= b.Low && price <= b.High)
//                {
//                    var ev = BandEvent.Inside;
//                    if (price > prevPrice && price >= b.High) ev = BandEvent.CrossUp;
//                    else if (price < prevPrice && price <= b.Low) ev = BandEvent.CrossDn;
//                    return new Evt { Event = ev, Hit = b };
//                }
//            }

//            // 범위 밖이면 가장 가까운 위/아래 밴드로 이벤트 근사
//            Exercise_1.BandRange above = null, below = null;
//            foreach (var b in _bands)
//            {
//                if (price > b.High) if (above == null || b.High > above.High) above = b;
//                if (price < b.Low) if (below == null || b.Low < below.Low) below = b;
//            }

//            if (above != null)
//                return new Evt { Event = (price < prevPrice) ? BandEvent.CrossDn : BandEvent.CrossUp, Hit = above };
//            if (below != null)
//                return new Evt { Event = (price > prevPrice) ? BandEvent.CrossUp : BandEvent.CrossDn, Hit = below };

//            return new Evt { Event = BandEvent.None, Hit = null };
//        }

//        private Exercise_1.BandRange GetStartBand()
//            => _bands.Where(b => b.Qty > 0).OrderByDescending(b => b.Band).FirstOrDefault();
//    }

//    // ==== DTOs (SDU 전용, 충돌 방지) ====
//    public sealed class BandMultiDecision
//    {
//        public List<Exercise_1.Domain.BandTradeSignal> Signals { get; } = new List<Exercise_1.Domain.BandTradeSignal>();
//    }

//    public sealed class BandTradeSignal
//    {
//        public Exercise_1.DecisionSide Side { get; set; }
//        public List<int> TargetBands { get; set; }
//        public string Reason { get; set; }
//        public long QtyHint { get; set; }
//    }
//}
