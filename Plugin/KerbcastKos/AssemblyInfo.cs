/* KSP's AssemblyLoader loads mod DLLs in filename order and calls GetTypes() on
   each as it goes. On a case-sensitive filesystem (Linux / the Deck)
   "KerbcastKos" sorts before "kOS", so without a declared dependency the addon
   is scanned before kOS is loaded and dropped with a ReflectionTypeLoadException
   (it never surfaces on a case-insensitive macOS/Windows dev box). These
   KSPAssemblyDependency attributes make KSP load kOS + kOS.Safe first, mirroring
   kOS's own declaration ([KSPAssembly("kOS", 1, 6, 0)] +
   [KSPAssemblyDependency("kOS.Safe", 0, 0)]). The attribute type lives in
   Assembly-CSharp, which this assembly already references. */

[assembly: KSPAssemblyDependency("kOS", 1, 6)]
[assembly: KSPAssemblyDependency("kOS.Safe", 0, 0)]

/* Kerbcast.API is a hard dependency: the addon has nothing to do without it. */
[assembly: KSPAssemblyDependency("KerbcastAPI", 1, 0)]
