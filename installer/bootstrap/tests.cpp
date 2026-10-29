#include "core.hpp"
#include <iostream>
using namespace yahad;
static int passed=0,failed=0;
static std::wstring parent;
void check(bool b){if(!b)throw std::runtime_error("assertion");}
template<class F>void test(const char* name,F f){try{f();++passed;std::cout<<"PASS "<<name<<"\n";}catch(const std::exception& e){++failed;std::cout<<"FAIL "<<name<<": "<<e.what()<<"\n";}}
template<class F>void rejects(const char* code,F f){try{f();}catch(const Failure& e){check(std::string(e.what())==code);return;}throw std::runtime_error("unexpected success");}
struct Memory:Reader{
 std::vector<unsigned char> bytes;size_t cursor=0,chunk=7;uint64_t header=0;std::function<void(size_t)> after;
 explicit Memory(std::string s):bytes(s.begin(),s.end()){}explicit Memory(std::vector<unsigned char> s):bytes(std::move(s)){}
 uint64_t length()const override{return header;}
 size_t read(unsigned char* b,size_t capacity)override{auto n=std::min({chunk,capacity,bytes.size()-cursor});std::copy_n(bytes.data()+cursor,n,b);cursor+=n;if(after)after(cursor);return n;}
};
std::string digest(const std::vector<unsigned char>& b){Sha h;h.add(b.data(),b.size());return h.finish();}
Pin pin(const std::string& s="abc"){std::vector<unsigned char>b(s.begin(),s.end());return {L"https://fixture.invalid/yahad-0.5.3.exe",b.size(),digest(b),L"0.5.3"};}
size_t leftovers(){WIN32_FIND_DATAW f{};auto h=FindFirstFileW((parent+L"\\Yahad.Download-*").c_str(),&f);if(h==INVALID_HANDLE_VALUE)return 0;size_t n=0;do{++n;}while(FindNextFileW(h,&f));FindClose(h);return n;}
std::vector<unsigned char> readFile(const std::wstring& p){Handle h(CreateFileW(p.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,0,nullptr));check((bool)h);LARGE_INTEGER n;check(GetFileSizeEx(h.h,&n)!=FALSE&&n.QuadPart<10000000);std::vector<unsigned char>b((size_t)n.QuadPart);DWORD got;check(ReadFile(h.h,b.data(),(DWORD)b.size(),&got,nullptr)!=FALSE&&got==b.size());return b;}
int wmain(int argc,wchar_t** argv){
 CoInitializeEx(nullptr,COINIT_MULTITHREADED);try{check(argc==3);parent=argv[1];auto pins=pinDirectories(parent);std::atomic_bool cancel=false;Notify ignore=[](Progress){};
 test("sha256-known-vector",[]{check(pin().sha=="ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");});
 test("https-pin",[]{check(parseUrl(pin().url).host==L"fixture.invalid");});
 test("url-without-path-or-query",[]{check(parseUrl(L"https://fixture.invalid").path==L"/");});
 test("redirect-exact-same-host",[]{check(allowedRedirect(L"downloads.example",L"downloads.example",2));});
 test("github-assets-exact-host",[]{check(allowedRedirect(L"github.com",L"release-assets.githubusercontent.com",0));});
 test("redirect-suffix-confusion-refused",[]{check(!allowedRedirect(L"github.com",L"release-assets.githubusercontent.com.evil.invalid",0));});
 test("unrelated-origin-cannot-use-github-exception",[]{check(!allowedRedirect(L"other.invalid",L"release-assets.githubusercontent.com",0));});
 test("redirect-count-bounded",[]{check(!allowedRedirect(L"github.com",L"github.com",3));});
 for(auto s:{L"http://fixture.invalid/a",L"https://u:p@fixture.invalid/a",L"https://fixture.invalid:444/a",L"file:///C:/x",L"https://fixture.invalid/a#x",L"https://fixture.invalid/\r\nX:y"})test("reject-unsafe-url",[&]{rejects("DOWNLOAD_URL",[&]{parseUrl(s);});});
 test("reject-invalid-hash",[]{auto p=pin();p.sha="bad";rejects("PAYLOAD_PIN",[&]{validatePin(p);});});
 test("reject-size-bound",[]{auto p=pin();p.bytes=2ULL*1024*1024*1024;rejects("PAYLOAD_PIN",[&]{validatePin(p);});});
 test("verified-file-without-length-header",[&]{Memory m("abc");auto d=download(pin(),m,parent,cancel,ignore);check(d->verified&&readFile(d->path)==m.bytes);});
 test("verified-exact-length-header",[&]{Memory m("abc");m.header=3;auto d=download(pin(),m,parent,cancel,ignore);check(d->verified);});
 test("header-mismatch-before-file",[&]{Memory m("abc");m.header=4;rejects("CONTENT_LENGTH",[&]{download(pin(),m,parent,cancel,ignore);});check(leftovers()==0);});
 test("truncated-cleans-private-file",[&]{Memory m("ab");rejects("DOWNLOAD_INCOMPLETE",[&]{download(pin(),m,parent,cancel,ignore);});check(leftovers()==0);});
 test("oversized-cleans-private-file",[&]{Memory m("abcd");rejects("DOWNLOAD_SIZE",[&]{download(pin(),m,parent,cancel,ignore);});check(leftovers()==0);});
 test("corrupt-cleans-private-file",[&]{Memory m("abd");rejects("DOWNLOAD_HASH",[&]{download(pin(),m,parent,cancel,ignore);});check(leftovers()==0);});
 test("cancel-before-download",[&]{cancel=true;Memory m("abc");rejects("CANCELLED",[&]{download(pin(),m,parent,cancel,ignore);});cancel=false;check(leftovers()==0);});
 test("cancel-wins-before-handoff",[]{std::atomic_bool c=false,h=false;std::mutex gate;check(requestCancellation(c,h,gate));rejects("CANCELLED",[&]{claimHandoff(c,h,gate);});check(!h);});
 test("handoff-owns-later-cancellation",[]{std::atomic_bool c=false,h=false;std::mutex gate;claimHandoff(c,h,gate);check(h&&!requestCancellation(c,h,gate)&&!c);});
 test("cancel-during-transfer",[&]{Memory m("abc");m.after=[&](size_t){cancel=true;};rejects("CANCELLED",[&]{download(pin(),m,parent,cancel,ignore);});cancel=false;check(leftovers()==0);});
 test("cancel-during-verification",[&]{Memory m("abc");rejects("CANCELLED",[&]{download(pin(),m,parent,cancel,[&](Progress p){if(p.phase==Progress::Verifying)cancel=true;});});cancel=false;check(leftovers()==0);});
 test("timeout-cleans-partial-file",[&]{Memory m("abc");m.chunk=1;m.after=[](size_t n){if(n==2)throw Failure("NETWORK_READ",ERROR_WINHTTP_TIMEOUT);};rejects("NETWORK_READ",[&]{download(pin(),m,parent,cancel,ignore);});check(leftovers()==0);});
 test("retry-uses-clean-new-transfer",[&]{Memory bad("abd");rejects("DOWNLOAD_HASH",[&]{download(pin(),bad,parent,cancel,ignore);});Memory good("abc");auto d=download(pin(),good,parent,cancel,ignore);check(d->verified);});
 test("progress-observed-through-verification",[&]{Memory m("abc");m.chunk=1;std::vector<Progress> states;auto d=download(pin(),m,parent,cancel,[&](Progress p){states.push_back(p);});check(states.size()>=5&&states[0].current==1&&states.back().phase==Progress::Verifying&&states.back().current==3);});
 test("verified-file-denies-write-delete",[&]{Memory m("abc");auto d=download(pin(),m,parent,cancel,ignore);Handle w(CreateFileW(d->path.c_str(),GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,0,nullptr));check(!w);check(!DeleteFileW(d->path.c_str()));});
 test("parent-rename-is-denied",[&]{Memory m("abc");auto d=download(pin(),m,parent,cancel,ignore);check(!MoveFileW(d->directory.c_str(),(d->directory+L"-moved").c_str()));});
 test("unverified-file-never-launches",[&]{PrivateFile d(parent);rejects("UNVERIFIED_LAUNCH",[&]{launch(d);});});
 test("write-failure-is-reported-and-cleaned",[&]{{PrivateFile d(parent);d.file.reset();rejects("DISK_WRITE",[&]{const unsigned char b=1;d.write(&b,1);});}check(leftovers()==0);});
 test("hardlinked-file-refused",[&]{PrivateFile d(parent);auto extra=d.directory+L"\\extra";check(CreateHardLinkW(extra.c_str(),d.path.c_str(),nullptr)!=FALSE);rejects("FILE_CHANGED",[&]{d.seal();});d.file.reset();check(DeleteFileW(extra.c_str())!=FALSE);});
 test("relative-parent-refused",[]{rejects("LOCAL_PATH",[]{pinDirectories(L"relative");});});
 test("unc-parent-refused",[]{rejects("LOCAL_PATH",[]{pinDirectories(L"\\\\server\\share");});});
 test("clean-environment-drops-hooks",[&]{SetEnvironmentVariableW(L"NODE_OPTIONS",L"--require malicious");SetEnvironmentVariableW(L"DOTNET_STARTUP_HOOKS",L"malicious");auto b=cleanEnvironment(parent);std::wstring all(b.begin(),b.end());check(all.find(L"NODE_OPTIONS")==std::wstring::npos&&all.find(L"DOTNET_STARTUP_HOOKS")==std::wstring::npos&&all.find(L"PATH=")!=std::wstring::npos);});
 test("actual-verified-harmless-child-handoff",[&]{auto bytes=readFile(argv[2]);Pin p{L"https://fixture.invalid/harmless.exe",bytes.size(),digest(bytes),L"test-only"};Memory m(bytes);m.chunk=65536;auto d=download(p,m,parent,cancel,ignore);auto child=launch(*d);check(WaitForSingleObject(child.h,15000)==WAIT_OBJECT_0);DWORD code;check(GetExitCodeProcess(child.h,&code)!=FALSE&&code==0);auto result=d->directory+L"\\clean.txt";check(readFile(result)==std::vector<unsigned char>({'o','k'}));check(DeleteFileW(result.c_str())!=FALSE);});
 check(leftovers()==0);std::cout<<"{\"passed\":"<<passed<<",\"failed\":"<<failed<<",\"networkUsed\":false,\"servicesInstalled\":false}\n";
 }catch(const std::exception& e){std::cout<<"HARNESS FAILURE: "<<e.what()<<"\n";return 2;}CoUninitialize();return failed?1:0;
}
