#define WIN32_LEAN_AND_MEAN
#include <windows.h>
// COFF's standard load-config symbol makes initial static DLL resolution use
// System32, before the process entry point can set dynamic search policy.
// build.py verifies the resulting PE directory and field, not just this source.
extern "C" __attribute__((used)) const IMAGE_LOAD_CONFIG_DIRECTORY64 _load_config_used=[]() constexpr {
 IMAGE_LOAD_CONFIG_DIRECTORY64 c{};c.Size=sizeof(c);c.DependentLoadFlags=LOAD_LIBRARY_SEARCH_SYSTEM32;return c;
}();
