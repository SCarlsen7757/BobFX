// Inspect the DLL copied from the verified runtime image, without loading its dependencies.
using System.Diagnostics;
using System.Reflection;

if (args.Length != 2) throw new ArgumentException("Usage: AssemblyVersion.cs DLL VERSION");
var expected = Version.Parse(args[1]);
var assembly = AssemblyName.GetAssemblyName(args[0]).Version;
var product = FileVersionInfo.GetVersionInfo(args[0]).ProductVersion;
if (assembly != new Version(expected.Major, expected.Minor, expected.Build, 0) ||
    product?.Split('+')[0] != args[1])
    throw new InvalidOperationException($"Version mismatch: assembly={assembly}, informational={product}, expected={args[1]}");
Console.WriteLine($"Assembly={assembly}; InformationalVersion={product}");
