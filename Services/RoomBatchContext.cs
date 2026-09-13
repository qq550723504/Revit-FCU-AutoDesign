using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace FCUAutoDesign
{
    internal class RoomBatchContext
    {
        public MainPipeRun Supply { get; }
        public MainPipeRun Return { get; }
        public MainPipeRun Condensate { get; }
        private readonly List<FcuDesignResult> completed = new List<FcuDesignResult>();
        public RoomBatchContext(Pipe supply, Pipe ret, Pipe condensate)
        {
            Supply = new MainPipeRun(supply);
            Return = ret == null ? null : new MainPipeRun(ret);
            Condensate = condensate == null ? null : new MainPipeRun(condensate);
        }
        public void Register(FcuDesignResult result)
        {
            Supply.Register(result.SupplyConnection);
            Return?.Register(result.ReturnConnection);
            Condensate?.Register(result.DrainConnection);
            completed.Add(result);
        }
        public void VerifyNew(Document doc, TeeConnectionResult candidate)
        {
            foreach (FcuDesignResult previous in completed)
                foreach (TeeConnectionResult circuit in new[] { previous.SupplyConnection, previous.ReturnConnection, previous.DrainConnection })
                    CircuitInterferenceVerifier.Verify(doc, circuit, candidate, "先前房间", "当前房间", false);
        }
        public void VerifyPrevious(Document doc, TeeConnectionResult supply, TeeConnectionResult ret, TeeConnectionResult drain)
        {
            VerifyNew(doc, supply); VerifyNew(doc, ret); VerifyNew(doc, drain);
            foreach (FcuDesignResult previous in completed)
            {
                Supply.VerifyPrevious(doc, previous.SupplyConnection, supply);
                Return?.VerifyPrevious(doc, previous.ReturnConnection, ret);
                Condensate?.VerifyPrevious(doc, previous.DrainConnection, drain);
            }
        }
    }
}
