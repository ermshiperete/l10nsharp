# Change Log

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/)
and this project adheres to [Semantic Versioning](http://semver.org/).

<!-- Available types of changes:
### Added
### Changed
### Fixed
### Deprecated
### Removed
### Security
-->

## [Unreleased]

### Added

- `LocalizationManager.Create` methods without `TranslationMemory kind` parameter

### Deprecated

- `LocalizationManager.Create` methods with `TranslationMemory kind` parameter

## [6.0.0] - 2022-11-21

### Changed

- Added cleanUpTmx parameter to LocalizationManager.DeleteOldTranslationFiles to allow for cleanup of old TMX files.

### Removed

- TMX-based localization no longer supported
- LocalizationManager.GetTranslationFileNameForLanguage is no longer public.

## [5.0.0] - 2022-07-08

### Added

- option `LocalizationManager.ThrowIfManagerDisposed` to not throw if LM disposed (BL-9904)
- XliffBody.TransUnitsUnordered for where you just need to enumerate all of them.
- (Made public) XliffBody.AddTransUnit and .RemoveTransUnit for where you need to modify.
- XliffBody.TransUnitsForXml. This is necessarily public to support (backwards-compatible)
    serialization and deserialization in XML, but is not intended for any other purpose.

### Changed

- Scanning resources for strings no longer rethrows unexpected exceptions.  It now writes the
  exception (and stack trace) using a (conditional) Console.WriteLine and a Debug.WriteLine.
  Rethrowing the exception leads to creating a zero-length xliff file which causes another
  exception.  Swallowing the exception allows the scanning process to continue and complete.
  The old behavior has been an endless source of periodic instability in using L10NSharp over
  the years.
- remove progress dialog when initializating Xliff localization managers (BL-11157)
- Made string retrieval operations on Xliff-based LocalizationManagers thread-safe
- Added ILocalizationManager parameter to StringsLocalizedHandler
- It's long been a convention that xliff file names are module.lang.xlf (e.g., Bloom.fr.xlf)
or else kept in language-code folders (.../en/Bloom.xlf) if UseLanguageCodeFolders is set.
With the latest changes, this is required: the language name indicated in these ways in the file
name must match the language declared in the target-language attribute, or at least match the
first element of the target-language (e.g., a file with target-languge es-ES may be stored in
file like Bloom.es.xlf or .../es/Bloom.xlf).
- Progress dialogs are no longer shown when initializing XLIFF-based LocalizationManagers.
- Made it possible for caller to specify the file extension of the "original" executable file
when constructing an XLIFF-based LocalizationManager.
- Changed the way the "original" attribute is set in XLIFF files. It used to be based on the
Name, but changed it to use Id instead.
- Added optional owner parameter to methods that show dialog boxes so that they can be
displayed centered on a parent window (and not appear off-screen).

### Removed

- XliffBody.TransUnits, as there is no good way to make this thread-safe for all the ways
    it could be used, such as adding items to the list. (See Added for replacements.)

## [4.1.0] - 2021-03-04

### Changed

- Add `ExtractXliff` tool as nuget package
- Add `CheckOrFixXliff` tool as nuget package
- Added version of LocalizationManager.Create to allow "custom" localization methods
- Added -m switch to ExtractXliff command-line to allow caller to pass additional string-localization methods

## [4.0.3] - 2020-01-21

### Changed

- Add build number to AssemblyFileVersion

## [4.0.2] - 2019-07-09

### Fixed

- If translator returns an unmodified source string, don't substitute the English language name for the vernacular name.

## [4.0.1] - 2019-07-08

### Added

- create symbol nuget package

### Fixed

- Find TMX files in `Generated` and `User Modified` directories

- Don't ask Bing translator to translate language names: https://github.com/sillsdev/l10nsharp/issues/66.
  Also don't display the name a second time in parentheses if English and native name are identical.

## [4.0.0] - 2019-05-16

### Changed

- Allow to select translation memory (TMX or XLIFF). This changed a few APIs.
  To create a `LocalizationManager` you now pass a `TranslationMemory` parameter
  (cf. [migration](https://github.com/sillsdev/l10nsharp/wiki/Migration) guide):

      LocalizationManager.Create(TranslationMemory.XLiff, lang, "SampleApp", "SampleApp",
        Application.ProductVersion, directoryOfInstalledXliffFiles, "MyCompany/L10NSharpSample",
        icon, "sample@example.com", "SampleApp");

- Nuget package is now called `L10NSharp` instead of `L10NSharp.xliff` or `L10NSharp.tmx`

## [3.1.1] - 2019-04-26

### Fixed

- Create .exe for `CheckOrFixXliff` and `ExtractXliff` instead of .dll

## [3.1.0] - 2019-04-16

### Changed

- Create nuget package
- Strong-name assembly
