using System.Resources;
using System.Runtime.CompilerServices;
using System.Windows;

[assembly: NeutralResourcesLanguage("en")]

// tools/HangProbe drives the real window through the operations that used to block
// the UI thread; it needs the internal entry points those operations sit behind.
[assembly: InternalsVisibleTo("HangProbe")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
