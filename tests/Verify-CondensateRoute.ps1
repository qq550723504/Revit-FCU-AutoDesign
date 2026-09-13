# Pure geometry regression; no Revit process or model is accessed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase, PresentationCore
$planner = Get-Content (Join-Path $PSScriptRoot '..\Geometry\CondensateRoutePlanner.cs') -Raw -Encoding UTF8
$checks = @'
namespace FCUAutoDesign
{
    public static class CondensateRouteChecks
    {
        private static int count;
        private static void Check(bool ok, string name)
        {
            if (!ok) throw new System.Exception("FAIL: " + name);
            count++;
            System.Console.WriteLine("PASS: " + name);
        }
        private static void Reject(System.Action action, string reason, string name)
        {
            try { action(); }
            catch (System.InvalidOperationException ex) { Check(ex.Message.Contains(reason), name); return; }
            throw new System.Exception("FAIL: " + name + " was accepted");
        }
        private static System.Windows.Media.Media3D.Point3D P(double x, double y, double z)
        { return new System.Windows.Media.Media3D.Point3D(x, y, z); }
        public static void Run()
        {
            var start = P(0,0,10);
            var dir = new System.Windows.Media.Media3D.Vector3D(1,0,0);
            var a = P(-5,3,8);
            var b = P(5,3,8);
            var route = CondensateRoutePlanner.Plan(start,dir,a,b,.4,.01);
            Check(route.Length == 3 && (route[2]-P(.4,3,8)).Length < 1e-8, "Route reaches actual main elevation without preset grade");
            Check(route[1].Z < start.Z && route[1].Z > route[2].Z, "Actual height difference descends along both segments");
            var level = CondensateRoutePlanner.Plan(start,dir,P(-5,3,10),P(5,3,10),.4,.01);
            Check(level[0].Z == 10 && level[1].Z == 10 && level[2].Z == 10, "Same elevation produces horizontal pipes");
            var straight = CondensateRoutePlanner.Plan(start,dir,P(3,-5,8),P(3,5,8),.4,.01);
            Check(straight.Length == 2, "Collinear route connects directly without extra elbow");
            var sloped = CondensateRoutePlanner.Plan(start,dir,P(-5,3,8),P(5,3,9),.4,.01);
            Check(System.Math.Abs(sloped[2].Z-8.54) < 1e-8, "Sloped main connection uses actual elevation");
            var reversed = CondensateRoutePlanner.Plan(start,dir,b,a,.4,.01);
            Check((reversed[2]-route[2]).Length < 1e-8, "Main endpoint order does not affect join");
            var higher = CondensateRoutePlanner.Plan(start,dir,P(-5,3,11),P(5,3,11),.4,.01);
            Check(higher.Length == 3 && higher[1].Z > start.Z && higher[1].Z < 11
                && higher[2].Z == 11, "Higher main connects without slope direction restriction");
            var directHigher = CondensateRoutePlanner.Plan(start,dir,P(3,-5,12),P(3,5,12),.4,.01);
            Check(directHigher.Length == 2 && directHigher[1].Z == 12, "Direct ascending route accepted");
            Reject(() => CondensateRoutePlanner.Plan(start,dir,P(1,3,8),P(5,3,8),.4,.01), "投影", "Out of bounds projection rejected");
            Reject(() => CondensateRoutePlanner.Plan(start,dir,P(.4,3,8),P(5,3,8),.4,.01), "端点", "Endpoint join rejected");
            Reject(() => CondensateRoutePlanner.Plan(start,dir,P(3,3,5),P(3,3,8),.4,.01), "立管", "Vertical main remains unsupported");
            Reject(() => CondensateRoutePlanner.Plan(start,dir,P(-3,-5,8),P(-3,5,8),.4,.01), "折返", "Hairpin rejected");
            foreach (double shift in new double[] { -.2, -.1, .1, .2 })
            {
                var shifted = CondensateRoutePlanner.Plan(start,dir,a,b,.4,.01,shift);
                Check((shifted[0]-route[0]).Length < 1e-8 && (shifted[2]-route[2]).Length < 1e-8
                    && System.Math.Abs(shifted[1].Z-route[1].Z-shift) < 1e-8,
                    "Detour changes only intermediate elevation and preserves terminal points");
            }
            var bentStraight = CondensateRoutePlanner.Plan(start,dir,P(3,-5,8),P(3,5,8),.4,.01,.2);
            Check(bentStraight.Length == 3, "Collinear plan retains bend when avoiding another circuit");
            Reject(() => CondensateRoutePlanner.Plan(start,dir,a,b,.4,.01,double.NaN), "无效", "Nonfinite detour rejected");
            System.Console.WriteLine(count + " geometry checks passed. Revit fitting and rollback tests are NOT_RUN.");
        }
    }
}
'@
Add-Type -TypeDefinition ($planner + [Environment]::NewLine + $checks) -ReferencedAssemblies 'WindowsBase', 'PresentationCore'
[FCUAutoDesign.CondensateRouteChecks]::Run()
