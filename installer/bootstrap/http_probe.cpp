// Test-only: pinned official vendor bytes; never executes the result.
#include "core.hpp"
#include <iostream>
using namespace yahad;
int wmain(int argc,wchar_t** argv){
 if(argc!=2)return 2;CoInitializeEx(nullptr,COINIT_MULTITHREADED);std::atomic_bool cancel=false;
 Pin p{L"https://ziglang.org/download/0.15.2/zig-x86_64-windows-0.15.2.zip",92614574,"3a0ed1e8799a2f8ce2a6e6290a9ff22e6906f8227865911fb7ddedc3cc14cb0c",L"vendor-test-only"};
 try{
  bool cancelledCleanly=false;try{HttpReader reader(p,cancel);auto bytes=download(p,reader,argv[1],cancel,[&](Progress pr){if(pr.phase==Progress::Downloading&&pr.current>=65536)cancel=true;});}catch(const Failure& e){cancelledCleanly=std::string(e.what())=="CANCELLED";}
  require(cancelledCleanly,"TEST_CANCEL");cancel=false;uint64_t observed=0;HttpReader reader(p,cancel);auto bytes=download(p,reader,argv[1],cancel,[&](Progress pr){if(pr.phase==Progress::Downloading)observed=pr.current;});require(bytes->verified&&observed==p.bytes,"TEST_VERIFY");
  std::cout<<"{\"nativeHttpsVerified\":true,\"cancelAndRetry\":true,\"bytes\":"<<observed<<",\"source\":\"official-zig-0.15.2\",\"downloadExecuted\":false,\"actualProductHostingTested\":false}\n";
 }catch(const Failure& e){std::cout<<"{\"nativeHttpsVerified\":false,\"error\":\""<<e.what()<<"\",\"os\":"<<e.os<<"}\n";return 1;}CoUninitialize();return 0;
}
