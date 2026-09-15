$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase, PresentationCore
$source = Get-Content (Join-Path $PSScriptRoot '..\Geometry\MainPipeSegmentLocator.cs') -Raw -Encoding UTF8
$checks = @'
namespace FCUAutoDesign
{
    public static class SegmentChecks
    {
        private static int count;
        private static System.Windows.Media.Media3D.Point3D P(double x, double y = 0, double z = 0)
        { return new System.Windows.Media.Media3D.Point3D(x,y,z); }
        private static void Check(bool ok, string name)
        {
            if (!ok) throw new System.Exception("FAIL: " + name);
            count++; System.Console.WriteLine("PASS: " + name);
        }
        private static void Reject(System.Action action, string name)
        {
            try { action(); }
            catch (System.InvalidOperationException) { Check(true,name); return; }
            throw new System.Exception("FAIL: " + name);
        }
        public static void Run()
        {
            Check(MainPipeSegmentLocator.Locate(new[]{P(0)},new[]{P(10)},P(8,3),.01)==0,"Original segment selected");
            var a = new[]{P(0),P(5.2)}; var b = new[]{P(4.8),P(10)};
            Check(MainPipeSegmentLocator.Locate(a,b,P(8,3),.01)==1,"Later room selects newly split second segment");
            Check(MainPipeSegmentLocator.Locate(a,b,P(2,3),.01)==0,"Other room still selects first segment");
            Check(MainPipeSegmentLocator.Locate(b,a,P(8,3),.01)==1,"Reversed endpoint orientation supported");
            Check(MainPipeSegmentLocator.Locate(new[]{a[1],a[0]},new[]{b[1],b[0]},P(8,3),.01)==0,"Enumeration order does not select the wrong segment");
            Reject(()=>MainPipeSegmentLocator.Locate(a,b,P(5,3),.01),"Existing tee gap rejected");
            Reject(()=>MainPipeSegmentLocator.Locate(a,b,P(4.8,3),.01),"Cut endpoint rejected");
            Reject(()=>MainPipeSegmentLocator.Locate(a,b,P(4.795,3),.01),"Too close to endpoint rejected");
            Reject(()=>MainPipeSegmentLocator.Locate(a,b,P(11,3),.01),"Outside original run rejected");
            Reject(()=>MainPipeSegmentLocator.Locate(new[]{P(0),P(1)},new[]{P(10),P(9)},P(5,3),.01),"Overlapping candidate segments rejected as ambiguous");
            Check(MainPipeSegmentLocator.Locate(new[]{P(0),P(5.2),P(8.2)},new[]{P(4.8),P(7.8),P(10)},P(9,3),.01)==2,"Multiple committed splits remain addressable");
            Check(MainPipeSegmentLocator.Locate(new[]{P(100,200,30)},new[]{P(100,210,32)},P(103,205,40),.01)==0,"Translated sloping main located in XY");
            Reject(()=>MainPipeSegmentLocator.Locate(new System.Windows.Media.Media3D.Point3D[0],new System.Windows.Media.Media3D.Point3D[0],P(5),.01),"Empty run rejected");
            Reject(()=>MainPipeSegmentLocator.Locate(a,b,P(double.NaN),.01),"Invalid approach rejected");
            System.Console.WriteLine(count + " segment checks passed. Revit batch integration tests are NOT_RUN.");
        }
    }
}
'@
Add-Type -TypeDefinition ($source + [Environment]::NewLine + $checks) -ReferencedAssemblies 'WindowsBase', 'PresentationCore'
[FCUAutoDesign.SegmentChecks]::Run()
