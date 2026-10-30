#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <initializer_list>
int WINAPI wWinMain(HINSTANCE,HINSTANCE,LPWSTR,int){
 wchar_t b[1024];for(auto key:{L"NODE_OPTIONS",L"DOTNET_STARTUP_HOOKS",L"DOTNET_ADDITIONAL_DEPS",L"PYTHONPATH"})if(GetEnvironmentVariableW(key,b,1024))return 3;
 auto h=CreateFileW(L"clean.txt",GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(h==INVALID_HANDLE_VALUE)return 4;DWORD n;bool ok=WriteFile(h,"ok",2,&n,nullptr)&&n==2;CloseHandle(h);return ok?0:5;
}
