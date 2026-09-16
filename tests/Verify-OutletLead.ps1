$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase, PresentationCore
$source = Get-Content (Join-Path $PSScriptRoot '..\Geometry\OutletLeadPlanner.cs') -Raw -Encoding UTF8
$routeSource = Get-Content (Join-Path $PSScriptRoot '..\Geometry\LowerFlipRoutePlanner.cs') -Raw -Encoding UTF8
$routeSource = [regex]::Replace($routeSource, '(?m)^using .*;\r?$', '')
$checks = @'
namespace FCUAutoDesign
{
    public static class OutletLeadChecks
    {
        private static int count;
        private static Point3D P(double x, double y, double z) { return new Point3D(x,y,z); }
        private static Point3D[] Box(double x1,double x2,double y1,double y2,double z1,double z2)
        {
            var result = new List<Point3D>();
            foreach(double x in new[]{x1,x2}) foreach(double y in new[]{y1,y2}) foreach(double z in new[]{z1,z2})
                result.Add(P(x,y,z));
            return result.ToArray();
        }
        private static void Check(bool ok, string name)
        { if (!ok) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        private static void Reject(Action f, string name)
        {
            try { f(); } catch(InvalidOperationException) { Check(true,name); return; }
            throw new Exception("FAIL: " + name);
        }
        // Independent segment/slab intersection checks the actual generated polyline against the obstacle.
        private static bool Hit(Point3D a,Point3D b,Point3D[] box)
        {
            double[] start={a.X,a.Y,a.Z}, end={b.X,b.Y,b.Z};
            double[] min={box.Min(p=>p.X)-.01,box.Min(p=>p.Y)-.01,box.Min(p=>p.Z)-.01};
            double[] max={box.Max(p=>p.X)+.01,box.Max(p=>p.Y)+.01,box.Max(p=>p.Z)+.01};
            double enter=0,exit=1;
            for(int axis=0;axis<3;axis++)
            {
                double d=end[axis]-start[axis];
                if(Math.Abs(d)<1e-10) { if(start[axis]<min[axis] || start[axis]>max[axis]) return false; }
                else
                {
                    double t1=(min[axis]-start[axis])/d,t2=(max[axis]-start[axis])/d;
                    enter=Math.Max(enter,Math.Min(t1,t2)); exit=Math.Min(exit,Math.Max(t1,t2));
                    if(enter>exit) return false;
                }
            }
            return true;
        }
        public static void Run()
        {
            var start=P(0,0,3); var dir=new Vector3D(1,0,0);
            // Synthetic coordinates reproducing the reported supply-drop / condensate-outlet collision.
            var obstacle=Box(.35,.45,-.02,.02,2.6,3.2);
            int blocked=0;
            foreach(int shift in new[]{0,-1,1,-2,2})
            for(int l=0;l<=8;l++) for(int d=0;d<=(shift==0?8:4);d++)
            {
                var prefix=LowerFlipRoutePlanner.Approach(start,dir,.4+l*.1,.15+d*.1,shift*.1,.001);
                if(Hit(prefix[0],prefix[1],obstacle)) blocked++;
            }
            Check(blocked==261,"All 261 old candidates collide before their first turn");
            var leads=OutletLeadPlanner.BeforeObstacles(start,dir,new[]{obstacle},.4,.01,.1,.001);
            Check(leads.Length==3 && leads.All(l=>l>0 && l<.35-.01),"New leads turn before the supply drop");
            foreach(double lead in leads)
            {
                var prefix=LowerFlipRoutePlanner.Approach(start,dir,lead,.15,0,.001);
                var route=LowerFlipRoutePlanner.Complete(prefix,P(lead,4,2),.001);
                bool hit=false; for(int i=1;i<route.Length;i++) hit|=Hit(route[i-1],route[i],obstacle);
                Check(!hit,"Complete early-turn route avoids supply drop; lead="+lead);
            }
            var elbowOnly=Box(.43,.47,-.02,.02,3.04,3.08);
            Check(!Hit(start,P(.4,0,3),elbowOnly)
                && OutletLeadPlanner.BeforeObstacles(start,dir,new[]{elbowOnly},.4,.01,.1,.001).Length>0,
                "Nearby first-elbow obstacle also produces earlier leads");
            Check(OutletLeadPlanner.BeforeObstacles(start,dir,new Point3D[0][],.4,.01,.1,.001).Length==0,"No obstacles preserves original candidate set");
            foreach(var clear in new[]{Box(-.5,-.3,-.02,.02,2.6,3.2),Box(.35,.45,1,2,2.6,3.2),
                Box(.35,.45,-.02,.02,4,5),Box(2,3,-.02,.02,2.6,3.2)})
                Check(OutletLeadPlanner.BeforeObstacles(start,dir,new[]{clear},.4,.01,.1,.001).Length==0,
                    "Obstacle behind, beside, above or beyond outlet is ignored");
            var near=Box(.25,.3,-.02,.02,2.6,3.2);
            var ordered=OutletLeadPlanner.BeforeObstacles(start,dir,new[]{obstacle,near},.4,.01,.1,.001);
            var reversed=OutletLeadPlanner.BeforeObstacles(start,dir,new[]{near,obstacle},.4,.01,.1,.001);
            Check(ordered.SequenceEqual(reversed) && ordered[0]<leads[0],"Nearest obstacle limits lead independent of enumeration order");
            Check(OutletLeadPlanner.BeforeObstacles(start,dir,new[]{Box(.05,.1,-.02,.02,2.6,3.2)},.4,.01,.1,.001).Length==0,
                "No positive room for turn does not invent zero or negative leads");
            var overlapping=Box(-.02,.1,-.1,.1,2.95,3.05);
            Check(OutletLeadPlanner.BeforeObstacles(start,dir,new[]{overlapping,obstacle},.4,.01,.1,.001).SequenceEqual(leads),
                "Origin-overlapping box does not erase other early-turn suggestions; final collision checks still required");
            foreach(double angle in new[]{Math.PI/2,.73,Math.PI})
            {
                Func<Point3D,Point3D> move=p=>P(100+p.X*Math.Cos(angle)-p.Y*Math.Sin(angle),
                    200+p.X*Math.Sin(angle)+p.Y*Math.Cos(angle),p.Z+30);
                var rotated=OutletLeadPlanner.BeforeObstacles(move(start),new Vector3D(Math.Cos(angle),Math.Sin(angle),0),
                    new[]{obstacle.Select(move).ToArray()},.4,.01,.1,.001);
                Check(rotated.Length==leads.Length && rotated.Zip(leads,(a,b)=>Math.Abs(a-b)<1e-8).All(x=>x),
                    "Translated/rotated obstacle corners preserve outlet distances: "+angle);
            }
            // Local diagnostic 20260915-150738, FCU 576003: translated to condensate origin,
            // mirrored X to the outward axis, units mm. No customer absolute coordinates retained.
            var actualLead=Box(0,362,-16.75,16.75,63.25,96.75);
            var actualElbow=Box(362,420,-20,20,42,100);
            var actualDrop=Box(383.25,416.75,-16.75,16.75,-32,42);
            var replay=OutletLeadPlanner.BeforeObstacles(P(0,0,0),dir,
                new[]{actualLead,actualElbow,actualDrop},400,10,100,1);
            Check(replay.SequenceEqual(new[]{252.0,189.0,126.0}),"Measured geometry reproduces logged 252/189/126 mm leads");
            Check(OutletLeadPlanner.BeforeObstacles(P(0,0,0),dir,new[]{actualLead,actualElbow,actualDrop},400,10,100,1,null)
                .SequenceEqual(replay),"Unknown installation minimum retains early-turn candidates");
            Check(OutletLeadPlanner.BeforeObstacles(P(0,0,0),dir,new[]{actualLead,actualElbow,actualDrop},400,10,100,1,200)
                .SequenceEqual(new[]{252.0}),"Explicit minimum removes candidates below net-length requirement");
            Check(OutletLeadPlanner.BeforeObstacles(P(0,0,0),dir,new[]{actualLead,actualElbow,actualDrop},400,10,100,1,300)
                .Length==0,"Hard minimum is not relaxed to escape an obstacle");
            Reject(()=>OutletLeadPlanner.BeforeObstacles(start,dir,new[]{obstacle},.4,.01,.1,.001,double.NaN),"Invalid optional minimum rejected");
            Check(OutletLeadPlanner.BeforeObstacles(P(0,0,0),dir,new[]{actualLead},400,10,100,1).Length==0,
                "Measured adjacent supply lead cannot alone propose a positive turn length");
            var measuredPrefix=LowerFlipRoutePlanner.Approach(P(0,0,0),dir,replay[0],150,0,1);
            var measuredRoute=LowerFlipRoutePlanner.Complete(measuredPrefix,P(252,1916.748,89.2),1);
            bool measuredHit=false;
            foreach(var box in new[]{actualLead,actualElbow,actualDrop})
                for(int i=1;i<measuredRoute.Length;i++) measuredHit|=Hit(measuredRoute[i-1],measuredRoute[i],box);
            Check(!measuredHit,"Measured early-turn polyline avoids the three local supply obstacle boxes");
            Reject(()=>OutletLeadPlanner.BeforeObstacles(start,dir,new[]{obstacle},double.NaN,.01,.1,.001),"NaN requested lead rejected");
            Reject(()=>OutletLeadPlanner.BeforeObstacles(start,dir,new[]{obstacle},.4,-.01,.1,.001),"Invalid radius rejected");
            Reject(()=>OutletLeadPlanner.BeforeObstacles(start,dir,new[]{new[]{P(0,0,0)}},.4,.01,.1,.001),"Incomplete obstacle box rejected");
            Reject(()=>OutletLeadPlanner.BeforeObstacles(start,new Vector3D(0,0,1),new[]{obstacle},.4,.01,.1,.001),"Vertical connector rejected");
            Console.WriteLine(count+" outlet obstacle checks passed. Reproduced 261 blocked old candidates. Revit fitting/collision/rollback acceptance: NOT_RUN.");
        }
    }
}
'@
Add-Type -TypeDefinition ($source + [Environment]::NewLine + $routeSource + [Environment]::NewLine + $checks) -ReferencedAssemblies 'WindowsBase', 'PresentationCore', 'System.Core'
[FCUAutoDesign.OutletLeadChecks]::Run()
