Build environment setup (Phoenix Point mod projects)
======================================================

Symptom
-------
    CSC : error CS0006: Metadata file 'C:\Program Files (x86)\Reference
    Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll' could
    not be found

This happens after a clean Windows/Visual Studio (re)install because Visual
Studio's core install no longer includes .NET Framework reference
assemblies by default -- they're a separate, optional component.

What's actually needed
-----------------------
These mod projects target .NET Framework 4.7.2 (to match the game's
Mono/Unity runtime), but build.cmd overrides FrameworkPathOverride to the
.NET Framework 4.8 reference assemblies folder instead (4.8 is a superset
and binary-compatible, and the 4.8 pack is easier to obtain/keep current
than 4.7.2's). So what you need installed is the *4.8* targeting pack,
not 4.7.2.

Fix -- option A (Visual Studio Installer, recommended)
-------------------------------------------------------
1. Start Menu -> "Visual Studio Installer" -> open it.
2. Find your VS2022 install (Community/Professional/etc.) -> "Modify".
3. Go to the "Individual components" tab.
4. Search "targeting pack" and check:
       .NET Framework 4.8 targeting pack
   (also fine to grab 4.7.2's if you want it for other projects.)
5. Click "Modify" and let it install.

Fix -- option B (standalone installer)
----------------------------------------
1. Download the ".NET Framework 4.8 Developer Pack" from Microsoft:
       https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48
   (the "Developer Pack", not just the runtime -- the runtime alone does
   not include the reference assemblies used at compile time.)
2. Run the installer, reboot if it asks.

Verify
------
After either fix, this folder should exist and contain mscorlib.dll,
System.dll, System.Core.dll, etc.:

    C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\

Then re-run the build from the relevant mod's solution root, e.g.:

    cd HavenUnderAttackTimer
    ./build

Other prerequisites (already fine on this machine, listed for completeness
in case of a future full reinstall)
----------------------------------------------------------------------------
- Visual Studio 2022 (any edition) with the ".NET desktop development"
  workload, which provides MSBuild at:
      C:\Program Files\Microsoft Visual Studio\2022\<Edition>\MSBuild\Current\Bin\MSBuild.exe
  build.cmd hardcodes the Community path above -- if you install a
  different edition (Professional/Enterprise), edit MSBUILD in build.cmd
  to match.
