using System;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

/* The pad by day: the game's fade passes 1, the tint lands on exactly black. */
Check(GalaxyBlackout.CanClear(true, 0f, 0f, 0f, true, true, 0f, 0f, 0f), "a black tint over a black solid clear is cleared");

Check(!GalaxyBlackout.CanClear(true, 0f, 0f, 0f, true, true, 1f, 1f, 1f), "full stars render");
Check(!GalaxyBlackout.CanClear(true, 0f, 0f, 0f, true, true, 0.001f, 0.001f, 0.001f), "a faint tint still renders, since a bright texel could round up");
Check(!GalaxyBlackout.CanClear(true, 0f, 0f, 0f, true, true, 0f, 0f, 0.002f), "one channel above zero renders");
Check(!GalaxyBlackout.CanClear(true, 0f, 0f, 0f, true, true, float.NaN, 0f, 0f), "an unreadable tint renders");
Check(!GalaxyBlackout.CanClear(true, 0f, 0f, 0f, true, false, 0f, 0f, 0f), "a tint not read this frame renders");
Check(!GalaxyBlackout.CanClear(false, 0f, 0f, 0f, true, true, 0f, 0f, 0f), "a camera that does not clear to a colour renders");
Check(!GalaxyBlackout.CanClear(true, 0.1f, 0f, 0f, true, true, 0f, 0f, 0f), "a coloured background renders, since a black cube would cover it");
Check(!GalaxyBlackout.CanClear(true, 0f, 0f, 0f, false, true, 0f, 0f, 0f), "anything else on the galaxy layer renders");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
