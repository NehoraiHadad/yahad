#include "core.hpp"
#include "payload_pin.hpp"
#include <commctrl.h>
#include <shellapi.h>
#include <thread>
#include <mutex>
#include <sstream>
#include <iomanip>

using namespace yahad;
namespace {
constexpr UINT UPDATE=WM_APP+1,DONE=WM_APP+2;
HWND window,primary,secondary;HFONT normal,bold,title,brand,small;std::thread worker;std::atomic_bool cancelRequested=false,handoffStarted=false;std::mutex handoffGate;
std::wstring payloadUrl;
std::wstring loadUrl(HINSTANCE instance){auto resource=FindResourceW(instance,MAKEINTRESOURCEW(102),RT_RCDATA);require(resource!=nullptr,"BOOTSTRAP_CONFIG");auto bytes=SizeofResource(instance,resource);require(bytes>=2&&bytes<=8192&&bytes%2==0,"BOOTSTRAP_CONFIG");auto data=(const wchar_t*)LockResource(LoadResource(instance,resource));require(data&&data[bytes/2-1]==0,"BOOTSTRAP_CONFIG");std::wstring result(data,bytes/2-1);require(result.find(L'\0')==std::wstring::npos,"BOOTSTRAP_CONFIG");if(!result.empty())parseUrl(result);return result;}
std::mutex stateMutex;Progress progress{Progress::Connecting,0,PAYLOAD_BYTES};std::wstring status;bool busy=false,handed=false,closing=false,failed=false;
const COLORREF ink=RGB(25,57,48),green=RGB(30,86,66),muted=RGB(90,105,99),paper=RGB(247,248,244);
void text(HDC dc,const std::wstring& value,RECT r,HFONT font,COLORREF color,UINT flags=DT_RIGHT|DT_RTLREADING|DT_WORDBREAK){SelectObject(dc,font);SetTextColor(dc,color);SetBkMode(dc,TRANSPARENT);DrawTextW(dc,value.c_str(),(int)value.size(),&r,flags|DT_NOPREFIX);}
void fill(HDC dc,RECT r,COLORREF color){auto b=CreateSolidBrush(color);FillRect(dc,&r,b);DeleteObject(b);}
void round(HDC dc,RECT r,COLORREF color,int radius=20){auto b=CreateSolidBrush(color);auto old=SelectObject(dc,b);auto pen=SelectObject(dc,GetStockObject(NULL_PEN));RoundRect(dc,r.left,r.top,r.right,r.bottom,radius,radius);SelectObject(dc,old);SelectObject(dc,pen);DeleteObject(b);}
std::wstring mb(uint64_t n){std::wostringstream s;s<<std::fixed<<std::setprecision(1)<<double(n)/1000000.0;return s.str();}
std::wstring errorText(const Failure& e){
 std::string c=e.what();
 if(c=="CANCELLED")return L"ההורדה בוטלה. אפשר להתחיל שוב כשתרצו. שום רכיב לא הותקן.";
 if(c=="DISK_FULL"||c=="DISK_WRITE"||c=="DISK_SPACE"||c=="DISK_FLUSH")return L"אין מספיק מקום פנוי או שלא ניתן לשמור את הקובץ. פנו מקום ונסו שוב.";
 if(c=="DOWNLOAD_HASH"||c=="DOWNLOAD_SIZE"||c=="CONTENT_LENGTH"||c=="DOWNLOAD_INCOMPLETE"||c=="CONTENT_ENCODING")return L"הקובץ שהתקבל אינו תואם לגרסה המאומתת. הוא לא יופעל. נסו להוריד שוב.";
 if(c=="NOT_PUBLISHED")return L"חבילת ההתקנה אינה זמינה כרגע בשרת. נסו שוב מאוחר יותר או השתמשו במתקין המלא.";
 if(c=="PROXY_AUTH")return L"הרשת דורשת כניסה דרך שרת מתווך. בדקו את הגדרות הרשת או השתמשו במתקין המלא.";
 if(c=="INSTALLER_START")return L"Windows לא הצליח לפתוח את אשף ההתקנה. ייתכן שתוכנת האבטחה חסמה אותו. לא הותקן שירות; אפשר לנסות שוב.";
 if(c.rfind("NETWORK_",0)==0||c.rfind("HTTP_",0)==0||c.rfind("REDIRECT_",0)==0)return L"ההורדה לא הושלמה. בדקו חיבור לאינטרנט ונסו שוב. בעיית תעודה או הפניה לא מאושרת לא תיעקף.";
 return L"לא ניתן להשלים את ההורדה בבטחה. הקובץ לא יופעל. נסו שוב או השתמשו במתקין המלא.";
}
void layout(){RECT r;GetClientRect(window,&r);int left=34,right=r.right-276;MoveWindow(primary,right-190,r.bottom-72,190,44,TRUE);MoveWindow(secondary,left,r.bottom-72,110,44,TRUE);}
void paint(HDC dc){
 RECT client;GetClientRect(window,&client);int w=client.right,h=client.bottom,edge=w-244,contentRight=edge-32;fill(dc,client,paper);fill(dc,{edge,0,w,h},ink);
 round(dc,{w-172,40,w-72,116},RGB(66,110,90),28);text(dc,L"יחד",{w-174,52,w-72,100},brand,RGB(255,255,255),DT_CENTER|DT_RTLREADING|DT_SINGLELINE);
 text(dc,L"מתקינים בקלות.\nממשיכים ביחד.",{edge+26,148,w-26,260},title,RGB(255,255,255));
 text(dc,L"1   הורדה ואימות\n\n2   התקנת השירות\n\n3   פתיחת יחד",{edge+26,292,w-26,460},normal,RGB(212,228,216));
 text(dc,L"Windows x64  ·  גרסה 0.5.3",{edge+20,h-58,w-20,h-25},small,RGB(188,212,197),DT_CENTER|DT_RTLREADING|DT_SINGLELINE);
 text(dc,L"ברוכים הבאים ליחד",{34,32,contentRight,76},title,ink);
 text(dc,L"כל מה שצריך, באופן אוטומטי",{34,85,contentRight,120},bold,green);
 text(dc,L"האשף יוריד ויתקין את הרכיבים בעצמו. אין צורך להתקין תוכנות מראש או להקליד פקודות.",{34,123,contentRight,179},normal,muted);
 round(dc,{34,187,contentRight,365},RGB(255,255,255));
 text(dc,L"מה כלול בהתקנה?",{52,201,contentRight-18,230},bold,ink);
 text(dc,L"Node ו־.NET — מנועי הריצה הפרטיים של יחד\nFFmpeg — הודעות קוליות ותמונות מקדימות\nCaddy — כלול; אינו מופעל במסלול המקומי\nהאפליקציה ושירות Windows שפועל ברקע",{52,239,contentRight-18,355},normal,muted);
 text(dc,L"הורדה כוללת: "+mb(PAYLOAD_BYTES)+L" MB · נדרש חיבור לאינטרנט",{34,379,contentRight,408},bold,ink);
 text(dc,L"לפני התקנת השירות Windows יבקש הרשאת מנהל. לאחר ההתקנה ייפתח הווב להגדרות המשפחה. גרסת בדיקה זו מקומית בלבד, עם WhatsApp מדומה.",{34,414,contentRight,483},small,muted);
 Progress p;std::wstring message;{std::lock_guard<std::mutex> guard(stateMutex);p=progress;message=status;}
 if(message.empty())message=!payloadUrl.empty()?L"מוכנים להתחיל. ההורדה והאימות אינם דורשים הרשאת מנהל.":L"קובץ ההורדה מוכן לבדיקה, אך כתובת ההפצה טרם הוגדרה. לא תתבצע הורדה או התקנה.";
 int y=std::max(492,h-160);text(dc,message,{34,y,contentRight,y+54},small,failed?RGB(157,63,48):ink);
 if(busy&&!handed){int top=h-99;round(dc,{34,top,contentRight,top+7},RGB(222,229,221),6);int length=contentRight-34;int portion=p.total?(int)(length*std::min(p.current,p.total)/p.total):0;if(portion>0)round(dc,{contentRight-portion,top,contentRight,top+7},green,6);}
}
void setProgress(Progress p){
 {std::lock_guard<std::mutex> guard(stateMutex);progress=p;if(p.phase==Progress::Connecting)status=L"מתחברים לשרת ההורדה…";else if(p.phase==Progress::Downloading)status=L"מורידים את הרכיבים: "+mb(p.current)+L" מתוך "+mb(p.total)+L" MB";else if(p.phase==Progress::Verifying)status=L"מאמתים את שלמות הקובץ לפני הפעלה…";else status=L"הקובץ אומת. פותחים את אשף התקנת השירות…";}
 PostMessageW(window,UPDATE,0,0);
}
void start(){
 if(busy||payloadUrl.empty())return;if(worker.joinable())worker.join();cancelRequested=false;handoffStarted=false;busy=true;handed=false;failed=false;closing=false;EnableWindow(primary,FALSE);SetWindowTextW(secondary,L"ביטול");
 worker=std::thread([]{
   try{
     Pin pin{payloadUrl,PAYLOAD_BYTES,PAYLOAD_SHA,L"0.5.3"};setProgress({Progress::Connecting,0,pin.bytes});HttpReader source(pin,cancelRequested);
     auto payload=download(pin,source,known(FOLDERID_LocalAppData)+L"\\Temp",cancelRequested,setProgress);cancelled(cancelRequested);setProgress({Progress::Handoff,pin.bytes,pin.bytes});
     // The verified handle denies writes/deletion through handoff and lifetime of
     // the existing installer. No service operation is implemented here.
     claimHandoff(cancelRequested,handoffStarted,handoffGate);auto child=launch(*payload);PostMessageW(window,UPDATE,1,0);
     require(WaitForSingleObject(child.h,INFINITE)==WAIT_OBJECT_0,"INSTALLER_WAIT");
     {std::lock_guard<std::mutex> guard(stateMutex);status=L"אשף ההתקנה נסגר. תוצאת ההתקנה וכתובת הכניסה מוצגות באשף יחד; סגירתו לבדה אינה הוכחת הצלחה.";}
     PostMessageW(window,DONE,0,0);
   }catch(const Failure& e){{std::lock_guard<std::mutex> guard(stateMutex);status=errorText(cancelRequested&&!handoffStarted?Failure("CANCELLED"):e);}PostMessageW(window,DONE,1,0);}
    catch(...){{std::lock_guard<std::mutex> guard(stateMutex);status=L"אירעה שגיאה בהורדה. שום קובץ שלא אומת לא יופעל. אפשר לנסות שוב.";}PostMessageW(window,DONE,1,0);}
 });InvalidateRect(window,nullptr,FALSE);
}
LRESULT CALLBACK procedure(HWND h,UINT message,WPARAM wp,LPARAM lp){
 switch(message){
 case WM_CREATE:window=h;primary=CreateWindowExW(WS_EX_RTLREADING,L"BUTTON",L"הורדה והתקנה",WS_CHILD|WS_VISIBLE|BS_OWNERDRAW,0,0,0,0,h,(HMENU)1,nullptr,nullptr);secondary=CreateWindowExW(WS_EX_RTLREADING,L"BUTTON",L"סגירה",WS_CHILD|WS_VISIBLE|BS_OWNERDRAW,0,0,0,0,h,(HMENU)2,nullptr,nullptr);EnableWindow(primary,!payloadUrl.empty());return 0;
 case WM_SIZE:if(primary)layout();InvalidateRect(h,nullptr,FALSE);return 0;
 case WM_GETMINMAXINFO:((MINMAXINFO*)lp)->ptMinTrackSize={800,740};return 0;
 case WM_ERASEBKGND:return 1;
 case WM_PAINT:{PAINTSTRUCT p;auto dc=BeginPaint(h,&p);paint(dc);EndPaint(h,&p);return 0;}
 case WM_PRINTCLIENT:paint((HDC)wp);return 0;
 case WM_DRAWITEM:{auto p=(DRAWITEMSTRUCT*)lp;bool main=p->CtlID==1,disabled=(p->itemState&ODS_DISABLED)!=0;round(p->hDC,p->rcItem,main?(disabled?RGB(179,194,180):green):RGB(232,237,230),14);wchar_t caption[80];GetWindowTextW(p->hwndItem,caption,80);auto r=p->rcItem;r.top+=11;text(p->hDC,caption,r,bold,main?RGB(255,255,255):ink,DT_CENTER|DT_RTLREADING|DT_SINGLELINE);if(p->itemState&ODS_FOCUS){InflateRect(&r,-4,-4);DrawFocusRect(p->hDC,&r);}return TRUE;}
 case WM_COMMAND:if(LOWORD(wp)==1)start();else if(LOWORD(wp)==2){if(busy&&!handed&&requestCancellation(cancelRequested,handoffStarted,handoffGate)){EnableWindow(secondary,FALSE);std::lock_guard<std::mutex> guard(stateMutex);status=L"מבטלים את ההורדה ומנקים את הקובץ החלקי…";InvalidateRect(h,nullptr,FALSE);}else SendMessageW(h,WM_CLOSE,0,0);}return 0;
 case UPDATE:if(wp){handed=true;SetWindowTextW(secondary,L"סגירה");EnableWindow(secondary,TRUE);std::lock_guard<std::mutex> guard(stateMutex);status=L"אשף התקנת יחד נפתח. המשיכו בחלון שלו; הוא יציג את התקדמות ההתקנה ויפתח את הווב.";}InvalidateRect(h,nullptr,FALSE);return 0;
 case DONE:busy=false;failed=wp!=0;EnableWindow(primary,!payloadUrl.empty());EnableWindow(secondary,TRUE);SetWindowTextW(primary,failed?L"ניסיון חוזר":L"פתיחת האשף שוב");SetWindowTextW(secondary,L"סגירה");InvalidateRect(h,nullptr,FALSE);if(closing)DestroyWindow(h);return 0;
 case WM_CLOSE:if(busy&&!handed&&requestCancellation(cancelRequested,handoffStarted,handoffGate)){closing=true;EnableWindow(secondary,FALSE);return 0;}DestroyWindow(h);return 0;
 case WM_DESTROY:PostQuitMessage(0);return 0;
 }return DefWindowProcW(h,message,wp,lp);
}
void render(const std::wstring& path){
 RECT r;GetClientRect(window,&r);HDC display=GetDC(window),dc=CreateCompatibleDC(display);HBITMAP image=CreateCompatibleBitmap(display,r.right,r.bottom);auto old=SelectObject(dc,image);paint(dc);
 for(HWND child:{primary,secondary}){RECT cr;GetWindowRect(child,&cr);POINT p{cr.left,cr.top};ScreenToClient(window,&p);SetViewportOrgEx(dc,p.x,p.y,nullptr);SendMessageW(child,WM_PRINT,(WPARAM)dc,PRF_CLIENT);SetViewportOrgEx(dc,0,0,nullptr);}
 BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=r.right;info.bmiHeader.biHeight=-r.bottom;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;std::vector<unsigned char> pixels(r.right*r.bottom*4);SelectObject(dc,old);require(GetDIBits(display,image,0,r.bottom,pixels.data(),&info,DIB_RGB_COLORS)!=0,"RENDER");BITMAPFILEHEADER head{};head.bfType=0x4d42;head.bfOffBits=sizeof(head)+sizeof(info.bmiHeader);head.bfSize=head.bfOffBits+(DWORD)pixels.size();Handle f(CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));require((bool)f,"RENDER_FILE");DWORD n;WriteFile(f.h,&head,sizeof(head),&n,nullptr);WriteFile(f.h,&info.bmiHeader,sizeof(info.bmiHeader),&n,nullptr);WriteFile(f.h,pixels.data(),(DWORD)pixels.size(),&n,nullptr);DeleteObject(image);DeleteDC(dc);ReleaseDC(window,display);
}
}
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE,LPWSTR,int show){
 try{payloadUrl=loadUrl(instance);}catch(...){return 2;}
 int argc=0;auto argv=CommandLineToArgvW(GetCommandLineW(),&argc);
 if(argc==2&&std::wstring(argv[1])==L"--notices"){auto r=FindResourceW(instance,MAKEINTRESOURCEW(103),RT_RCDATA);if(!r)return 2;auto p=LockResource(LoadResource(instance,r));DWORD n;WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),p,SizeofResource(instance,r),&n,nullptr);LocalFree(argv);return 0;}
 if(argc==2&&std::wstring(argv[1])==L"--describe"){std::string output="{\"format\":1,\"version\":\"0.5.3\",\"payloadBytes\":"+std::to_string(PAYLOAD_BYTES)+",\"payloadSha256\":\""+PAYLOAD_SHA+"\",\"distributionConfigured\":"+(!payloadUrl.empty()?"true":"false")+",\"requiresPreinstalledRuntime\":false,\"hostInstallationTested\":false}\n";DWORD n;WriteFile(GetStdHandle(STD_OUTPUT_HANDLE),output.data(),(DWORD)output.size(),&n,nullptr);LocalFree(argv);return 0;}
 bool smallRender=argc==3&&std::wstring(argv[1])==L"--render-small";bool rendering=argc==3&&(std::wstring(argv[1])==L"--render"||smallRender);std::wstring image=rendering?argv[2]:L"";if(argc!=1&&!rendering){LocalFree(argv);return 2;}LocalFree(argv);
 CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);InitCommonControls();
 auto font=[](int size,int weight){return CreateFontW(-size,0,0,0,weight,FALSE,FALSE,FALSE,HEBREW_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,L"Segoe UI");};normal=font(17,FW_NORMAL);bold=font(17,FW_SEMIBOLD);title=font(28,FW_SEMIBOLD);brand=font(36,FW_SEMIBOLD);small=font(15,FW_NORMAL);
 WNDCLASSW c{};c.lpfnWndProc=procedure;c.hInstance=instance;c.lpszClassName=L"Yahad.NativeDownload";c.hCursor=LoadCursorW(nullptr,IDC_ARROW);RegisterClassW(&c);window=CreateWindowExW(WS_EX_RTLREADING,c.lpszClassName,L"יחד — התקנה אוטומטית",WS_OVERLAPPEDWINDOW,CW_USEDEFAULT,CW_USEDEFAULT,smallRender?800:930,smallRender?740:760,nullptr,nullptr,instance,nullptr);if(!window)return 3;layout();
 if(rendering){try{render(image);}catch(...){return 4;}DestroyWindow(window);}else{ShowWindow(window,show);UpdateWindow(window);MSG m;while(GetMessageW(&m,nullptr,0,0)>0){if(!IsDialogMessageW(window,&m)){TranslateMessage(&m);DispatchMessageW(&m);}}}
 if(worker.joinable())worker.join();for(auto f:{normal,bold,title,brand,small})DeleteObject(f);CoUninitialize();return 0;
}
