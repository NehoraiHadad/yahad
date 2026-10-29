#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <winhttp.h>
#include <bcrypt.h>
#include <sddl.h>
#include <shlobj.h>
#include <string>
#include <vector>
#include <memory>
#include <functional>
#include <atomic>
#include <stdexcept>
#include <algorithm>
#include <cstdint>
#include <mutex>

namespace yahad {
struct Failure:std::runtime_error { DWORD os; explicit Failure(const char* code,DWORD e=0):std::runtime_error(code),os(e){} };
inline void require(bool ok,const char* code){if(!ok)throw Failure(code,GetLastError());}
struct Handle {
 HANDLE h=INVALID_HANDLE_VALUE;
 Handle()=default;explicit Handle(HANDLE v):h(v){} ~Handle(){reset();}
 Handle(const Handle&)=delete;Handle& operator=(const Handle&)=delete;
 Handle(Handle&& o) noexcept:h(o.h){o.h=INVALID_HANDLE_VALUE;}
 Handle& operator=(Handle&& o) noexcept{if(this!=&o){reset();h=o.h;o.h=INVALID_HANDLE_VALUE;}return *this;}
 void reset(){if(h&&h!=INVALID_HANDLE_VALUE)CloseHandle(h);h=INVALID_HANDLE_VALUE;}
 explicit operator bool()const{return h&&h!=INVALID_HANDLE_VALUE;}
};
struct Internet {
 HINTERNET h=nullptr;explicit Internet(HINTERNET v=nullptr):h(v){}~Internet(){if(h)WinHttpCloseHandle(h);}
 Internet(const Internet&)=delete;Internet& operator=(const Internet&)=delete;
};
struct Pin { std::wstring url;uint64_t bytes;std::string sha;std::wstring version; };
struct Progress { enum Phase{Connecting,Downloading,Verifying,Handoff} phase;uint64_t current,total; };
using Notify=std::function<void(Progress)>;
inline void cancelled(const std::atomic_bool& c){if(c.load())throw Failure("CANCELLED");}
inline bool requestCancellation(std::atomic_bool& cancel,std::atomic_bool& handoff,std::mutex& gate){std::lock_guard<std::mutex> guard(gate);if(handoff.load())return false;cancel=true;return true;}
inline void claimHandoff(std::atomic_bool& cancel,std::atomic_bool& handoff,std::mutex& gate){std::lock_guard<std::mutex> guard(gate);cancelled(cancel);handoff=true;}
inline std::wstring known(REFKNOWNFOLDERID id){PWSTR p=nullptr;require(SUCCEEDED(SHGetKnownFolderPath(id,0,nullptr,&p)),"LOCAL_PATH");std::wstring s(p);CoTaskMemFree(p);return s;}
inline std::wstring windows(){wchar_t p[MAX_PATH+1];auto n=GetWindowsDirectoryW(p,MAX_PATH);require(n>0&&n<MAX_PATH,"WINDOWS_PATH");return std::wstring(p,n);}
inline std::wstring lower(std::wstring s){for(auto& c:s)if(c>=L'A'&&c<=L'Z')c+=32;return s;}
struct Url {std::wstring host,path;INTERNET_PORT port;};
inline bool allowedRedirect(const std::wstring& origin,const std::wstring& target,unsigned count){return count<3&&(target==origin||(origin==L"github.com"&&target==L"release-assets.githubusercontent.com"));}
inline Url parseUrl(const std::wstring& s){
 require(!s.empty()&&s.size()<=4096&&s.find_first_of(L"\r\n\t#\\") == std::wstring::npos,"DOWNLOAD_URL");
 URL_COMPONENTS c{};c.dwStructSize=sizeof(c);c.dwSchemeLength=c.dwHostNameLength=c.dwUrlPathLength=c.dwExtraInfoLength=c.dwUserNameLength=c.dwPasswordLength=(DWORD)-1;
 require(WinHttpCrackUrl(s.c_str(),(DWORD)s.size(),ICU_REJECT_USERPWD,&c)!=FALSE,"DOWNLOAD_URL");
 require(c.nScheme==INTERNET_SCHEME_HTTPS&&c.nPort==443&&c.dwHostNameLength>0&&c.dwUserNameLength==0&&c.dwPasswordLength==0,"DOWNLOAD_URL");
 auto part=[](const wchar_t* p,DWORD n){return n?std::wstring(p,n):std::wstring();};
 Url u{lower(part(c.lpszHostName,c.dwHostNameLength)),part(c.lpszUrlPath,c.dwUrlPathLength)+part(c.lpszExtraInfo,c.dwExtraInfoLength),c.nPort};
 require(u.host.find_first_not_of(L"abcdefghijklmnopqrstuvwxyz0123456789.-")==std::wstring::npos,"DOWNLOAD_URL");if(u.path.empty())u.path=L"/";return u;
}
inline void validatePin(const Pin& p){
 require(p.bytes>0&&p.bytes<=1024ULL*1024*1024&&p.sha.size()==64,"PAYLOAD_PIN");
 require(p.sha.find_first_not_of("0123456789abcdef")==std::string::npos&&!p.version.empty(),"PAYLOAD_PIN");parseUrl(p.url);
}
class Reader {public:virtual ~Reader()=default;virtual size_t read(unsigned char*,size_t)=0;virtual uint64_t length()const=0;};
class HttpReader final:public Reader {
 Internet session,connection,request;uint64_t size=0;
 public:HttpReader(const Pin& pin,const std::atomic_bool& cancel):session(WinHttpOpen(L"Yahad.Setup.Web/0.5.3",WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,WINHTTP_NO_PROXY_NAME,WINHTTP_NO_PROXY_BYPASS,0)){
   validatePin(pin);cancelled(cancel);require(session.h!=nullptr,"NETWORK_OPEN");require(WinHttpSetTimeouts(session.h,5000,5000,10000,10000)!=FALSE,"NETWORK_TIMEOUT_CONFIG");
   auto u=parseUrl(pin.url);std::wstring origin=u.host;
   for(unsigned redirect=0;;++redirect){
     cancelled(cancel);connection.h=WinHttpConnect(session.h,u.host.c_str(),u.port,0);require(connection.h!=nullptr,"NETWORK_CONNECT");
     request.h=WinHttpOpenRequest(connection.h,L"GET",u.path.c_str(),nullptr,WINHTTP_NO_REFERER,WINHTTP_DEFAULT_ACCEPT_TYPES,WINHTTP_FLAG_SECURE);require(request.h!=nullptr,"NETWORK_REQUEST");
     DWORD policy=WINHTTP_OPTION_REDIRECT_POLICY_NEVER;require(WinHttpSetOption(request.h,WINHTTP_OPTION_REDIRECT_POLICY,&policy,sizeof(policy))!=FALSE,"NETWORK_REDIRECT_POLICY");
     DWORD auth=WINHTTP_AUTOLOGON_SECURITY_LEVEL_HIGH;require(WinHttpSetOption(request.h,WINHTTP_OPTION_AUTOLOGON_POLICY,&auth,sizeof(auth))!=FALSE,"NETWORK_AUTH_POLICY");
     require(WinHttpSendRequest(request.h,L"Accept-Encoding: identity\r\n",(DWORD)-1,WINHTTP_NO_REQUEST_DATA,0,0,0)!=FALSE,"NETWORK_SEND");
     cancelled(cancel);
     require(WinHttpReceiveResponse(request.h,nullptr)!=FALSE,"NETWORK_RECEIVE");cancelled(cancel);
     DWORD code=0,n=sizeof(code);require(WinHttpQueryHeaders(request.h,WINHTTP_QUERY_STATUS_CODE|WINHTTP_QUERY_FLAG_NUMBER,WINHTTP_HEADER_NAME_BY_INDEX,&code,&n,WINHTTP_NO_HEADER_INDEX)!=FALSE,"NETWORK_STATUS");
     if(code==301||code==302||code==303||code==307||code==308){
       require(redirect<3,"REDIRECT_LIMIT");wchar_t target[4097];n=sizeof(target);require(WinHttpQueryHeaders(request.h,WINHTTP_QUERY_LOCATION,WINHTTP_HEADER_NAME_BY_INDEX,target,&n,WINHTTP_NO_HEADER_INDEX)!=FALSE,"REDIRECT_URL");
       u=parseUrl(target);require(allowedRedirect(origin,u.host,redirect),"REDIRECT_HOST");
       WinHttpCloseHandle(request.h);request.h=nullptr;WinHttpCloseHandle(connection.h);connection.h=nullptr;continue;
     }
     require(code==200,code==407?"PROXY_AUTH":code==404?"NOT_PUBLISHED":"HTTP_STATUS");
     wchar_t header[128];n=sizeof(header);
     if(WinHttpQueryHeaders(request.h,WINHTTP_QUERY_CONTENT_ENCODING,WINHTTP_HEADER_NAME_BY_INDEX,header,&n,WINHTTP_NO_HEADER_INDEX))require(lower(header)==L"identity","CONTENT_ENCODING");
     n=sizeof(header);if(WinHttpQueryHeaders(request.h,WINHTTP_QUERY_CONTENT_LENGTH,WINHTTP_HEADER_NAME_BY_INDEX,header,&n,WINHTTP_NO_HEADER_INDEX)){
       std::wstring v(header);require(!v.empty()&&v.find_first_not_of(L"0123456789")==std::wstring::npos&&v.size()<12,"CONTENT_LENGTH");size=std::stoull(v);require(size==pin.bytes,"CONTENT_LENGTH");
     }else require(GetLastError()==ERROR_WINHTTP_HEADER_NOT_FOUND,"CONTENT_LENGTH");
     break;
   }
 }
 uint64_t length()const override{return size;}
 size_t read(unsigned char* b,size_t capacity)override{DWORD got=0;require(WinHttpReadData(request.h,b,(DWORD)capacity,&got)!=FALSE,"NETWORK_READ");return got;}
};
struct Sha {
 BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;std::vector<unsigned char> object;
 Sha(){require(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0,"HASH_INIT");DWORD bytes=0,n=0;require(BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,(PUCHAR)&bytes,sizeof(bytes),&n,0)>=0,"HASH_INIT");object.resize(bytes);require(BCryptCreateHash(algorithm,&hash,object.data(),bytes,nullptr,0,0)>=0,"HASH_INIT");}
 ~Sha(){if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);}
 void add(const unsigned char* p,size_t n){require(BCryptHashData(hash,const_cast<PUCHAR>(p),(ULONG)n,0)>=0,"HASH_READ");}
 std::string finish(){unsigned char out[32];require(BCryptFinishHash(hash,out,sizeof(out),0)>=0,"HASH_FINISH");std::string s;for(auto b:out){s+="0123456789abcdef"[b>>4];s+="0123456789abcdef"[b&15];}return s;}
};
inline std::wstring sid(){Handle t;require(OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&t.h)!=FALSE,"OWNER_TOKEN");DWORD n=0;GetTokenInformation(t.h,TokenUser,nullptr,0,&n);std::vector<unsigned char> b(n);require(GetTokenInformation(t.h,TokenUser,b.data(),n,&n)!=FALSE,"OWNER_TOKEN");PWSTR p=nullptr;require(ConvertSidToStringSidW(((TOKEN_USER*)b.data())->User.Sid,&p)!=FALSE,"OWNER_TOKEN");std::wstring value(p);LocalFree(p);return value;}
inline std::vector<Handle> pinDirectories(const std::wstring& path){
 require(path.size()>3&&path[1]==L':'&&path[2]==L'\\'&&path.find(L"..") == std::wstring::npos,"LOCAL_PATH");std::vector<Handle> pins;
 auto volume=path.substr(0,3);DWORD flags=0;require(GetDriveTypeW(volume.c_str())==DRIVE_FIXED&&GetVolumeInformationW(volume.c_str(),nullptr,0,nullptr,nullptr,&flags,nullptr,0)!=FALSE&&(flags&FILE_PERSISTENT_ACLS),"LOCAL_VOLUME");
 for(size_t end=3;;){auto part=path.substr(0,end);Handle h(CreateFileW(part.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT,nullptr));require((bool)h,"LOCAL_DIRECTORY");BY_HANDLE_FILE_INFORMATION f{};require(GetFileInformationByHandle(h.h,&f)!=FALSE&&(f.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)&&!(f.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT),"LOCAL_REPARSE");pins.push_back(std::move(h));if(end==path.size())break;auto next=path.find(L'\\',end+1);end=next==std::wstring::npos?path.size():next;}
 return pins;
}
struct PrivateFile {
 std::wstring directory,path;std::vector<Handle> directories;Handle file;bool verified=false,directoryCreated=false,fileCreated=false;
 explicit PrivateFile(const std::wstring& parent){
  try{
   directories=pinDirectories(parent);GUID id{};require(SUCCEEDED(CoCreateGuid(&id)),"TEMP_NAME");wchar_t value[40];StringFromGUID2(id,value,40);directory=parent+L"\\Yahad.Download-"+value;
   auto owner=sid();auto sddl=L"O:"+owner+L"D:P(A;;FA;;;SY)(A;;FA;;;"+owner+L")";PSECURITY_DESCRIPTOR sd=nullptr;require(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&sd,nullptr)!=FALSE,"TEMP_SECURITY");SECURITY_ATTRIBUTES sa{sizeof(sa),sd,FALSE};
   auto made=CreateDirectoryW(directory.c_str(),&sa);LocalFree(sd);require(made!=FALSE,"TEMP_CREATE");directoryCreated=true;
   auto held=pinDirectories(directory);for(auto& h:held)directories.push_back(std::move(h));path=directory+L"\\Yahad.Setup.exe";
   file=Handle(CreateFileW(path.c_str(),GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr));require((bool)file,"FILE_CREATE");fileCreated=true;
  }catch(...){file.reset();if(fileCreated)DeleteFileW(path.c_str());directories.clear();if(directoryCreated)RemoveDirectoryW(directory.c_str());throw;}
 }
 ~PrivateFile(){file.reset();if(fileCreated)DeleteFileW(path.c_str());directories.clear();if(directoryCreated)RemoveDirectoryW(directory.c_str());}
 void write(const unsigned char* b,size_t n){DWORD done=0;require(WriteFile(file.h,b,(DWORD)n,&done,nullptr)!=FALSE&&done==n,"DISK_WRITE");}
 void seal(){require(FlushFileBuffers(file.h)!=FALSE,"DISK_FLUSH");file.reset();file=Handle(CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr));require((bool)file,"FILE_SEAL");BY_HANDLE_FILE_INFORMATION f{};require(GetFileInformationByHandle(file.h,&f)!=FALSE&&f.nNumberOfLinks==1&&!(f.dwFileAttributes&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_DIRECTORY)),"FILE_CHANGED");}
};
inline void checkSpace(const std::wstring& parent,uint64_t bytes){ULARGE_INTEGER free{};require(GetDiskFreeSpaceExW(parent.c_str(),&free,nullptr,nullptr)!=FALSE,"DISK_SPACE");require(free.QuadPart>bytes+64ULL*1024*1024,"DISK_FULL");}
inline std::unique_ptr<PrivateFile> download(const Pin& pin,Reader& reader,const std::wstring& parent,const std::atomic_bool& cancel,const Notify& notify){
 validatePin(pin);cancelled(cancel);checkSpace(parent,pin.bytes);require(reader.length()==0||reader.length()==pin.bytes,"CONTENT_LENGTH");auto result=std::make_unique<PrivateFile>(parent);std::vector<unsigned char> buffer(65536);uint64_t total=0;
 const auto began=GetTickCount64();
 for(;;){cancelled(cancel);require(GetTickCount64()-began<3600000,"NETWORK_DEADLINE");size_t n=reader.read(buffer.data(),buffer.size());cancelled(cancel);if(!n)break;require(n<=buffer.size()&&n<=pin.bytes-total,"DOWNLOAD_SIZE");result->write(buffer.data(),n);total+=n;notify({Progress::Downloading,total,pin.bytes});}
 require(total==pin.bytes,"DOWNLOAD_INCOMPLETE");result->seal();cancelled(cancel);notify({Progress::Verifying,0,pin.bytes});Sha hash;uint64_t read=0;
 for(;;){cancelled(cancel);DWORD n=0;require(ReadFile(result->file.h,buffer.data(),(DWORD)buffer.size(),&n,nullptr)!=FALSE,"VERIFY_READ");if(!n)break;read+=n;require(read<=pin.bytes,"DOWNLOAD_SIZE");hash.add(buffer.data(),n);notify({Progress::Verifying,read,pin.bytes});}
 require(read==pin.bytes&&hash.finish()==pin.sha,"DOWNLOAD_HASH");cancelled(cancel);result->verified=true;return result;
}
inline std::vector<wchar_t> cleanEnvironment(const std::wstring& temp){
 auto win=windows();std::vector<std::pair<std::wstring,std::wstring>> vars={{L"APPDATA",known(FOLDERID_RoamingAppData)},{L"LOCALAPPDATA",known(FOLDERID_LocalAppData)},{L"PATH",win+L"\\System32;"+win},{L"ProgramData",known(FOLDERID_ProgramData)},{L"ProgramFiles",known(FOLDERID_ProgramFiles)},{L"SystemRoot",win},{L"TEMP",temp},{L"TMP",temp},{L"USERPROFILE",known(FOLDERID_Profile)},{L"WINDIR",win}};
 std::sort(vars.begin(),vars.end(),[](auto& a,auto& b){return lower(a.first)<lower(b.first);});std::vector<wchar_t> block;for(auto& pair:vars){auto s=pair.first+L"="+pair.second;block.insert(block.end(),s.begin(),s.end());block.push_back(0);}block.push_back(0);return block;
}
inline Handle launch(const PrivateFile& payload){
 require(payload.verified&&(bool)payload.file,"UNVERIFIED_LAUNCH");
 auto env=cleanEnvironment(payload.directory);auto command=L"\""+payload.path+L"\"";std::vector<wchar_t> mutableCommand(command.begin(),command.end());mutableCommand.push_back(0);STARTUPINFOW si{};si.cb=sizeof(si);PROCESS_INFORMATION pi{};
 require(CreateProcessW(payload.path.c_str(),mutableCommand.data(),nullptr,nullptr,FALSE,CREATE_UNICODE_ENVIRONMENT,env.data(),payload.directory.c_str(),&si,&pi)!=FALSE,"INSTALLER_START");CloseHandle(pi.hThread);return Handle(pi.hProcess);
}
}
