# Skin-flatten metrics computed from the rendered PNGs of GeneralizationTest (token: flat).
# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less script files as ANSI.
#
# Usage:
#   powershell -File Tools\SkinFlat\metrics.ps1 -Dir <NormalTestOut\gen6_..._flat> -Char Ch07 `
#       -Boxes "cheekL:skin:239,432,309,502;browL:feat:239,298,321,321"
#   Box = name:kind:x0,y0,x1,y1 in pixels of the 700x700 FRONT render (top-left origin). Boxes are placed by hand on
#   the 'copy' render so they do not depend on the algorithm's own mask.
#
# What it reports (before = albedocopy render, after = albedoflat render, both without outline):
#   - copy vs original render difference (is the unprocessed copy a fair stand-in for the imported texture?)
#   - changed pixels per mask class: black = non-face mesh, blue = face mesh but not a target,
#     sky = weight ramp 0..0.5 (blended by weight), red = flattened, green = kept, yellow = target but skipped
#   - CV  : coefficient of variation of gray(render)/gray(clay render). The clay render carries only the toon bands,
#           so the ratio isolates the texture. Lower = flatter skin.
#   - grad: mean gradient magnitude of gray (central difference). For feature boxes it is measured only on target
#           pixels, so eyeballs (separate mesh) do not inflate "preservation".
#   The whole-target numbers use the geometry-defined target (face weight and skin shell), not the algorithm's choice.
#   The 'flattened set' numbers are self-selected and are printed for reference only.
param([string]$Dir, [string]$Char, [string]$Boxes = "")
Add-Type -AssemblyName System.Drawing
if (-not ("SkinFlatMetrics" -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class SkinFlatMetrics {
  static byte[] Load(string p, out int W, out int H) {
    using (Bitmap t = new Bitmap(p)) { W = t.Width; H = t.Height;
      BitmapData bd = t.LockBits(new Rectangle(0,0,W,H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb); byte[] raw = new byte[bd.Stride*H]; Marshal.Copy(bd.Scan0, raw, 0, raw.Length); t.UnlockBits(bd);
      byte[] px = new byte[W*H*3]; for (int y=0;y<H;y++) for (int x=0;x<W;x++) { int a=y*bd.Stride+x*4, b=(y*W+x)*3; px[b]=raw[a+2]; px[b+1]=raw[a+1]; px[b+2]=raw[a]; } return px; } }
  static float G(byte[] p, int i) { return (p[i*3] + p[i*3+1] + p[i*3+2]) / 3f; }
  static void Stat(byte[] img, byte[] clay, bool[] set, int W, int H, out double cv, out double grad, out int n) {
    double s=0, s2=0, e=0; n=0; int ne=0;
    for (int y=1;y<H-1;y++) for (int x=1;x<W-1;x++) { int i=y*W+x; if (!set[i]) continue; float c=G(clay,i); if (c<=8) continue;
      double r = G(img,i)/c; s+=r; s2+=r*r; n++;
      if (set[i-1]&&set[i+1]&&set[i-W]&&set[i+W]) { double gx=G(img,i+1)-G(img,i-1), gy=G(img,i+W)-G(img,i-W); e+=Math.Sqrt(gx*gx+gy*gy)*0.5; ne++; } }
    double m = n>0? s/n : 0; cv = (n>0 && m>1e-6)? Math.Sqrt(Math.Max(0, s2/n - m*m))/m : 0; grad = ne>0? e/ne : 0; }
  public static string Run(string dir, string c, string view, string boxes) {
    int W,H; string pre = System.IO.Path.Combine(dir, c + "_");
    byte[] orig = Load(pre+"albedo_mix1.0_noline_"+view+".png", out W, out H), copy = Load(pre+"albedocopy_mix1.0_noline_"+view+".png", out W, out H),
           flat = Load(pre+"albedoflat_mix1.0_noline_"+view+".png", out W, out H), clay = Load(pre+"clay_mix1.0_noline_"+view+".png", out W, out H), mask = Load(pre+"flatmask_"+view+".png", out W, out H);
    int N=W*H; bool[] T=new bool[N], S=new bool[N], K=new bool[N];
    int fg=0, d3=0, d3max=0; int[] cnt=new int[7], chg=new int[7], mx=new int[7]; // 0 non-face mesh,1 blue,2 sky,3 red,4 green,5 yellow,6 background
    for (int i=0;i<N;i++) { int r=mask[i*3], g=mask[i*3+1], b=mask[i*3+2];
      int cls = (b>128) ? (g>64 ? 2 : 1) : (r>128 ? (g>128 ? 5 : 3) : (g>128 ? 4 : 0));
      if (cls==0 && G(clay,i)<=8) cls = 6;   // black in the mask AND black in the clay render = empty background, not a non-face mesh (the clay render is bright on every mesh)
      if (cls>=3 && cls<=5) T[i]=true; if (cls==3) S[i]=true; if (cls==4) K[i]=true;
      if (G(orig,i)>8) { fg++; int d=(int)Math.Abs(G(orig,i)-G(copy,i)); if(d>d3max)d3max=d; if(d>4)d3++; }
      int dd = Math.Max(Math.Abs(copy[i*3]-flat[i*3]), Math.Max(Math.Abs(copy[i*3+1]-flat[i*3+1]), Math.Abs(copy[i*3+2]-flat[i*3+2])));
      cnt[cls]++; if (dd>2) chg[cls]++; if (dd>mx[cls]) mx[cls]=dd; }
    var sb = new System.Text.StringBuilder();
    sb.AppendFormat("{0} {1}: copy-vs-original maxdiff {2}, >4 in {3:F2}% of foreground\n", c, view, d3max, fg>0?100.0*d3/fg:0);
    string[] nm = { "non-face mesh", "blue(not target)", "sky(weight ramp)", "red(flattened)", "green(kept)", "yellow(skipped)", "background" };
    for (int k=0;k<6;k++) if (cnt[k]>0) sb.AppendFormat("  {0,-18} px {1,7}  changed(>2) {2,7} ({3,5:F1}%)  max diff {4}\n", nm[k], cnt[k], chg[k], 100.0*chg[k]/cnt[k], mx[k]);
    if (cnt[0]==0) sb.Append("  non-face mesh      px       0  (nothing to check in this view)\n");
    double cv0,g0,cv1,g1; int n; int nT=cnt[3]+cnt[4]+cnt[5];
    Stat(copy,clay,T,W,H,out cv0,out g0,out n); Stat(flat,clay,T,W,H,out cv1,out g1,out n);
    sb.AppendFormat("  whole target (geometry-defined, {0} px): CV {1:F3} -> {2:F3} (x{3:F2}), grad {4:F2} -> {5:F2} (x{6:F2})\n", nT, cv0, cv1, cv0>0?cv1/cv0:0, g0, g1, g0>0?g1/g0:0);
    Stat(copy,clay,S,W,H,out cv0,out g0,out n); Stat(flat,clay,S,W,H,out cv1,out g1,out n);
    sb.AppendFormat("  flattened set (self-selected, reference only): CV {0:F3} -> {1:F3}, grad {2:F2} -> {3:F2}\n", cv0, cv1, g0, g1);
    if (!string.IsNullOrEmpty(boxes)) foreach (string bx in boxes.Split(';')) { if (bx.Trim().Length==0) continue; string[] p = bx.Split(':'); string[] q = p[2].Split(',');
      if (p.Length<3 || q.Length<4) { sb.AppendFormat("  box '{0}': bad format, expected name:kind:x0,y0,x1,y1\n", bx); continue; }
      int x0=Math.Max(0,int.Parse(q[0])), y0=Math.Max(0,int.Parse(q[1])), x1=Math.Min(W,int.Parse(q[2])), y1=Math.Min(H,int.Parse(q[3])); bool[] B=new bool[N]; int inT=0, all=0;   // x1,y1 exclusive
      for (int y=y0;y<y1;y++) for (int x=x0;x<x1;x++) { all++; if (T[y*W+x]) { B[y*W+x]=true; inT++; } }
      Stat(copy,clay,B,W,H,out cv0,out g0,out n); Stat(flat,clay,B,W,H,out cv1,out g1,out n);
      sb.AppendFormat("  box {0,-9} ({1}, {2}/{3} px on target): CV {4:F3} -> {5:F3} (x{6:F2}), grad {7:F2} -> {8:F2} (x{9:F2})\n", p[0], p[1], inT, all, cv0, cv1, cv0>0?cv1/cv0:0, g0, g1, g0>0?g1/g0:0); }
    return sb.ToString(); }
}
'@
}
[SkinFlatMetrics]::Run($Dir, $Char, "front", $Boxes)
[SkinFlatMetrics]::Run($Dir, $Char, "side", "")
