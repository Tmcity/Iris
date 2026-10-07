await using var sta=new ReviewSta();
if(args.Contains("--preparation"))await HistoryChecks.Preparation(sta);
else if(args.Contains("--lifecycle"))await HistoryChecks.Lifecycle(sta);
else if(args.Contains("--history"))await HistoryChecks.Basic(sta);
else await HistorySettingsChecks.Run(sta);
