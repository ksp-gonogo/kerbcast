// Unit test for SettingsLayer: the key-application helpers behind
// KerbcamSettings' layered settings load. The contract under test:
// absent/empty keys keep the current value (which is what makes
// "shipped defaults file, then user override file on top" work),
// unparsable values warn and keep the current value, floats parse
// with InvariantCulture.
//
// Exit code 0 = pass, 1 = fail.

using System;
using System.Collections.Generic;
using Kerbcam;

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

// A settings file's flat key/value view: the same Func<string, string>
// shape as ConfigNode.GetValue (null for absent keys).
Func<string, string> Node(params (string key, string value)[] pairs)
{
    var d = new Dictionary<string, string>();
    foreach (var (key, value) in pairs) d[key] = value;
    return k => d.TryGetValue(k, out var v) ? v : null;
}

var warnings = new List<string>();
void Warn(string msg) => warnings.Add(msg);

// --- Single-file apply: present keys set, absent keys keep the default. ---
{
    warnings.Clear();
    int port = 8088; string bind = "127.0.0.1"; float fps = 30f; bool spawn = true;
    var node = Node(("Port", "9000"), ("BindAddress", " 0.0.0.0 "));
    SettingsLayer.ApplyInt(node, "Port", v => port = v, Warn);
    SettingsLayer.ApplyString(node, "BindAddress", v => bind = v);
    SettingsLayer.ApplyFloat(node, "MaxCaptureFps", v => fps = v, Warn);
    SettingsLayer.ApplyBool(node, "AutoSpawnSidecar", v => spawn = v, Warn);
    Check(port == 9000, "present int key applies");
    Check(bind == "0.0.0.0", "string value is trimmed");
    Check(fps == 30f, "absent float key keeps the compiled default");
    Check(spawn, "absent bool key keeps the compiled default");
    Check(warnings.Count == 0, "no warnings for valid + absent keys");
}

// --- Layering: user file over defaults file, key by key. ---
{
    // Mirrors KerbcamSettings.Load(): compiled defaults, then the shipped
    // defaults file, then the user override file, same helper each pass.
    int port = 8088, width = 1024, bitrate = 0;
    var defaultsFile = Node(("Port", "9000"), ("Width", "1280"));
    var userFile = Node(("Port", "7777"), ("BitrateBps", "4000000"));
    foreach (var file in new[] { defaultsFile, userFile })
    {
        SettingsLayer.ApplyInt(file, "Port", v => port = v, Warn);
        SettingsLayer.ApplyInt(file, "Width", v => width = v, Warn);
        SettingsLayer.ApplyInt(file, "BitrateBps", v => bitrate = v, Warn);
    }
    Check(port == 7777, "user file key overrides defaults file key");
    Check(width == 1280, "key absent from user file keeps defaults-file value");
    Check(bitrate == 4000000, "key only in user file applies over compiled default");
}

// --- Layering: empty user file (every key absent) changes nothing. ---
{
    int port = 8088;
    var defaultsFile = Node(("Port", "9000"));
    var emptyUserFile = Node();
    SettingsLayer.ApplyInt(defaultsFile, "Port", v => port = v, Warn);
    SettingsLayer.ApplyInt(emptyUserFile, "Port", v => port = v, Warn);
    Check(port == 9000, "empty user file leaves defaults-file values intact");
}

// --- Parse failures: warn, keep the current (already-layered) value. ---
{
    warnings.Clear();
    int port = 9000; bool spawn = true; float fps = 30f;
    var bad = Node(("Port", "not-a-port"), ("AutoSpawnSidecar", "yes"), ("MaxCaptureFps", "fast"));
    SettingsLayer.ApplyInt(bad, "Port", v => port = v, Warn);
    SettingsLayer.ApplyBool(bad, "AutoSpawnSidecar", v => spawn = v, Warn);
    SettingsLayer.ApplyFloat(bad, "MaxCaptureFps", v => fps = v, Warn);
    Check(port == 9000 && spawn && fps == 30f, "unparsable values keep current values");
    Check(warnings.Count == 3, $"each unparsable value warns once (got {warnings.Count})");
}

// --- Empty string value is treated as absent (ConfigNode quirk). ---
{
    warnings.Clear();
    int port = 9000;
    SettingsLayer.ApplyInt(Node(("Port", "")), "Port", v => port = v, Warn);
    Check(port == 9000 && warnings.Count == 0, "empty value keeps current value, no warning");
}

// --- Floats: InvariantCulture, '.' decimal separator only. ---
{
    warnings.Clear();
    float budget = 24f;
    SettingsLayer.ApplyFloat(Node(("B", "16.5")), "B", v => budget = v, Warn);
    Check(budget == 16.5f, "dot-decimal float parses");
    SettingsLayer.ApplyFloat(Node(("B", "12,5")), "B", v => budget = v, Warn);
    Check(budget == 16.5f && warnings.Count == 1, "comma-decimal float warns and keeps current value");
}

// --- Float3 (DebugWindDirection): three comma-separated floats. ---
{
    warnings.Clear();
    (float x, float y, float z) wind = (0f, 0f, 0f);
    SettingsLayer.ApplyFloat3(Node(("W", "100, 0, -5.5")), "W", (x, y, z) => wind = (x, y, z), Warn);
    Check(wind == (100f, 0f, -5.5f), "three floats parse with trimming");
    SettingsLayer.ApplyFloat3(Node(("W", "1, 2")), "W", (x, y, z) => wind = (x, y, z), Warn);
    Check(wind == (100f, 0f, -5.5f), "two components warn and keep current value");
    SettingsLayer.ApplyFloat3(Node(("W", "1, two, 3")), "W", (x, y, z) => wind = (x, y, z), Warn);
    Check(wind == (100f, 0f, -5.5f), "non-float component warns and keeps current value");
    Check(warnings.Count == 2, $"both bad float3 values warned (got {warnings.Count})");
}

// --- TryParseInt: presence-detecting parse for per-camera Width/Height. ---
{
    warnings.Clear();
    Check(SettingsLayer.TryParseInt(Node(("Width", " 640 ")), "Width", Warn) == 640, "TryParseInt parses with trimming");
    Check(SettingsLayer.TryParseInt(Node(), "Width", Warn) == null, "TryParseInt absent key -> null");
    Check(warnings.Count == 0, "absent key does not warn");
    Check(SettingsLayer.TryParseInt(Node(("Width", "wide")), "Width", Warn) == null, "TryParseInt garbage -> null");
    Check(warnings.Count == 1, "garbage value warns");
}

Console.WriteLine(failures == 0 ? "SettingsLayer.Tests: all passed" : $"SettingsLayer.Tests: {failures} FAILED");
return failures == 0 ? 0 : 1;
