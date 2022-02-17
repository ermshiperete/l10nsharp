## Overview

L10NSharp is a .NET localization library for Windows Forms applications. It collects strings which
need localization when your application first runs and saves them in a translation memory file. It
can also dynamically collect strings at runtime.

L10NSharp works with either TMX or XLIFF files as translation memory. Which one gets used can be
selected when creating a `LocalizationManager`.

## How to use

L10NSharp is provided as a [nuget package](https://www.nuget.org/packages/L10NSharp).

To use L10NSharp in your application, simply call the `Create` method on `LocalizationManager`, 
passing the location of the translation memory files and some other information:

```csharp
using (var lm = LocalizationManager.Create(TranslationMemory.XLiff, lang, "SampleApp",
    "SampleApp", productVersion, directoryOfInstalledXliffFiles, "MyCompany/L10NSharpSample",
    icon, "sample@example.com", "SampleApp")
{
    // existing code to run the application
}
```

One of two conventions must be used to name xlf files; this enables lazy loading so that
only the xlf files for languages that are actually in use are loaded into memory.
By default, directoryOfInstalledFiles contains files named Whatever.lang.xlf; if
LocalizationManager.UseLanguageCodeFolders is true, then they are in folders whose names
are the language tags. These names must match the target-language declared in the XLF
for lazy loading to work properly. If the target-language is a multi-part tag (like es-ES),
the lang component in the file path may be either the full tag (Whatever.es-ES.xlf or
es-ES/Whatever.xlf) or its first component, the actual language tag (Whatever.es.xlf
or es/Whatever.xlf).

## L10NSharpExtender

To localize a Windows Forms form or control, simply add the `L10NSharpExtender`. It will
automatically collect all the localizable strings on your form or control and its children.

## Localizing Within the Application

L10NSharp provides a dialog for translating terms while running the application. The dialog can be
launched by Alt-Shift-clicking a Windows Forms element.

## Upgrading to a newer version

The [migration](https://github.com/sillsdev/l10nsharp/wiki/Migration) guide describes the 
necessary changes when upgrading to a higher major version.

## Building

Just download the repository and build the solution (`L10NSharp.sln`).

The command line build command would look something like this:

    msbuild /t:Build /p:Configuration=Debug build/L10NSharp.proj

Note that on Linux building L10NSharp requires at least version 5 of Mono, which `mono5-sil` provides.
The Mono 5 version that the Mono project provides also works (if mono is at least version 5.16).

## Running Unit Tests

We use NUnit to run our unit tests. NUnit is downloaded via NuGet.  There may be a few tests that
do not run, but all tests that run should pass.

The tests can be run from the command line like this:

    msbuild /t:Test build/L10NSharp.proj

It is also possible to run the tests from inside Visual Studio, Rider or MonoDevelop (if `mono5-sil`
is installed and made the default Mono runtime).

## Testing in client projects (as applicable):

  * Set an enviroment variable `LOCAL_NUGET_REPO` with the path to a folder on your computer (or local network) to publish locally-built packages
  * See [these instructions](https://docs.microsoft.com/en-us/nuget/hosting-packages/local-feeds) to enable local package sources
  * `build /t:pack` will pack nuget packages and publish them to `LOCAL_NUGET_REPO`

Further instructions at https://github.com/sillsdev/libpalaso/wiki/Developing-with-locally-modified-nuget-packages

## Working on UI related files

The L10NSharp project uses SDK style .csproj files which don't allow the Designer to be used in
Visual Studio 2017+. There is a `src\L10NSharp\L10NSharp-Designer.csproj` file which uses the old
.csproj style and thus allows the programmer to edit the files with the Designer in Visual
Studio 2017.
