// Unit test for SidecarArgs, the sidecar's launch command line.
//
// Exit code 0 = pass, 1 = fail.

using System;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

{
    var a = SidecarArgs.Build("/tmp/rings", "0.0.0.0:8088", 1024, 576, 0, false, false);
    Check(a == "--shm-dir \"/tmp/rings\" --http-bind 0.0.0.0:8088 --max-width 1024 --max-height 576",
        "defaults pass only the ring dir, bind and size: " + a);
}
{
    var a = SidecarArgs.Build("/tmp/rings", "0.0.0.0:8088", 1024, 576, 2500000, false, false);
    Check(a.EndsWith(" --bitrate-bps 2500000"), "an explicit bitrate is forwarded");
}
{
    var a = SidecarArgs.Build("/tmp/rings", "0.0.0.0:8088", 1024, 576, 0, true, false);
    Check(a.EndsWith(" --manual-tier-switch"), "the manual tier test switch is forwarded when on");
    Check(!SidecarArgs.Build("/tmp/rings", "0.0.0.0:8088", 1024, 576, 0, false, false).Contains("--manual-tier-switch"),
        "and not when off");
}
{
    var a = SidecarArgs.Build("C:\\KSP\\GameData\\Kerbcast\\rings", "127.0.0.1:9000", 640, 360, 0, false, false);
    Check(a.StartsWith("--shm-dir \"C:\\KSP\\GameData\\Kerbcast\\rings\" "), "a path with backslashes stays quoted whole");
}

{
    var a = SidecarArgs.Build("/tmp/rings", "0.0.0.0:8088", 1024, 576, 0, false, true);
    Check(a.EndsWith(" --lighter-streams-for-slow-viewers"), "lighter streams for slow viewers is forwarded when on");
    Check(!a.Contains("--manual-tier-switch"), "independently of the test switch");
    Check(!SidecarArgs.Build("/tmp/rings", "0.0.0.0:8088", 1024, 576, 0, false, false).Contains("--lighter-streams"),
        "and not when off");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
