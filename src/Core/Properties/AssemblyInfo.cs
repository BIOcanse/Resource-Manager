using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resource Manager-APP.Tests")]
[assembly: InternalsVisibleTo("ResourceManager.FreedomPointCheck")]
[assembly: InternalsVisibleTo("ResourceManager.FrontendPreferencesBuild")]
#if RM_STATE_NAMES
[assembly: InternalsVisibleTo("ResourceManager.ControlDecisionAcceptance")]
#endif
