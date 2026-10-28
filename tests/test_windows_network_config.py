import unittest
from installer.windows.network_config import caddyfile


class NetworkConfigTests(unittest.TestCase):
    def test_private_only_config_explicitly_disables_trust_admin_and_redirect_listener(self):
        config=caddyfile('192.168.4.5',3000)
        for required in ('admin off','auto_https disable_redirects','skip_install_trust','bind 192.168.4.5','tls internal','reverse_proxy 127.0.0.1:3000'): self.assertIn(required,config)
        self.assertNotIn('on_demand',config)

    def test_public_loopback_and_noncanonical_addresses_refused(self):
        for address in ('8.8.8.8','127.0.0.1','0.0.0.0','169.254.1.2','::1','192.168.004.5','192.168.4.5\nadmin localhost:2019'):
            with self.subTest(address=address),self.assertRaises(ValueError): caddyfile(address,3000)

    def test_backend_port_cannot_create_an_extra_listener_or_inject_config(self):
        for port in (0,443,65536,True,'3000\n}'): 
            with self.subTest(port=port),self.assertRaises(ValueError): caddyfile('10.1.2.3',port)


if __name__=='__main__':unittest.main(verbosity=2)
