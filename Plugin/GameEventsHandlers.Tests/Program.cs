using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

/*
 * KSP's EventData.Add builds an EvtDelegate whose constructor calls
 * evt.Target.GetType(), so a static method handler throws a
 * NullReferenceException and aborts whatever Awake registered it. This scan
 * fails when any plugin source adds a GameEvents handler that the same file
 * declares static.
 */

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

var addCall = new Regex(@"GameEvents\.\w+\.Add\(\s*(?<name>[A-Za-z_]\w*)\s*\)");

List<string> StaticHandlers(string source)
{
    var found = new List<string>();
    foreach (Match m in addCall.Matches(source))
    {
        string name = m.Groups["name"].Value;
        var decl = new Regex(@"^\s*(?:(?:private|internal|public|protected)\s+)?static\s+[\w<>\[\],. ]+\s" + Regex.Escape(name) + @"\s*\(", RegexOptions.Multiline);
        if (decl.IsMatch(source) && !found.Contains(name)) found.Add(name);
    }
    return found;
}

Check(StaticHandlers("static class X {\n  static void E() { GameEvents.onA.Add(OnA); }\n  static void OnA(Vessel v) {}\n}").SequenceEqual(new[] { "OnA" }),
    "self-test: a static handler is caught");
Check(StaticHandlers("class X {\n  void E() { GameEvents.onA.Add(OnA); GameEvents.onB.Add(h.OnB); }\n  void OnA(Vessel v) {}\n  static void OnB(Vessel v) {}\n}").Count == 0,
    "self-test: instance handlers and member-access handlers pass");

string Here([CallerFilePath] string path = "") => path;
string plugin = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here()), ".."));
var dirs = new[] { "Kerbcast", "KerbcastKos", "Kerbcast.DevProvider" }.Select(d => Path.Combine(plugin, d)).Where(Directory.Exists).ToList();
Check(dirs.Count >= 2, $"plugin source found ({dirs.Count} folders)");

int files = 0, adds = 0;
foreach (var dir in dirs)
    foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
    {
        string src = File.ReadAllText(f);
        files++;
        adds += addCall.Matches(src).Count;
        foreach (var name in StaticHandlers(src))
            Check(false, $"{Path.GetRelativePath(plugin, f)} adds static GameEvents handler {name}");
    }
Check(adds > 20, $"scanned {adds} GameEvents.Add calls in {files} files");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
