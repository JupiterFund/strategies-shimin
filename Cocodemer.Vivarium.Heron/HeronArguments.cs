using Cocodemer.Vivarium.Foundation.Attributes;
using Cocodemer.Vivarium.Foundation.Utility.Simple;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cocodemer.Vivarium.Heron
{
    public class HeronArguments : BaldArguments
    {
        [StrategyArgument(Parser = nameof(ReadString))]
        public string ZgPrice { get; set; }
        [StrategyArgument(Parser = nameof(ReadTimeSpan))]
        public TimeSpan[][] TradingPeriods { get; set; }
        [StrategyArgument(Parser = nameof(ReadTimeSpan))]
        public TimeSpan[][] ValidTickPeriods { get; set; }
        [StrategyArgument(Parser = nameof(ReadLong))]
        public long Volume { get; set; }
        [StrategyArgument(Parser = nameof(ReadTimeSpan))]
        public TimeSpan CloseTime { get; set; }
        [StrategyArgument(Parser = nameof(ReadDouble))]
        public double ToleratedTickGapForSeconds { get; set; }
        [StrategyArgument(Parser = nameof(ReadDouble))]
        public double OpenThreshold { get; set; }
        [StrategyArgument(Parser = nameof(ReadDouble))]
        public double CloseThreshold { get; set; }
        [StrategyArgument(Parser = nameof(ReadDouble))]
        public double MaxWaitSeconds { get; set; }
        [StrategyArgument(Parser = nameof(ReadInt))]
        public int SlipTickCount { get; set; }
    }
}
