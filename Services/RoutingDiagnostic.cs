using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;

namespace FCUAutoDesign
{
    internal sealed class RoutingDiagnostic
    {
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        private readonly StringBuilder lines = new StringBuilder();
        public RoutingDiagnostic(int fcuId)
        {
            Add("FCU="+fcuId+"; UTC="+DateTime.UtcNow.ToString("o")
                +"; MVID="+typeof(RoutingDiagnostic).Assembly.ManifestModule.ModuleVersionId);
            Add("Coordinates and lengths: mm; directions: unit vectors. Candidate filters are not Revit acceptance.");
        }
        public void Add(string text) { lines.AppendLine(Clock.ElapsedMilliseconds+" ms | "+text); }
        public static string Point(XYZ p)
        {
            return string.Format(CultureInfo.InvariantCulture,"({0:F3},{1:F3},{2:F3})",
                p.X*304.8,p.Y*304.8,p.Z*304.8);
        }
        public string Finish(string summary)
        {
            Add(summary);
            try
            {
                string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FCUAutoDesign","Diagnostics");
                Directory.CreateDirectory(dir);
                string path=Path.Combine(dir,"route-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".txt");
                File.WriteAllText(path,lines.ToString(),Encoding.UTF8);
                return summary+Environment.NewLine+"诊断文件："+path;
            }
            catch(IOException ex) { return summary+"；诊断文件写入失败："+ex.Message; }
            catch(UnauthorizedAccessException ex) { return summary+"；诊断文件写入失败："+ex.Message; }
        }
    }
}
