import json
import tempfile
import os
import unittest
from pathlib import Path
from installer.windows.prepare_core_fixture import stage_public_lan_help


class NativeLanAssetTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='yahad-lan-assets-',dir=os.environ.get('YAHAD_TEST_TEMP'))
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo = self.root/'repo'; self.repo.mkdir()
        self.app = self.root/'app'; self.app.mkdir()

    def assets(self):
        source = self.repo/'deploy'; source.mkdir()
        (source/'lan-help.html').write_text('<p>Public instructions only</p>', encoding='utf-8')
        (source/'lan-capabilities.json').write_text(json.dumps(dict(format=1, localHttps=True,
            exactAdditionalOrigins=True, ownerLoopbackBootstrap=True, publicHelpTemplate='deploy/lan-help.html')), encoding='utf-8')
        return source

    def test_only_public_whitelist_is_staged(self):
        source = self.assets()
        (source/'root.key').write_text('SYNTHETIC PRIVATE INPUT MUST NEVER BE COPIED', encoding='utf-8')
        (source/'configuration.env').write_text('SYNTHETIC ENV MUST NEVER BE COPIED', encoding='utf-8')
        self.assertEqual(stage_public_lan_help(self.repo,self.app), ['deploy/lan-help.html','deploy/lan-capabilities.json'])
        self.assertEqual({p.name for p in (self.app/'deploy').iterdir()}, {'lan-help.html','lan-capabilities.json'})

    def test_legacy_checkpoint_is_supported_but_half_contract_refuses(self):
        self.assertEqual(stage_public_lan_help(self.repo,self.app), [])
        source = self.repo/'deploy'; source.mkdir()
        (source/'lan-help.html').write_text('public',encoding='utf-8')
        with self.assertRaises(ValueError): stage_public_lan_help(self.repo,self.app)
        self.assertFalse((self.app/'deploy').exists())

    def test_wrong_marker_or_oversize_help_refuses_before_copy(self):
        source = self.assets()
        (source/'lan-capabilities.json').write_text('{}',encoding='utf-8')
        with self.assertRaises(ValueError): stage_public_lan_help(self.repo,self.app)
        (source/'lan-help.html').write_bytes(b'x'*65537)
        with self.assertRaises(ValueError): stage_public_lan_help(self.repo,self.app)
        self.assertFalse((self.app/'deploy').exists())


if __name__ == '__main__': unittest.main()
