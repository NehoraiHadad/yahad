"""Inspect native launcher dependencies and early DLL search hardening."""
import struct
from pathlib import Path
def inspect(path):
    b=Path(path).read_bytes();u16=lambda p:struct.unpack_from('<H',b,p)[0];u32=lambda p:struct.unpack_from('<I',b,p)[0]
    pe=u32(0x3c)
    if b[pe:pe+4]!=b'PE\0\0' or u16(pe+4)!=0x8664:raise ValueError('Windows x64 required')
    optional=pe+24
    if u16(optional)!=0x20b:raise ValueError('PE32+ required')
    section=optional+u16(pe+20);sections=[]
    for n in range(u16(pe+6)):
        s=section+n*40;sections.append((u32(s+12),max(u32(s+8),u32(s+16)),u32(s+20)))
    def offset(rva):
        for start,size,raw in sections:
            if start<=rva<start+size:return raw+rva-start
        raise ValueError('Unmapped RVA')
    def string(rva):
        p=offset(rva);end=b.index(b'\0',p)
        return b[p:end].decode('ascii').lower()
    directory=optional+112;imports=[];rva=u32(directory+8)
    if rva:
        p=offset(rva)
        while any(b[p:p+20]):imports.append(string(u32(p+12)));p+=20
    stock={'advapi32.dll','bcrypt.dll','comctl32.dll','gdi32.dll','kernel32.dll','msvcrt.dll','ole32.dll','shell32.dll','user32.dll','winhttp.dll','ntdll.dll','ws2_32.dll'}
    if any(n not in stock and not n.startswith(('api-ms-win-','ext-ms-win-')) for n in imports):raise ValueError('Non-OS runtime imported: '+repr(imports))
    if not {'winhttp.dll','bcrypt.dll','advapi32.dll'}<=set(imports):raise ValueError('Concrete download, hash and private-file adapters must be linked even before distribution URL configuration')
    load=u32(directory+10*8)
    if not load:raise ValueError('Missing early DLL load policy')
    dependent=u16(offset(load)+78)
    if dependent!=0x800:raise ValueError('System32-only initial DLL policy required')
    characteristics=u16(optional+70)
    if characteristics&0x160!=0x160:raise ValueError('ASLR, high-entropy VA and NX required')
    if b'level="asInvoker"' not in b:raise ValueError('Explicit unelevated manifest required')
    if b'0ae76fb89c2df50ad68cc1b635d06003a893b5613f01aa9b9a4ba146269edf37' not in b:raise ValueError('Missing pinned payload identity')
    return {'machine':'AMD64','imports':imports,'initialDllSearchFlags':dependent,'asInvoker':True,'aslr':True,'nx':True,'highEntropyVa':True,'privateRuntimeRequired':False}
