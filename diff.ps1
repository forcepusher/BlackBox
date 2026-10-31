param([int]$Round = 1)
# How far Sloperator's drawing of each SVG is from Edge's: share of pixels that differ.
Add-Type -ReferencedAssemblies System.Drawing @"
using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Diff { public static string Of(string a, string b) {
  var x = new Bitmap(a); var y = new Bitmap(b); if (x.Width != y.Width || x.Height != y.Height) return "size differs";
  var r = new Rectangle(0,0,x.Width,x.Height);
  var dx = x.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb); var dy = y.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
  var bx = new byte[dx.Stride*x.Height]; var by = new byte[dy.Stride*y.Height]; Marshal.Copy(dx.Scan0,bx,0,bx.Length); Marshal.Copy(dy.Scan0,by,0,by.Length);
  x.UnlockBits(dx); y.UnlockBits(dy); long n=0, big=0;
  for (int j=0;j<x.Height;j++) for (int i=0;i<x.Width;i++){ int k=j*dx.Stride+i*4; int d=System.Math.Abs(bx[k]-by[k])+System.Math.Abs(bx[k+1]-by[k+1])+System.Math.Abs(bx[k+2]-by[k+2]); if(d>30) n++; if(d>150) big++; }
  x.Dispose(); y.Dispose(); return string.Format("{0:P2} of pixels differ, {1} strongly", (double)n/(r.Width*r.Height), big); } }
"@
$o = Join-Path (Split-Path $MyInvocation.MyCommand.Path) out
foreach ($a in 'A','B','C','D') { $p1 = Join-Path $o ("{0}-svg-r{1}.png" -f $a, $Round); $p2 = Join-Path $o ("{0}-svg-r{1}.edge.png" -f $a, $Round); (Split-Path $p1 -Leaf) + ": " + [Diff]::Of($p1, $p2) }
