import tempfile
import os
import unittest
from pathlib import Path
from installer.windows.first_install_package import source_database_schema, require_source_database_schema


class SourceSchemaTests(unittest.TestCase):
    def source(self, text):
        root = Path(self.temporary.name)
        (root / 'src').mkdir(exist_ok=True)
        (root / 'src' / 'database-schema.ts').write_text(text, encoding='utf-8')
        # Store imports the constant; it deliberately has no numeric declaration.
        (root / 'src' / 'store.ts').write_text("import { DATABASE_SCHEMA } from './db.js';", encoding='utf-8')
        return root

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='yahad-lan-schema-',dir=os.environ.get('YAHAD_TEST_TEMP'))
        self.addCleanup(self.temporary.cleanup)

    def test_shared_schema_five_and_six_are_derived_without_store_literal(self):
        for schema in (5, 6):
            self.assertEqual(source_database_schema(self.source(f'export const DATABASE_SCHEMA = {schema};\n')), schema)

    def test_missing_duplicate_or_out_of_bounds_schema_is_refused(self):
        for text in ('', 'export const DATABASE_SCHEMA = 0;', 'export const DATABASE_SCHEMA = 65;',
                     'export const DATABASE_SCHEMA = 6;\nexport const DATABASE_SCHEMA = 5;',
                     '// export const DATABASE_SCHEMA = 6;'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                source_database_schema(self.source(text))

    def test_native_payload_metadata_cannot_disagree_with_the_shared_source(self):
        source=self.source('export const DATABASE_SCHEMA = 6;\n')
        self.assertEqual(require_source_database_schema({'databaseSchema':6},source),6)
        for schema in (5,7,None,'6',True):
            with self.subTest(schema=schema),self.assertRaises(ValueError):
                require_source_database_schema({'databaseSchema':schema},source)


if __name__ == '__main__':
    unittest.main()
