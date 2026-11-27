Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public class W { public delegate bool CB(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(CB cb, IntPtr l);
[DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
[DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, CB cb, IntPtr l);
[DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,IntPtr l);
public static IntPtr Find(IntPtr top, string txt){ IntPtr f=IntPtr.Zero; EnumChildWindows(top,(k,m)=>{ var t=new StringBuilder(256); GetWindowText(k,t,256); if(t.ToString()==txt && IsWindowVisible(k)){ f=k; return false;} return true;}, IntPtr.Zero); return f; }
public static void Click(uint pid, string btn){ EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h,out p); if(p==pid && IsWindowVisible(h)){ IntPtr b=Find(h,btn); if(b!=IntPtr.Zero){ if(btn=="&Use Outlook without an email account"){ SendMessage(b,0xF1,(IntPtr)1,IntPtr.Zero);} else if(btn=="N&o"){ IntPtr y=Find(h,"&Yes"); if(y!=IntPtr.Zero) SendMessage(y,0xF1,IntPtr.Zero,IntPtr.Zero); SendMessage(b,0xF1,(IntPtr)1,IntPtr.Zero);} else { SendMessage(b,0xF5,IntPtr.Zero,IntPtr.Zero);} } } return true;}, IntPtr.Zero);}
public static List<string> L(uint pid){ var r=new List<string>(); EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h,out p); if(p==pid){ var s=new StringBuilder(256); var c=new StringBuilder(256); GetWindowText(h,s,256); GetClassName(h,c,256); r.Add(c+" | "+s+" | vis="+IsWindowVisible(h)); if(IsWindowVisible(h)) EnumChildWindows(h,(k,m)=>{ var t=new StringBuilder(512); GetWindowText(k,t,512); if(t.Length>0) r.Add("   child: "+t); return true;}, IntPtr.Zero); } return true;}, IntPtr.Zero); return r; } }
"@
$out="C:\otest.txt"; "" | Out-File $out
if(Test-Path C:\click.txt){ foreach($b in (Get-Content C:\click.txt)){ Get-Process OUTLOOK | % { [W]::Click($_.Id,$b) }; Start-Sleep 4 }; Start-Sleep 6 }
Get-Process OUTLOOK | % { [W]::L($_.Id) } | Out-File $out -Append
