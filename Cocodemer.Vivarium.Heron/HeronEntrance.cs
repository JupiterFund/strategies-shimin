using Cocodemer.Foundation;
using Cocodemer.Foundation.Collections;
using Cocodemer.Foundation.Log;
using Cocodemer.Foundation.Utilities;
using Cocodemer.Model;
using Cocodemer.Model.Crypto;
using Cocodemer.Model.Orders;
using Cocodemer.Vivarium.Foundation.Strategy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cocodemer.Vivarium.Heron
{
    public class ConBondStockInfo : GuidObject
    {
        public Instrument ConvertibleBond { get; private set; }
        public Instrument Stock { get; private set; }
        public double? ConvertPrice { get; private set; }
        public DepthMarketData CbLastTick;
        public DepthMarketData StockLastTick;
        public StrategyOrder ProcessingOrder;

        public ConBondStockInfo(Instrument convertibleBond, Instrument stock, double convertPrice)
        {
            ConvertibleBond = convertibleBond;
            Stock = stock;
            ConvertPrice = convertPrice;
            CbLastTick = null;
            StockLastTick = null;
            ProcessingOrder = null;
        }

        public double? Premium
        {
            get
            {
                if (CbLastTick != null && StockLastTick != null)
                {
                    return CbLastTick.LastPrice / (StockLastTick.LastPrice * 100 / ConvertPrice.Value) - 1;
                }
                else
                {
                    return null;
                }
            }
        }
    }

    public class HeronEntrance : BaldStrategyBase<HeronInfo, HeronArguments>
    {
        private List<ConBondStockInfo> _conBondStockInfos = new List<ConBondStockInfo>();


        protected override void OnStrategyLaunching(DateTime time, ApartmentLaunchType type)
        {
            base.OnStrategyLaunching(time, type);
            if (Info == null)
            {
                Info = new HeronInfo();
            }
            string[] cps = Arguments.ZgPrice.Split(new[] { '~'}, StringSplitOptions.RemoveEmptyEntries);
            _conBondStockInfos = cps.Select(x => 
            {
                var zz = x.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                return new ConBondStockInfo(Instruments.First(y => y.Name == zz[0]), Instruments.First(y => y.Name == zz[1]), Convert.ToDouble(zz[2]));
            }).ToList();
            if (_conBondStockInfos.Count + _conBondStockInfos.Select(x=>x.Stock).Distinct().Count() == Instruments.Length)
            {
                foreach (var info in _conBondStockInfos)
                {
                    if (!(Instruments.Contains(info.ConvertibleBond)))
                    {
                        throw new CocoException($"无法在证券订阅集合中找到{info.ConvertibleBond.Name}");
                    }
                    else if (!Instruments.Contains(info.Stock))
                    {
                        throw new CocoException($"无法在证券订阅集合中找到{info.Stock.Name}");
                    }
                }
            }
            else
            {
                throw new CocoException("参数中配置的Instrument与订阅的Instrument长度不一致.");
            }
        }

        protected override void OnStrategyShutingdown(DateTime time, ApartmentShutdownType type)
        {
            try
            {
                TSLog(CocoLogLevel.Fatal, () => Status);
            }
            catch
            { }
            base.OnStrategyShutingdown(time, type);
        }
        private bool InTradingPeriod(TimeSpan time)
        {
            for (int i = 0; i < Arguments.TradingPeriods.Length; i++)
            {
                if (time >= Arguments.TradingPeriods[i][0]
                    && time < Arguments.TradingPeriods[i][1])
                {
                    return true;
                }
            }

            return false;
        }
        private bool InTickValidPeriod(TimeSpan time)
        {
            for (int i = 0; i < Arguments.ValidTickPeriods.Length; i++)
            {
                if (time >= Arguments.ValidTickPeriods[i][0]
                    && time < Arguments.ValidTickPeriods[i][1])
                {
                    return true;
                }
            }

            return false;
        }

        private bool _isClosingAll = false;
        protected override void BaldRun(QuickDictionary<Instrument, ApartmentTicks> triggeringTicks, List<StrategyOrder> buffer)
        {
            foreach (var tick in triggeringTicks)
            {
                var depth = tick.Value.GlobalLast();
                if (InTickValidPeriod(depth.ExchangeTime.TimeOfDay))
                {
                    foreach (var cb in _conBondStockInfos)
                    {
                        if (cb.ConvertibleBond == depth.Instrument)
                        {
                            cb.CbLastTick = depth;
                        }
                        else if (cb.Stock == depth.Instrument)
                        {
                            cb.StockLastTick = depth;
                        }
                    }

                }
            }


            if (StrategyApartment.CurrentLocalTime.TimeOfDay >= Arguments.CloseTime)
            {
                if (!_isClosingAll)
                {
                    if (_conBondStockInfos.All(x => x.ProcessingOrder == null))
                    {
                        //平掉所有仓位
                        CloseAllPositions(buffer);
                        _isClosingAll = true;
                        TSLog(CocoLogLevel.Fatal, ()=>"Start to close all position.");
                    }
                    else//先撤掉未完成的单子
                    {
                        _conBondStockInfos.Where(x => x.ProcessingOrder != null && (bool)x.ProcessingOrder.Cookie == false).ToList()
                            .ForEach(x =>
                            {
                                x.ProcessingOrder.Cookie = true;
                                StrategyApartment.CancelOrder(x.ProcessingOrder);
                            });
                    }
                }
            }
            else
            {
                foreach (var tick in triggeringTicks)
                {
                    var depth = tick.Value.GlobalLast();
                    foreach (var cb in _conBondStockInfos)
                    {
                        if (cb.ConvertibleBond != depth.Instrument && cb.Stock != depth.Instrument)
                        {
                            continue;
                        }
                        if (InTradingPeriod(depth.ExchangeTime.TimeOfDay))//在交易时段内
                        {
                            var pos = StrategyApartment.CapitalManagers[0].PositionBook.GetPosition(cb.ConvertibleBond, PositionType.Long);
                            TimeSpan timediff = TimeSpan.MaxValue;
                            if (cb.CbLastTick != null && cb.StockLastTick != null)
                            {
                                timediff = cb.CbLastTick.ExchangeTime - cb.StockLastTick.ExchangeTime;
                            }
                            if (cb.ProcessingOrder == null//没有处理的单子
                                && Math.Abs(timediff.TotalSeconds) < Arguments.ToleratedTickGapForSeconds)
                            {
                                if (pos.GetHoldingVolume() == 0)//没有仓位
                                {
                                    if (cb.Premium != null && cb.Premium.Value < Arguments.OpenThreshold)//溢价率小于阈值
                                    {
                                        if (!(cb.Stock.LimitPriceInfo != null && cb.StockLastTick.LastPrice.AlmostEquals(cb.Stock.LimitPriceInfo.LowerLimitPrice)))//非跌停
                                        {
                                            //开仓
                                            var order = StrategyOrder.CreateOrder(cb.ConvertibleBond, 0, OpenCloseType.Open, PositionType.Long, Arguments.Volume, cb.CbLastTick.LastPrice);
                                            order.TimeoutBeforeCocoCancel = TimeSpan.FromSeconds(Arguments.MaxWaitSeconds);
                                            order.Cookie = false;
                                            order.SlipTickCount = Arguments.SlipTickCount;
                                            buffer.Add(order);
                                            cb.ProcessingOrder = order;
                                            TSLog(CocoLogLevel.Notice,
                                                () => $"[BaldRun.{order.BuySell}] " + $"ID:{order.ID} [{order.BuySell}] [{order.NativeVolume}] LB [{order.Instrument}] @ [{order.LimitPrice.Value}] [Prem:{cb.Premium}]");
                                        }
                                    }
                                }
                                else//有仓位
                                {
                                    if (cb.Premium != null && cb.Premium.Value >= Arguments.CloseThreshold)//溢价率小于阈值
                                    {
                                        //平仓
                                        var order = StrategyOrder.CreateOrder(cb.ConvertibleBond, 0, OpenCloseType.Close, PositionType.Long, pos.GetHoldingVolume(), cb.CbLastTick.LastPrice);
                                        order.TimeoutBeforeCocoCancel = TimeSpan.FromSeconds(Arguments.MaxWaitSeconds);
                                        order.Cookie = false;
                                        order.SlipTickCount = Arguments.SlipTickCount;
                                        buffer.Add(order);
                                        cb.ProcessingOrder = order;
                                        TSLog(CocoLogLevel.Notice,
                                            () => $"[BaldRun.{order.BuySell}] " + $"ID:{order.ID} [{order.BuySell}] [{order.NativeVolume}] LB [{order.Instrument}] @ [{order.LimitPrice.Value}] [Prem:{cb.Premium}]");
                                    }
                                }
                            }
                            else if (cb.ProcessingOrder != null && (bool)cb.ProcessingOrder.Cookie == false)//有正在处理的单子 and 没有在撤单
                            {
                                if (cb.ProcessingOrder.OpenClose == OpenCloseType.Open)
                                {
                                    if (cb.Premium.Value >= Arguments.CloseThreshold)
                                    {
                                        cb.ProcessingOrder.Cookie = true;
                                        StrategyApartment.CancelOrder(cb.ProcessingOrder);
                                        TSLog(CocoLogLevel.Notice, () => $"[BaldRun.Cancel] ID:{cb.ProcessingOrder.ID} [{cb.ProcessingOrder.BuySell}] [{cb.ProcessingOrder.NativeVolume}] LB [{cb.ProcessingOrder.Instrument}] @ [{cb.ProcessingOrder.LimitPrice.Value}] [Prem:{cb.Premium}]");
                                    }
                                }
                                else
                                {
                                    if (cb.Premium.Value <= Arguments.OpenThreshold)
                                    {
                                        cb.ProcessingOrder.Cookie = true;
                                        StrategyApartment.CancelOrder(cb.ProcessingOrder);
                                        TSLog(CocoLogLevel.Notice, () => $"[BaldRun.Cancel] ID:{cb.ProcessingOrder.ID} [{cb.ProcessingOrder.BuySell}] [{cb.ProcessingOrder.NativeVolume}] LB [{cb.ProcessingOrder.Instrument}] @ [{cb.ProcessingOrder.LimitPrice.Value}] [Prem:{cb.Premium}]");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private void CloseAllPositions(List<StrategyOrder> buffer)
        {
            var allPos = StrategyApartment.GetPositions();
            foreach (var info in _conBondStockInfos)
            {
                var pos = allPos.FirstOrDefault(x=>x.Instrument == info.ConvertibleBond.Name);
                if (pos != null)
                {
                    var order = StrategyOrder.CreateOrder(info.ConvertibleBond, 0, OpenCloseType.Close, PositionType.Long, pos.NativeVolume, info.CbLastTick.AskPrice[0]);
                    order.TimeoutBeforeCocoCancel = TimeSpan.FromSeconds(4);
                    order.Cookie = false;
                    order.SlipTickCount = Arguments.SlipTickCount;
                    buffer.Add(order);
                    info.ProcessingOrder = order;
                }
            }
        }

        public override bool CanSoftStop => _conBondStockInfos.All(x=>x.ProcessingOrder == null);


        public override void OnOrderFinished(StrategyOrder confirmedOrder, List<StrategyOrder> buffer)
        {
            TSLog(CocoLogLevel.Error, 
                () => $"[OnOrderFinished] ID:{confirmedOrder.ID} {confirmedOrder.BuySell} [{confirmedOrder.TradedVolumePrice.Volume}|{confirmedOrder.NativeVolume}] LB [{confirmedOrder.Instrument}] @ [{confirmedOrder.TradedVolumePrice.Price}]");
            if (StrategyApartment.CurrentLocalTime.TimeOfDay >= Arguments.CloseTime)
            {
                var cb = _conBondStockInfos.First(x => x.ProcessingOrder == confirmedOrder);
                if (_isClosingAll)
                {
                    if (confirmedOrder.OrderCompletionType != OrderCompletionType.FullyCompleted)
                    {
                        var order = confirmedOrder.CreateRemainOrder(cb.CbLastTick.BidPrice[0], Arguments.SlipTickCount);
                        order.Cookie = false;
                        buffer.Add(order);
                        cb.ProcessingOrder = order;
                    }
                    else
                    {
                        cb.ProcessingOrder = null;
                    }
                }
                else
                {
                    cb.ProcessingOrder = null;
                }
            }
            else
            {
                var cb = _conBondStockInfos.First(x => x.ProcessingOrder == confirmedOrder);
                cb.ProcessingOrder = null;
            }
        }

        public override string Status
        {
            get
            {
                var strBuilder = new StringBuilder();
                foreach (var info in _conBondStockInfos)
                {
                    var orderInfo = info.ProcessingOrder != null
                        ? $"{info.ProcessingOrder?.ID} {info.ProcessingOrder?.GeneratedLocalTime:s} {info.ProcessingOrder?.LimitPrice.Value} {info.ProcessingOrder?.NativeVolume}"
                        : null;
                    strBuilder.Append($"[{info.ConvertibleBond.Name}] [{info.Stock.Name}] [Prem:{info.Premium}] [Order: {orderInfo}]");

                }

                return strBuilder.ToString();
            }
        }

        public override void OnOrderLocalFinished(StrategyOrder confirmedOrder, OrderLocalFinishedStatus status, List<StrategyOrder> buffer)
        {
            if (status != OrderLocalFinishedStatus.None)
            {
                TSLog(CocoLogLevel.Error, () => $"OnOrderLocalFinished Error:{status}");
            }
        }

        public override void OnOrderTraded(StrategyOrder confirmedOrder, TradeBlock trade, List<StrategyOrder> buffer)
        {
            //not implement
        }

        public override void OnTransferCryptoFinished(long transferID, CryptoTransferResult result)
        {
            //not implement
        }
    }
}
