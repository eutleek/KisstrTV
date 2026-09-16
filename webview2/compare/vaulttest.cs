using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

class VaultTest
{
    static Dictionary<string, object> AsDict(object o) { return o as Dictionary<string, object>; }

    static List<object> AsList(object o)
    {
        if (o == null) return null;
        List<object> l = o as List<object>;
        if (l != null) return l;
        if (o is string) return null;
        if (o is IDictionary) return null;
        IEnumerable en = o as IEnumerable;
        if (en != null) { List<object> r = new List<object>(); foreach (object x in en) r.Add(x); return r; }
        return null;
    }

    static object Get(object o, string k)
    {
        Dictionary<string, object> d = AsDict(o);
        if (d == null) return null;
        object v;
        return d.TryGetValue(k, out v) ? v : null;
    }

    static string Str(object o) { return o == null ? "" : Convert.ToString(o); }

    static void Try(string label, string path, int maxLen)
    {
        string txt = File.ReadAllText(path, Encoding.UTF8);
        JavaScriptSerializer js = new JavaScriptSerializer();
        if (maxLen > 0) js.MaxJsonLength = maxLen;

        Stopwatch sw = Stopwatch.StartNew();
        object o = null; string err = null;
        try { o = js.DeserializeObject(txt); }
        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
        sw.Stop();

        Console.WriteLine("[" + label + "]  MaxJsonLength=" + js.MaxJsonLength
            + "  chars=" + txt.Length + "  " + sw.ElapsedMilliseconds + "ms");

        if (err != null) { Console.WriteLine("    >>> FAILED: " + err); return; }

        List<object> arr = AsList(Get(o, "items"));
        if (arr == null) { Console.WriteLine("    >>> items NOT a list"); return; }

        int ok = 0;
        foreach (object it in arr)
        {
            Dictionary<string, object> d = AsDict(it);
            if (d != null && !string.IsNullOrEmpty(Str(Get(d, "bvid")))) ok++;
        }
        Console.WriteLine("    >>> OK  items=" + arr.Count + "  valid=" + ok);
    }

    static void Main(string[] args)
    {
        string cur = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KisstrTV", "vault.json");

        Console.WriteLine("=== real vault.json (5365 items) ===");
        Try("default 2MB", cur, 0);
        Try("raised", cur, int.MaxValue);

        if (args.Length > 0 && File.Exists(args[0]))
        {
            Console.WriteLine();
            Console.WriteLine("=== stress file (" + args[0] + ") ===");
            Try("default 2MB", args[0], 0);
            Try("raised", args[0], int.MaxValue);
        }
    }
}
