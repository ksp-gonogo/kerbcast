using System.Reflection;

[assembly: AssemblyTitle("Kerbcast.DevProvider")]
[assembly: AssemblyVersion("1.0.0.0")]

/* Kerbcast.API first, so its types load before this assembly is scanned. */
[assembly: KSPAssemblyDependency("KerbcastAPI", 1, 0)]
