namespace FCUAutoDesign
{
    internal class RoomExecutionResult
    {
        public string RoomLabel { get; set; }
        public FcuDesignResult Design { get; set; }
        public string Error { get; set; }
        public bool NotRun { get; set; }
        public string Status(FcuDesignOptions options)
        {
            if (NotRun) return "未执行";
            if (Design == null) return "失败（已回滚）";
            ExecutionOutcome o = Design.Outcome;
            return o.SupplyTeeConnected && (!options.EnableReturnPipe || o.ReturnTeeConnected)
                && (!options.EnableCondensate || o.CondensateConnected) ? "连接完成" : "部分完成";
        }
    }
}
