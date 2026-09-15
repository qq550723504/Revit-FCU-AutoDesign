using System;
using System.Windows.Media.Media3D;

namespace FCUAutoDesign
{
    internal static class SegmentClearance
    {
        public static double Distance(Point3D a, Point3D b, Point3D c, Point3D d)
        {
            Vector3D u = b-a, v = d-c, w = a-c;
            double aa=u.LengthSquared, bb=Vector3D.DotProduct(u,v), cc=v.LengthSquared;
            double dd=Vector3D.DotProduct(u,w), ee=Vector3D.DotProduct(v,w);
            double best=Math.Min(Math.Min(PointDistance(a,c,d),PointDistance(b,c,d)),
                Math.Min(PointDistance(c,a,b),PointDistance(d,a,b)));
            double denominator=aa*cc-bb*bb;
            if (denominator > 1e-12*aa*cc)
            {
                double s=(bb*ee-cc*dd)/denominator, t=(aa*ee-bb*dd)/denominator;
                if(s>=0 && s<=1 && t>=0 && t<=1) best=Math.Min(best,(w+s*u-t*v).Length);
            }
            return best;
        }
        private static double PointDistance(Point3D p, Point3D a, Point3D b)
        {
            Vector3D axis=b-a;
            if(axis.LengthSquared==0) return (p-a).Length;
            double t=Math.Max(0,Math.Min(1,Vector3D.DotProduct(p-a,axis)/axis.LengthSquared));
            return (p-(a+t*axis)).Length;
        }
    }
}
