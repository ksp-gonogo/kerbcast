// Unit test for IntegrationPolicy: the Unity-free format-lever aggregation
// behind the visual-mod integration host. Contract: MSAA is forced off iff
// at least one AVAILABLE integration requires it; an unavailable integration
// never moves the lever. Exit code 0 = pass, 1 = fail.

using System;
using System.Collections.Generic;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

// No integrations: lever stays off-the-off (MSAA allowed).
Check(!IntegrationPolicy.ForceNoMsaa(new IntegrationFact[0]),
    "empty set does not force MSAA off");

// An available integration that does not care: lever unmoved.
Check(!IntegrationPolicy.ForceNoMsaa(new[] {
        new IntegrationFact("tufx", available: true, forcesNoMsaa: false) }),
    "available, non-forcing integration does not force MSAA off");

// An UNavailable integration that would force it: ignored.
Check(!IntegrationPolicy.ForceNoMsaa(new[] {
        new IntegrationFact("scatterer", available: false, forcesNoMsaa: true) }),
    "unavailable forcing integration is ignored");

// An available integration that forces it: lever on.
Check(IntegrationPolicy.ForceNoMsaa(new[] {
        new IntegrationFact("scatterer", available: true, forcesNoMsaa: true) }),
    "available forcing integration forces MSAA off");

// Mixed set: one available-and-forcing is enough.
Check(IntegrationPolicy.ForceNoMsaa(new[] {
        new IntegrationFact("tufx", true, false),
        new IntegrationFact("scatterer", true, true),
        new IntegrationFact("eve", false, true) }),
    "any available forcing integration wins in a mixed set");

if (failures == 0) { Console.WriteLine("ALL PASS"); return 0; }
Console.Error.WriteLine($"{failures} FAILURE(S)"); return 1;
