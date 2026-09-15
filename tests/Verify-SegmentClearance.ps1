$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase, PresentationCore
$source = Get-Content (Join-Path $PSScriptRoot '..\Geometry\SegmentClearance.cs') -Raw -Encoding UTF8
$checks = @'
namespace FCUAutoDesign
{
    public static class ClearanceChecks
    {
        static int count;
        static Point3D P(double x,double y,double z) {return new Point3D(x,y,z);}
        static void Check(bool value,string name) {if(!value)throw new Exception("FAIL: "+name);count++;Console.WriteLine("PASS: "+name);}
        static void Distance(Point3D a,Point3D b,Point3D c,Point3D d,double expected,string name)
        {
            Check(Math.Abs(SegmentClearance.Distance(a,b,c,d)-expected)<1e-9,name);
            Check(Math.Abs(SegmentClearance.Distance(d,c,b,a)-expected)<1e-9,name+" reversed");
        }
        public static void Run()
        {
            Distance(P(0,0,0),P(1,0,0),P(.5,-1,0),P(.5,1,0),0,"Crossing interiors");
            Distance(P(0,0,0),P(1,0,0),P(.5,-1,2),P(.5,1,2),2,"Skew interiors");
            Distance(P(0,0,0),P(1,0,0),P(0,2,0),P(1,2,0),2,"Parallel separated");
            Distance(P(0,0,0),P(1,0,0),P(2,0,0),P(3,0,0),1,"Collinear separated");
            Distance(P(0,0,0),P(1,0,0),P(.5,0,0),P(2,0,0),0,"Collinear overlap");
            Distance(P(0,0,0),P(0,0,0),P(1,0,0),P(1,0,0),1,"Degenerate points");
            Distance(P(0,0,0),P(1,0,0),P(2,1,0),P(2,2,0),Math.Sqrt(2),"Closest endpoints");
            int blocked=0;
            for(int i=0;i<9;i++)
                if(SegmentClearance.Distance(P(0,0,3),P(.4+i*.1,0,3),P(.4,0,2.6),P(.4,0,3.2))<.02) blocked++;
            Check(blocked==9,"All long outlet prefixes rejected independent of later route");
            Check(SegmentClearance.Distance(P(0,0,3),P(.24,0,3),P(.4,0,2.6),P(.4,0,3.2))>.02,"Early turn prefix is not blocked");
            Console.WriteLine(count+" clearance checks passed. Revit performance and solid collision acceptance: NOT_RUN.");
        }
    }
}
'@
Add-Type -TypeDefinition ($source + [Environment]::NewLine + $checks) -ReferencedAssemblies WindowsBase, PresentationCore
[FCUAutoDesign.ClearanceChecks]::Run()
