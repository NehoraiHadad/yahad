"""Generate a plan only. No listener, firewall, CA, trust or DNS operation."""
from ipaddress import IPv4Address, IPv4Network


def caddyfile(address, backend_port):
    ip=IPv4Address(address)
    if str(ip)!=address or not any(ip in IPv4Network(n) for n in ('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16')):
        raise ValueError('Explicit canonical private LAN address required')
    if type(backend_port) is not int or not 1024<=backend_port<=65535: raise ValueError('Private backend port required')
    return ('{\n    admin off\n    auto_https disable_redirects\n    skip_install_trust\n}\n'
        f'https://{address} {{\n    bind {address}\n    tls internal\n'
        f'    reverse_proxy 127.0.0.1:{backend_port}\n}}\n')
