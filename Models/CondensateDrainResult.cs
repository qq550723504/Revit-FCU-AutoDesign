using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace FCUAutoDesign
{
    internal class CondensateDrainResult
    {
        public bool Connected { get; set; }
        public string ErrorMessage { get; set; }
        public TeeConnectionResult Connection { get; }

        public CondensateDrainResult() : this(new TeeConnectionResult()) { }

        // 保留试建校验过的完整记录，禁止在适配时逐字段复制导致遗漏安装校验数据。
        public CondensateDrainResult(TeeConnectionResult connection)
        {
            if (connection == null) throw new System.ArgumentNullException("connection");
            Connection = connection;
            Connected = connection.BranchCreated && connection.TeeCreated;
            ErrorMessage = connection.ErrorMessage;
        }
        public List<ElementId> PipeIds { get; } = new List<ElementId>();
        public List<int> StartConnectorIds { get; } = new List<int>();
        public List<int> EndConnectorIds { get; } = new List<int>();
        public List<XYZ> PlannedDirections { get; } = new List<XYZ>();
        public double MinLength { get; set; }
    }
}
