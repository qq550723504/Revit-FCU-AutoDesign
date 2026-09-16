namespace FCUAutoDesign
{
    internal class RoomExecutionResult
    {
        public string RoomLabel { get; set; }
        public FcuDesignResult Design { get; set; }
        public string Error { get; set; }
        public bool NotRun { get; set; }
        public System.TimeSpan Elapsed { get; set; }
        public string Status(FcuDesignOptions options)
        {
            if (NotRun) return "未执行";
            if (Design == null) return "失败（已回滚）";
            foreach (FcuDesignResult device in Design.Devices)
            {
                ExecutionOutcome o = device.Outcome;
                if (!o.SupplyTeeConnected || (options.EnableReturnPipe && !o.ReturnTeeConnected)
                    || (options.EnableCondensate && !o.CondensateConnected)) return "部分完成";
            }
            return "连接完成";
        }
    }
}
