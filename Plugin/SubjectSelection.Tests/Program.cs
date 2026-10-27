using System;
using System.Collections.Generic;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

var log = new List<string>();
int changes = 0;

/* Stands in for the fix source, which resolves a chosen craft to itself. */
Action Resolver(SubjectSelection sel) => () =>
{
    changes++;
    if (sel.ChosenPersistentId != 0) sel.ResolvedPersistentId = sel.ChosenPersistentId;
};

var flight = SubjectSelection.ActiveVessel(log.Add);
flight.Changed = Resolver(flight);
Check(flight.FollowActive && flight.ChosenPersistentId == 0 && flight.WantedName == "", "flight default follows the active craft");

flight.Choose(42);
Check(!flight.FollowActive && flight.ChosenPersistentId == 42 && changes == 1, "choosing a craft stops following and announces it");
Check(log.Count == 1 && log[0].Contains("persistentId=42"), "the choice is logged");

flight.Merged(42, 7);
Check(flight.ChosenPersistentId == 7 && changes == 2, "a merge of the chosen craft follows it into the survivor");
flight.Merged(42, 7);
Check(flight.ChosenPersistentId == 7 && changes == 2, "KSP's second announcement of the same merge is a no-op");
flight.Merged(5, 5);
Check(changes == 2, "a merge into itself is ignored");

flight.Choose(0);
Check(flight.FollowActive && flight.ChosenPersistentId == 0 && changes == 3, "choosing 0 returns to following the active craft");
flight.ResolvedPersistentId = 9;
flight.Merged(9, 10);
Check(flight.ChosenPersistentId == 0 && changes == 3, "a merge never redirects a feed following the active craft");

var spaceCenter = SubjectSelection.Chosen("Relay One");
Check(!spaceCenter.FollowActive && spaceCenter.WantedName == "Relay One", "Space Center default watches a named craft");
spaceCenter.Changed = Resolver(spaceCenter);
spaceCenter.ResolvedPersistentId = 100;
spaceCenter.Merged(100, 200);
Check(spaceCenter.ChosenPersistentId == 200 && changes == 4, "a merge of the resolved default craft follows it, with no choice made");
spaceCenter.Merged(300, 400);
Check(spaceCenter.ChosenPersistentId == 200 && changes == 4, "a merge of some other craft is ignored");
spaceCenter.Choose(55);
spaceCenter.DropChoice();
Check(spaceCenter.ChosenPersistentId == 0 && !spaceCenter.FollowActive && changes == 5, "dropping a gone craft returns to the default quietly");
flight.Choose(3);
flight.DropChoice();
Check(flight.FollowActive, "dropping returns a flight feed to following the active craft");

var counted = SubjectSelection.ActiveVessel();
int v0 = counted.Version;
counted.Choose(8);
int v1 = counted.Version;
counted.ResolvedPersistentId = 8;
counted.Merged(8, 9);
int v2 = counted.Version;
counted.Merged(4, 5);
counted.DropChoice();
Check(v1 > v0 && v2 > v1 && counted.Version > v2, "every change of request, a quiet drop included, moves Version");
counted.ResolvedPersistentId = 12;
Check(counted.Version == v2 + 1, "resolving the request does not move Version");

/* A world model governs: truth must not resolve (and so must not drop) a
   craft the model knows but KSP no longer has. */
bool truthReads = false;
int truthResolves = 0;
var governed = SubjectSelection.Chosen("");
var gate = new TruthResolveGate(() => truthReads, () =>
{
    truthResolves++;
    /* What truth does with a craft KSP no longer has. */
    if (governed.ChosenPersistentId == 777) governed.DropChoice();
});
governed.Changed = gate.Changed;
governed.Choose(777);
Check(truthResolves == 0 && governed.ChosenPersistentId == 777 && gate.Pending,
    "under a world model, an aim at a craft only the model knows survives Choose");
gate.BeforeRead();
Check(truthResolves == 0 && governed.ChosenPersistentId == 777, "reading while the model still governs resolves nothing");
truthReads = true;
gate.BeforeRead();
Check(truthResolves == 1 && !gate.Pending, "once truth reads again, the deferred request is resolved once");
gate.BeforeRead();
Check(truthResolves == 1, "and only once");
governed.Choose(5);
Check(truthResolves == 2, "under truth a request resolves at once, as 1.11 did");
var always = new TruthResolveGate(null, () => truthResolves++);
always.Changed();
Check(truthResolves == 3, "with no world model wired, truth always resolves");

var unwired = SubjectSelection.ActiveVessel();
unwired.Choose(1);
unwired.Merged(1, 2);
Check(unwired.ChosenPersistentId == 2, "a selection with no source or log still tracks the request");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
