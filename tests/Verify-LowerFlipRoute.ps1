# Exercises the planner actually called by HydronicConnectionService (including condensate).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase, PresentationCore
$source = Get-Content (Join-Path $PSScriptRoot '..\Geometry\LowerFlipRoutePlanner.cs') -Raw -Encoding UTF8
$locator = Get-Content (Join-Path $PSScriptRoot '..\Geometry\MainPipeSegmentLocator.cs') -Raw -Encoding UTF8
$checks = @'
namespace FCUAutoDesign
{
    public static class LowerFlipChecks
    {
        private static int count;
        private static System.Windows.Media.Media3D.Point3D P(double x, double y, double z)
        { return new System.Windows.Media.Media3D.Point3D(x, y, z); }
        private static System.Windows.Media.Media3D.Vector3D V(double x, double y, double z)
        { return new System.Windows.Media.Media3D.Vector3D(x, y, z); }
        private static void Check(bool ok, string name)
        {
            if (!ok) throw new System.Exception("FAIL: " + name);
            count++; System.Console.WriteLine("PASS: " + name);
        }
        private static void Reject(System.Action f, string name)
        {
            try { f(); }
            catch (System.InvalidOperationException) { Check(true, name); return; }
            throw new System.Exception("FAIL: " + name);
        }
        private static bool RightAngles(System.Windows.Media.Media3D.Point3D[] points)
        {
            for (int i = 1; i < points.Length; i++)
            {
                var a = points[i] - points[i - 1];
                if (a.Length <= .001) return false;
                if (i < 2) continue;
                var b = points[i - 1] - points[i - 2];
                if (System.Math.Abs(System.Windows.Media.Media3D.Vector3D.DotProduct(a,b)) > 1e-8)
                    return false;
            }
            return true;
        }
        private static System.Windows.Media.Media3D.Point3D Project(
            System.Windows.Media.Media3D.Point3D point,
            System.Windows.Media.Media3D.Point3D a, System.Windows.Media.Media3D.Point3D b)
        {
            var axis = b-a;
            double t = ((point.X-a.X)*axis.X + (point.Y-a.Y)*axis.Y)/(axis.X*axis.X+axis.Y*axis.Y);
            return a+axis*t;
        }
        public static void Run()
        {
            var start = P(0,0,3); var dir = V(1,0,0);
            var basis = LowerFlipRoutePlanner.Approach(start,dir,.4,.15,0,.001);
            var plain = LowerFlipRoutePlanner.Complete(basis,P(.4,4,2),.001);
            Check(plain.Length == 5 && plain[0] == start && plain[1] == P(.4,0,3)
                && plain[2] == P(.4,0,2.85) && plain[3] == P(.4,4,2.85)
                && plain[4] == P(.4,4,2), "Existing four-segment hydronic path preserved");

            // Previous lateral-after-drop route: co-directed or reversing horizontal segments.
            var oldForward = new[]{start,P(.4,0,3),P(.4,0,2.85),P(.4,.2,2.85),P(.4,4,2.85),P(.4,4,2)};
            var oldReverse = new[]{start,P(.4,0,3),P(.4,0,2.85),P(.4,-.2,2.85),P(.4,4,2.85),P(.4,4,2)};
            Check(!RightAngles(oldForward) && !RightAngles(oldReverse), "Reproduced both invalid lateral-after-drop bends");
            Reject(()=>LowerFlipRoutePlanner.Validate(oldForward,.001), "Collinear elbow rejected before model creation");
            Reject(()=>LowerFlipRoutePlanner.Validate(oldReverse,.001), "Reversing elbow rejected before model creation");

            foreach (double side in new[]{-.2,.2})
            {
                var approach = LowerFlipRoutePlanner.Approach(start,dir,.4,.15,side,.001);
                var route = LowerFlipRoutePlanner.Complete(approach,P(.4,4,2),.001);
                Check(route.Length == 6 && RightAngles(route), "Lateral-before-drop gives four right-angle elbows: " + side);
                Check(route[1] == P(.4,0,3) && route[2] == P(.4,side,3)
                    && route[3] == P(.4,side,2.85), "Lateral shift moves the drop leg: " + side);
            }
            // Same plan remains valid for parallel, perpendicular, diagonal and reversed mains.
            int routes = 0;
            foreach (double angle in new[]{0.0, System.Math.PI/2, .73})
            foreach (bool reverse in new[]{false,true})
            foreach (bool perpendicular in new[]{false,true})
            {
                var d = V(System.Math.Cos(angle),System.Math.Sin(angle),0);
                var side = V(-d.Y,d.X,0);
                var origin = P(100,200,30);
                var axis = perpendicular ? side : d;
                var center = origin + (perpendicular ? d : side)*4 - V(0,0,2);
                var a = center-axis*10; var b = center+axis*10;
                if (reverse) { var temp=a; a=b; b=temp; }
                int candidates=0;
                foreach (int shift in new[]{0,-1,1,-2,2})
                for (int lead=0;lead<=8;lead++)
                for (int drop=0;drop<=(shift==0?8:4);drop++)
                {
                    var approach = LowerFlipRoutePlanner.Approach(origin,d,.4+lead*.1,.15+drop*.1,shift*.1,.001);
                    var join = Project(approach[approach.Length-1],a,b);
                    var route = LowerFlipRoutePlanner.Complete(approach,join,.001);
                    if (!RightAngles(route) || route[0] != origin || (route[route.Length-1]-join).Length > 1e-8)
                        throw new System.Exception("FAIL: candidate grid geometry");
                    candidates++; routes++;
                }
                Check(candidates==261,"All 261 active candidates have valid bends: angle="+angle+", reverse="+reverse+", perpendicular="+perpendicular);
            }

            var moved = LowerFlipRoutePlanner.Approach(P(0,4.7,3),dir,.4,.15,.6,.001);
            var aParts = new[]{P(4,0,2),P(4,5.2,2)};
            var bParts = new[]{P(4,4.8,2),P(4,10,2)};
            Check(MainPipeSegmentLocator.Locate(aParts,bParts,moved[moved.Length-1],.001)==1,
                "Shifted approach selects the correct split main segment");
            var gap = LowerFlipRoutePlanner.Approach(P(0,4.7,3),dir,.4,.15,.3,.001);
            Reject(()=>MainPipeSegmentLocator.Locate(aParts,bParts,gap[gap.Length-1],.001), "Shifted approach in tee gap rejected");
            var beyond = LowerFlipRoutePlanner.Approach(P(0,9.7,3),dir,.4,.15,.6,.001);
            Reject(()=>MainPipeSegmentLocator.Locate(aParts,bParts,beyond[beyond.Length-1],.001), "Shifted approach outside main rejected");
            Reject(()=>LowerFlipRoutePlanner.Complete(basis,P(.4,0,2),.001), "Zero approach segment rejected");
            Reject(()=>LowerFlipRoutePlanner.Complete(basis,P(.4,4,2.85),.001), "Zero final riser rejected");
            Check(RightAngles(LowerFlipRoutePlanner.Complete(basis,P(.4,4,4),.001)), "Higher main preserves existing geometric behavior");
            Check(LowerFlipRoutePlanner.Approach(start,V(2,0,0),.4,.15,0,.001)[1] == P(.4,0,3), "Outward direction normalized");
            Reject(()=>LowerFlipRoutePlanner.Approach(start,dir,.4,.15,double.NaN,.001), "NaN offset rejected");
            Reject(()=>LowerFlipRoutePlanner.Approach(start,dir,.4,.15,.0001,.001), "Short lateral segment rejected");
            Reject(()=>LowerFlipRoutePlanner.Approach(start,dir,.4,-.15,0,.001), "Negative drop rejected");
            Reject(()=>LowerFlipRoutePlanner.Approach(start,V(0,0,1),.4,.15,0,.001), "Vertical connector rejected");
            Reject(()=>LowerFlipRoutePlanner.Approach(P(double.NaN,0,3),dir,.4,.15,0,.001), "Invalid coordinates rejected");
            System.Console.WriteLine(count + " checks passed; " + routes + " active route candidates validated. Revit fitting, collision and rollback acceptance: NOT_RUN.");
        }
    }
}
'@
# The planner already imports the namespaces used by the locator.
$locator = [regex]::Replace($locator, '(?m)^using .*;\r?$', '')
Add-Type -TypeDefinition ($source + [Environment]::NewLine + $locator + [Environment]::NewLine + $checks) -ReferencedAssemblies 'WindowsBase', 'PresentationCore'
[FCUAutoDesign.LowerFlipChecks]::Run()
