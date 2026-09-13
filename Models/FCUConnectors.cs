using Autodesk.Revit.DB;

namespace FCUAutoDesign
{
    internal class FCUConnectors
    {
        public Connector SupplyConnector { get; set; }
        public Connector ReturnConnector { get; set; }
        public Connector CondensateConnector { get; set; }
    }
}
