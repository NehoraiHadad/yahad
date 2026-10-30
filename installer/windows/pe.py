"""Bounded PE import inspection. No LoadLibrary or executable invocation."""
import struct
import os

class PEError(ValueError): pass

def inspect(path):
    if os.path.getsize(path) > 512 * 1024 ** 2: raise PEError('PE size')
    with open(path, 'rb') as stream: data = stream.read()
    def span(offset, size):
        if offset < 0 or size < 0 or offset + size > len(data): raise PEError('PE bounds')
        return data[offset:offset + size]
    def word(offset): return struct.unpack('<H', span(offset, 2))[0]
    def dword(offset): return struct.unpack('<I', span(offset, 4))[0]
    if span(0, 2) != b'MZ': raise PEError('PE DOS header')
    pe = dword(0x3c)
    if span(pe, 4) != b'PE\0\0': raise PEError('PE signature')
    machine, count, optional_size = word(pe + 4), word(pe + 6), word(pe + 20)
    if count < 1 or count > 96: raise PEError('PE section count')
    optional, magic = pe + 24, word(pe + 24)
    if magic not in (0x20b, 0x10b): raise PEError('PE optional header')
    directory_offset = 112 if magic == 0x20b else 96
    span(optional, optional_size)
    directory_count = dword(optional + directory_offset - 4)
    if directory_count > 32 or optional_size < directory_offset + directory_count * 8: raise PEError('PE directories')
    sections = []
    for i in range(count):
        entry = optional + optional_size + i * 40
        span(entry, 40)
        size, address, raw_size, raw = (dword(entry + n) for n in (8, 12, 16, 20))
        span(raw, raw_size); sections.append((address, max(size, raw_size), raw, raw_size))
    def offset(rva, size=1):
        if rva < dword(optional + 60): span(rva, size); return rva
        matches = [(raw + rva - address, raw_size - (rva-address)) for address, length, raw, raw_size in sections if address <= rva < address + length]
        if len(matches) != 1 or matches[0][1] < size: raise PEError('PE RVA')
        span(matches[0][0], size); return matches[0][0]
    def directory(index):
        return (dword(optional + directory_offset + index * 8), dword(optional + directory_offset + index * 8 + 4)) if index < directory_count else (0, 0)
    def string(rva):
        start = offset(rva); end = data.find(b'\0', start, min(len(data), start + 260))
        if end < 0: raise PEError('PE import name')
        try: name = data[start:end].decode('ascii').lower()
        except UnicodeDecodeError: raise PEError('PE import name')
        if not name or any(c not in 'abcdefghijklmnopqrstuvwxyz0123456789._-' for c in name) or '/' in name or '\\' in name: raise PEError('PE import path')
        return name
    clr_rva, clr_size = directory(14)
    managed = False
    if clr_rva:
        if clr_size < 20: raise PEError('PE CLR header')
        flags = dword(offset(clr_rva, 20) + 16)
        managed = bool(flags & 1) and not flags & 2
    if machine != 0x8664 and not (machine == 0x14c and managed and magic == 0x10b): raise PEError('PE is not x64 or AnyCPU IL')
    imports = set()
    # Pure IL assemblies are resolved by the private CLR, not loaded as native
    # libraries through their legacy mscoree import stubs.
    if not managed:
        rva, size = directory(1)
        if rva:
            if size < 20 or size > 1024 * 1024: raise PEError('PE import table')
            for i in range(min(size // 20, 4096)):
                entry = offset(rva + i * 20, 20)
                if span(entry, 20) == bytes(20): break
                imports.add(string(dword(entry + 12)))
            else: raise PEError('PE unterminated imports')
        rva, size = directory(13)
        if rva:
            if size < 32 or size > 1024 * 1024: raise PEError('PE delay table')
            for i in range(min(size // 32, 4096)):
                entry = offset(rva + i * 32, 32)
                if span(entry, 32) == bytes(32): break
                if dword(entry) != 1: raise PEError('PE delay VA form unsupported')
                imports.add(string(dword(entry + 4)))
            else: raise PEError('PE unterminated delay imports')
    return {'machine': 'anycpu-il' if managed else 'x64', 'managed': managed, 'imports': sorted(imports)}

# Explicit Windows desktop OS contracts, not a lookup in the developer's PATH
# or installed DLL directories. VC redistributables are intentionally excluded.
SYSTEM_DLLS = set('advapi32.dll avicap32.dll avrt.dll bcrypt.dll bcryptprimitives.dll cabinet.dll cfgmgr32.dll combase.dll comctl32.dll comdlg32.dll crypt32.dll cryptbase.dll cryptnet.dll cryptsp.dll dbghelp.dll dnsapi.dll dwrite.dll dwmapi.dll dxgi.dll d3d9.dll d3d11.dll d2d1.dll gdi32.dll hid.dll imagehlp.dll imm32.dll iphlpapi.dll kernel32.dll kernelbase.dll mpr.dll msimg32.dll msvcrt.dll ncrypt.dll netapi32.dll normaliz.dll ntdll.dll ole32.dll oleacc.dll oleaut32.dll powrprof.dll propsys.dll psapi.dll rasapi32.dll rpcrt4.dll secur32.dll setupapi.dll shell32.dll shlwapi.dll sspicli.dll ucrtbase.dll user32.dll userenv.dll usp10.dll version.dll vfw32.dll windowscodecs.dll winhttp.dll wininet.dll winmm.dll winspool.drv wintrust.dll wldap32.dll ws2_32.dll wtsapi32.dll'.split())

def is_system(name):
    return name in SYSTEM_DLLS or name.startswith(('api-ms-win-', 'ext-ms-win-')) and name.endswith('.dll')

# windowscodecs.dll: Windows Imaging Component, built into supported Windows.
# https://learn.microsoft.com/en-us/windows/win32/wic/-wic-api
